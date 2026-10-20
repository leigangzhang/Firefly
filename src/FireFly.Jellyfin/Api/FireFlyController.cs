using System.Text.Json;
using FireFly.Core;
using FireFly.Core.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FireFly.Jellyfin.Api;

public sealed record SearchRequest(string? ItemId, string? Title, string? OriginalTitle, int? Year, MediaKind? Kind, int? Season, string? TmdbId, bool Refresh);

public sealed record SubmitRequest(MediaQuery Query, Release Release);

[ApiController]
[Route("FireFly")]
public sealed class FireFlyController : ControllerBase
{
    private const string Elevated = "RequiresElevation";
    private readonly FireFlyRuntime _runtime;
    private readonly ILibraryManager _library;

    public FireFlyController(FireFlyRuntime runtime, ILibraryManager library)
    {
        _runtime = runtime;
        _library = library;
    }

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 前端统一按 camelCase 读取字段；Jellyfin 默认的 MVC JSON 配置会保留 PascalCase，
    /// 所以 FireFly 的接口自己序列化，避免 TMDb 卡片、发布版本和下载任务出现 undefined。
    /// </summary>
    private IActionResult ApiJson(object? value, int statusCode = StatusCodes.Status200OK) => new ContentResult
    {
        Content = JsonSerializer.Serialize(value, ApiJsonOptions),
        ContentType = "application/json; charset=utf-8",
        StatusCode = statusCode,
    };

    /// <summary>注入的前端脚本和样式（index.html 里引用，无需登录）。</summary>
    [HttpGet("web/{name}")]
    [AllowAnonymous]
    public IActionResult Asset(string name)
    {
        var (resource, type) = name switch
        {
            "firefly.js" => ("FireFly.Jellyfin.Web.firefly.js", "application/javascript; charset=utf-8"),
            "firefly.css" => ("FireFly.Jellyfin.Web.firefly.css", "text/css; charset=utf-8"),
            _ => (null, null),
        };
        var stream = resource is null ? null : typeof(FireFlyController).Assembly.GetManifestResourceStream(resource);
        return stream is null ? NotFound() : File(stream, type!);
    }

    [HttpGet("status")]
    [Authorize(Policy = Elevated)]
    public IActionResult Status()
    {
        var quota = _runtime.Quota.Status();
        var paths = _runtime.Host.GetLibraryPaths();
        var dir = _runtime.DownloadDir;
        return ApiJson(new
        {
            version = typeof(FireFlyController).Assembly.GetName().Version?.ToString(),
            vip = quota.IsVip,
            license = quota.LicenseStatus,
            used = quota.Used,
            freeLimit = quota.FreeLimit,
            downloadDir = dir,
            downloadDirExists = Directory.Exists(dir),
            movieRoot = paths.MovieRoot,
            seriesRoot = paths.SeriesRoot,
            sameVolumeMovie = SameVolume(dir, paths.MovieRoot),
            sameVolumeSeries = SameVolume(dir, paths.SeriesRoot),
            tmdbConfigured = _runtime.Tmdb.Configured,
            keywords = _runtime.Keywords(),
            rulesVersion = _runtime.Rules.Version,
            dataDir = _runtime.DataDir,
            libraries = (_runtime.Host as JellyfinMediaHost)?.AllLocations().Select(l => new { l.Library, l.Type, l.Path }),
        });
    }

    [HttpGet("discover")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Discover([FromQuery] string kind = "movie", CancellationToken ct = default)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { configured = false, items = Array.Empty<object>() });
        }

        var mediaKind = kind == "tv" ? MediaKind.Series : MediaKind.Movie;
        try
        {
            var items = await _runtime.Tmdb.TrendingAsync(mediaKind, ct).ConfigureAwait(false);
            return ApiJson(new
            {
                configured = true,
                items = items.Select(i => new
                {
                    i.TmdbId,
                    kind = i.Kind,
                    i.Title,
                    i.OriginalTitle,
                    i.Year,
                    i.Overview,
                    i.PosterUrl,
                    i.Rating,
                    inLibrary = _runtime.Host.Contains(new MediaQuery { Kind = i.Kind, Title = i.Title, Year = i.Year, TmdbId = i.TmdbId }),
                }),
            });
        }
        catch (HttpRequestException ex)
        {
            return ApiJson(new { error = "TMDb 请求失败：" + ex.Message }, StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>剧集的季数（发现页选季用）。</summary>
    [HttpGet("seasons")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Seasons([FromQuery] string? itemId, [FromQuery] string? tmdbId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(itemId) && Guid.TryParse(itemId, out var id) && _library.GetItemById(id) is Series series)
        {
            var seasons = series.GetRecursiveChildren(i => i is Season).OfType<Season>()
                .Select(s => s.IndexNumber ?? 0).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
            return ApiJson(new { seasons = seasons.Count > 0 ? seasons : new List<int> { 1 } });
        }

        if (!string.IsNullOrEmpty(tmdbId) && _runtime.Tmdb.Configured)
        {
            var detail = await _runtime.Tmdb.DetailAsync(MediaKind.Series, tmdbId, ct).ConfigureAwait(false);
            var count = Math.Max(1, detail?.SeasonCount ?? 1);
            return ApiJson(new { seasons = Enumerable.Range(1, count) });
        }

        return ApiJson(new { seasons = new[] { 1 } });
    }

    [HttpPost("search")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        var query = ResolveQuery(request);
        if (query is null || string.IsNullOrWhiteSpace(query.Title))
        {
            return ApiJson(new { error = "无法识别要搜索的影片" }, StatusCodes.Status400BadRequest);
        }

        var result = await _runtime.Search.SearchAsync(query, request.Refresh, ct).ConfigureAwait(false);
        return ApiJson(new
        {
            query = result.Query,
            releases = result.Releases,
            queries = result.Queries,
            errors = result.Errors,
            fromCache = result.FromCache,
            quota = _runtime.Quota.Status(),
        });
    }

    [HttpPost("tasks")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct)
    {
        try
        {
            var task = await _runtime.App.SubmitAsync(request.Query, request.Release, ct).ConfigureAwait(false);
            return ApiJson(task);
        }
        catch (QuotaExceededException ex)
        {
            return ApiJson(new { error = ex.Message, quota = ex.Status }, StatusCodes.Status402PaymentRequired);
        }
        catch (InvalidOperationException ex)
        {
            return ApiJson(new { error = ex.Message }, StatusCodes.Status400BadRequest);
        }
    }

    [HttpGet("tasks")]
    [Authorize(Policy = Elevated)]
    public IActionResult Tasks() => ApiJson(_runtime.App.ListTasks());

    [HttpPost("tasks/{id}/dismiss")]
    [Authorize(Policy = Elevated)]
    public IActionResult Dismiss(string id) => _runtime.App.Dismiss(id) ? Ok() : NotFound();

    /// <summary>详情页给 itemId（电影 / 剧集 / 季 / 单集），发现页直接给片名年份。</summary>
    private MediaQuery? ResolveQuery(SearchRequest r)
    {
        if (!string.IsNullOrEmpty(r.ItemId) && Guid.TryParse(r.ItemId, out var id))
        {
            var item = _library.GetItemById(id);
            return item switch
            {
                Movie m => new MediaQuery { Kind = MediaKind.Movie, Title = m.Name, OriginalTitle = Different(m.OriginalTitle, m.Name), Year = m.ProductionYear, TmdbId = m.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) },
                Series s => FromSeries(s, r.Season ?? 1),
                Season season when season.Series is { } s => FromSeries(s, r.Season ?? season.IndexNumber ?? 1),
                Episode e when e.Series is { } s => FromSeries(s, r.Season ?? e.ParentIndexNumber ?? 1),
                _ => null,
            };
        }

        return new MediaQuery
        {
            Kind = r.Kind ?? MediaKind.Movie,
            Title = r.Title?.Trim() ?? string.Empty,
            OriginalTitle = Different(r.OriginalTitle, r.Title),
            Year = r.Year,
            Season = (r.Kind ?? MediaKind.Movie) == MediaKind.Series ? r.Season ?? 1 : null,
            TmdbId = r.TmdbId,
        };
    }

    private static MediaQuery FromSeries(Series s, int season) => new()
    {
        Kind = MediaKind.Series,
        Title = s.Name,
        OriginalTitle = Different(s.OriginalTitle, s.Name),
        Year = s.ProductionYear,
        Season = season,
        TmdbId = s.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb),
    };

    private static string? Different(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) || string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase) ? null : a.Trim();

    /// <summary>粗略判断两个目录是否在同一磁盘（macOS 外接盘都挂在 /Volumes 下）。</summary>
    private static bool? SameVolume(string a, string? b)
    {
        if (string.IsNullOrEmpty(b))
        {
            return null;
        }

        static string Root(string p)
        {
            var full = Path.GetFullPath(p);
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts[0] == "Volumes" ? "/Volumes/" + parts[1] : "/";
        }

        return Root(a) == Root(b);
    }
}
