using System.Text.Json;
using FireFly.Core;
using FireFly.Core.Metadata;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Sources;
using FireFly.Core.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FireFly.Jellyfin.Api;

/// <summary>Stage：preferred 先返回优选结果，fallback 追加降级结果；不传则一次做完。</summary>
public sealed record SearchRequest(string? ItemId, string? Title, string? OriginalTitle, int? Year, MediaKind? Kind, int? Season, string? TmdbId, bool Refresh, SearchStage? Stage = null);

/// <summary>提交下载只带版本 ID（InfoHash），完整版本和磁力由服务端从搜索缓存里找回。</summary>
public sealed record SubmitRequest(MediaQuery Query, string ReleaseId);


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
            "firefly.js" or "firefly-i18n.js" or "firefly-drawer.js" or "firefly-hot.js" or "firefly-detail.js"
                => ("FireFly.Jellyfin.Web." + name, "application/javascript; charset=utf-8"),
            "firefly.css" => ("FireFly.Jellyfin.Web.firefly.css", "text/css; charset=utf-8"),
            _ => (null, null),
        };
        var stream = resource is null ? null : typeof(FireFlyController).Assembly.GetManifestResourceStream(resource);
        return stream is null ? NotFound() : File(stream, type!);
    }

    /// <summary>
    /// TMDb 图片经插件取并缓存到本地。&lt;img&gt; 请求带不上 Jellyfin 的登录头，所以不要求登录；
    /// 只接受 TMDb 的尺寸和文件名，取的也只是配置的图片地址，不能被当成任意转发。
    /// </summary>
    [HttpGet("image/{size}/{file}")]
    [AllowAnonymous]
    public async Task<IActionResult> Image(string size, string file, CancellationToken ct)
    {
        try
        {
            var image = await _runtime.Tmdb.GetImageAsync(size, file, ct).ConfigureAwait(false);
            if (image is null)
            {
                return NotFound();
            }

            // 同一路径的图片不会变，让浏览器长期缓存
            Response.Headers.CacheControl = "public, max-age=2592000, immutable";
            return File(image.Value.Data, image.Value.ContentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>清空 TMDb 元数据和图片缓存。</summary>
    [HttpDelete("cache")]
    [Authorize(Policy = Elevated)]
    public IActionResult ClearCache()
    {
        _runtime.MetadataCache.Clear();
        _runtime.ImageCache.Clear();
        return ApiJson(new { metadataCacheBytes = 0, imageCacheBytes = 0 });
    }

    /// <summary>前端界面用的设置：界面文字跟随 TMDb 语言。</summary>
    [HttpGet("ui")]
    [Authorize(Policy = Elevated)]
    public IActionResult Ui() => ApiJson(new { language = _runtime.TmdbLanguage });

    [HttpGet("status")]
    [Authorize(Policy = Elevated)]
    public IActionResult Status()
    {
        var paths = _runtime.Host.GetLibraryPaths();
        var dir = _runtime.DownloadDir;
        return ApiJson(new
        {
            version = typeof(FireFlyController).Assembly.GetName().Version?.ToString(),
            downloadDir = dir,
            downloadDirExists = Directory.Exists(dir),
            movieRoot = paths.MovieRoot,
            seriesRoot = paths.SeriesRoot,
            sameVolumeMovie = SameVolume(dir, paths.MovieRoot),
            sameVolumeSeries = SameVolume(dir, paths.SeriesRoot),
            tmdbConfigured = _runtime.Tmdb.Configured,
            rulesVersion = _runtime.Rules.Version,
            rulesOverride = _runtime.Rules.HasOverride,
            rulesWarnings = _runtime.Rules.Warnings,
            sourceCount = _runtime.SourceCount,
            metadataCacheBytes = _runtime.MetadataCache.SizeBytes(),
            imageCacheBytes = _runtime.ImageCache.SizeBytes(),
            tmdbApiBases = _runtime.Rules.Tmdb.ApiBases,
            tmdbImageBases = _runtime.Rules.Tmdb.ImageBases,
            dataDir = _runtime.DataDir,
            libraries = (_runtime.Host as JellyfinMediaHost)?.AllLocations().Select(l => new { l.Library, l.Type, l.Path }),
        });
    }

    /// <summary>榜单页的筛选行。类型来自 TMDb（按配置语言），其余来自规则文件。</summary>
    [HttpGet("discover/filters")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> DiscoverFilters([FromQuery] string? category, CancellationToken ct)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { configured = false, rows = Array.Empty<object>() });
        }

        var rule = _runtime.Rules.Discover;
        var cat = DiscoverPlanner.Category(rule, category);
        try
        {
            var genres = await _runtime.Tmdb.GenresAsync(cat.Kind, ct).ConfigureAwait(false);
            return ApiJson(new
            {
                configured = true,
                category = cat.Key,
                genres = genres.ToDictionary(g => g.Value, g => g.Label),
                rows = DiscoverPlanner.Rows(rule, cat, genres, DateTime.Now.Year),
            });
        }
        catch (HttpRequestException ex)
        {
            return TmdbFailed(ex);
        }
    }

    /// <summary>榜单：按筛选项调用 TMDb /discover，一页 20 条。</summary>
    [HttpGet("discover")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Discover([FromQuery] DiscoverSelection selection, CancellationToken ct)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { configured = false, items = Array.Empty<object>() });
        }

        var plan = DiscoverPlanner.Plan(_runtime.Rules.Discover, selection, DateOnly.FromDateTime(DateTime.Now));
        try
        {
            var page = await _runtime.Tmdb.DiscoverAsync(plan, ct).ConfigureAwait(false);
            return ApiJson(new
            {
                configured = true,
                category = plan.Category.Key,
                page = page.Page,
                totalPages = page.TotalPages,
                totalResults = page.TotalResults,
                items = page.Items.Select(i => new
                {
                    i.TmdbId,
                    kind = i.Kind,
                    i.Title,
                    i.OriginalTitle,
                    i.Year,
                    i.Overview,
                    i.PosterUrl,
                    i.Rating,
                    i.GenreIds,
                    inLibrary = InLibrary(i.Kind, i.TmdbId, i.Title, i.Year),
                }),
            });
        }
        catch (HttpRequestException ex)
        {
            return TmdbFailed(ex);
        }
    }

    /// <summary>榜单页右上角的搜索：按片名搜 TMDb 电影和剧集。</summary>
    [HttpGet("discover/search")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> SearchTitles([FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { configured = false, items = Array.Empty<object>() });
        }

        if (string.IsNullOrWhiteSpace(q))
        {
            return ApiJson(new { configured = true, page = 1, totalPages = 0, totalResults = 0, items = Array.Empty<object>() });
        }

        try
        {
            var result = await _runtime.Tmdb.SearchAsync(q, page, ct).ConfigureAwait(false);
            return ApiJson(new
            {
                configured = true,
                page = result.Page,
                totalPages = result.TotalPages,
                totalResults = result.TotalResults,
                items = result.Items.Select(i => new
                {
                    i.TmdbId,
                    kind = i.Kind,
                    i.Title,
                    i.OriginalTitle,
                    i.Year,
                    i.Overview,
                    i.PosterUrl,
                    i.Rating,
                    i.GenreIds,
                    inLibrary = InLibrary(i.Kind, i.TmdbId, i.Title, i.Year),
                }),
            });
        }
        catch (HttpRequestException ex)
        {
            return TmdbFailed(ex);
        }
    }

    /// <summary>配置页的 TMDb 自检：接口入口和图片域名是否可达。</summary>
    [HttpGet("selfcheck/tmdb")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> CheckTmdb(CancellationToken ct) =>
        ApiJson(await _runtime.Tmdb.CheckAsync(ct).ConfigureAwait(false));

    /// <summary>资源详情页。kind = movie | tv。</summary>
    [HttpGet("detail/{kind}/{tmdbId}")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Detail(string kind, string tmdbId, CancellationToken ct)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { error = "请先在 控制台 → 插件 → FireFly 里填写 TMDb API Key" }, StatusCodes.Status400BadRequest);
        }

        var mediaKind = ParseKind(kind);
        try
        {
            var detail = await _runtime.Tmdb.FullDetailAsync(mediaKind, tmdbId, _runtime.Rules.Discover.WatchRegions, ct).ConfigureAwait(false);
            if (detail is null)
            {
                return ApiJson(new { error = "TMDb 没有这个条目" }, StatusCodes.Status404NotFound);
            }

            return ApiJson(new
            {
                detail,
                inLibrary = InLibrary(mediaKind, detail.TmdbId, detail.Title, detail.Year),
                favorite = _runtime.Favorites.Contains(mediaKind, detail.TmdbId),
                showTrailers = _runtime.ShowTrailers,
                language = _runtime.TmdbLanguage,
            });
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode == System.Net.HttpStatusCode.NotFound
                ? ApiJson(new { error = "TMDb 没有这个条目" }, StatusCodes.Status404NotFound)
                : TmdbFailed(ex);
        }
    }

    /// <summary>季卡片底部的"最近一集"。</summary>
    [HttpGet("detail/tv/{tmdbId}/season/{season:int}")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> SeasonLatest(string tmdbId, int season, CancellationToken ct)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return ApiJson(new { error = "未配置 TMDb API Key" }, StatusCodes.Status400BadRequest);
        }

        try
        {
            return ApiJson(new { episode = await _runtime.Tmdb.SeasonLatestEpisodeAsync(tmdbId, season, ct).ConfigureAwait(false) });
        }
        catch (HttpRequestException ex)
        {
            return TmdbFailed(ex);
        }
    }

    /// <summary>
    /// 收藏列表。收藏时存的片名和海报是当时的 TMDb 语言；列表按当前语言重新取（TMDb 有缓存），
    /// 取不到时用收藏时存的。
    /// </summary>
    [HttpGet("favorites")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> FavoriteList(CancellationToken ct)
    {
        var favorites = _runtime.Favorites.List();
        // 首次切换语言时缓存是空的，同时向 TMDb 发的请求限制在几个以内
        using var gate = new SemaphoreSlim(FavoriteLookupConcurrency);
        var current = await Task.WhenAll(favorites.Select(async f =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await CurrentLanguageAsync(f, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return ApiJson(favorites.Select((f, i) => new
        {
            f.TmdbId,
            kind = f.Kind,
            title = current[i]?.Title ?? f.Title,
            originalTitle = current[i]?.OriginalTitle ?? f.OriginalTitle,
            f.Year,
            posterUrl = current[i]?.PosterUrl ?? f.PosterUrl,
            rating = current[i]?.Rating ?? f.Rating,
            f.AddedAt,
            inLibrary = InLibrary(f.Kind, f.TmdbId, f.Title, f.Year),
        }));
    }

    private const int FavoriteLookupConcurrency = 6;

    private async Task<TmdbItem?> CurrentLanguageAsync(FavoriteItem favorite, CancellationToken ct)
    {
        if (!_runtime.Tmdb.Configured)
        {
            return null;
        }

        try
        {
            return await _runtime.Tmdb.DetailAsync(favorite.Kind, favorite.TmdbId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [HttpPost("favorites")]
    [Authorize(Policy = Elevated)]
    public IActionResult AddFavorite([FromBody] FavoriteItem item)
    {
        try
        {
            _runtime.Favorites.Add(item);
            return ApiJson(new { favorite = true });
        }
        catch (InvalidOperationException ex)
        {
            return ApiJson(new { error = ex.Message }, StatusCodes.Status400BadRequest);
        }
    }

    [HttpDelete("favorites/{kind}/{tmdbId}")]
    [Authorize(Policy = Elevated)]
    public IActionResult RemoveFavorite(string kind, string tmdbId)
    {
        _runtime.Favorites.Remove(ParseKind(kind), tmdbId);
        return ApiJson(new { favorite = false });
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

        query = await WithTmdbNamesAsync(query, ct).ConfigureAwait(false);
        var result = await _runtime.Search.SearchAsync(query, request.Refresh, request.Stage ?? SearchStage.Full, ct).ConfigureAwait(false);

        // 只下发干净名称和画质信息：不下发原始发布名、磁力、发布站、搜索词和出错的源地址，
        // 避免上游站点被识别、也避免绕过插件直接去资源站搜索
        return ApiJson(new
        {
            query = result.Query,
            releases = result.Releases.Select(r => new
            {
                id = r.InfoHash,
                displayName = ReleaseDisplay.Name(result.Query, r),
                coverage = result.Query.Kind == MediaKind.Series ? ReleaseDisplay.Coverage(r, _runtime.Rules, result.Query.SeasonEpisodes) : null,
                r.SizeBytes,
                r.CreatedAt,
                fileCount = r.Files.Count,
                preferred = !string.IsNullOrEmpty(r.Traits.Publisher),
                demoted = SearchService.Demotion(r) > 0,
                traits = new { r.Traits.Resolution, r.Traits.Codec, r.Traits.Hdr, r.Traits.MediaSource, r.Traits.Audio, r.Traits.Subtitles },
            }),
            sourceErrors = result.Errors.Count,
            fromCache = result.FromCache,
            needsFallback = result.NeedsFallback,
        });
    }

    [HttpPost("tasks")]
    [Authorize(Policy = Elevated)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct)
    {
        var release = string.IsNullOrWhiteSpace(request.ReleaseId) ? null : _runtime.Search.FindRelease(request.ReleaseId);
        if (release is null)
        {
            return ApiJson(new { error = "这个版本的信息已过期，请点刷新重新搜索后再下载" }, StatusCodes.Status410Gone);
        }

        try
        {
            var task = await _runtime.App.SubmitAsync(request.Query, release, ct).ConfigureAwait(false);
            return ApiJson(new { task = TaskView(task) });
        }
        catch (InvalidOperationException ex)
        {
            return ApiJson(new { error = ex.Message }, StatusCodes.Status400BadRequest);
        }
    }

    [HttpGet("tasks")]
    [Authorize(Policy = Elevated)]
    public IActionResult Tasks() => ApiJson(_runtime.App.ListTasks().Select(TaskView));

    /// <summary>任务列表同样只给干净名称，不带原始发布名和磁力。</summary>
    private static object TaskView(DownloadTask t) => new
    {
        t.Id,
        t.Query,
        state = t.State,
        displayName = t.Release.DisplayName ?? ReleaseDisplay.Name(t.Query, t.Release),
        t.SubmittedAt,
        t.UpdatedAt,
        t.BytesDone,
        t.BytesTotal,
        t.SpeedBps,
        t.Message,
    };

    [HttpPost("tasks/{id}/dismiss")]
    [Authorize(Policy = Elevated)]
    public IActionResult Dismiss(string id) => _runtime.App.Dismiss(id) ? Ok() : NotFound();

    /// <summary>有 TMDb ID 时补上原名和中文别名，供资源搜索换名重试；TMDb 不可用时照常用已有片名搜索。</summary>
    private async Task<MediaQuery> WithTmdbNamesAsync(MediaQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.TmdbId) || !_runtime.Tmdb.Configured)
        {
            return query;
        }

        try
        {
            var names = await _runtime.Tmdb.NamesAsync(query.Kind, query.TmdbId, ct).ConfigureAwait(false);
            return query with
            {
                OriginalTitle = query.OriginalTitle ?? Different(names.OriginalTitle, query.Title),
                AlternateTitles = names.AlternateTitles,
                SeasonEpisodes = query.Kind == MediaKind.Series && names.SeasonEpisodes?.TryGetValue(query.Season ?? 1, out var count) == true ? count : null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return query;
        }
    }

    private bool InLibrary(MediaKind kind, string tmdbId, string title, int? year) =>
        _runtime.Host.Contains(new MediaQuery { Kind = kind, Title = title, Year = year, TmdbId = tmdbId });

    private static MediaKind ParseKind(string kind) =>
        kind is "tv" or "Series" or "series" ? MediaKind.Series : MediaKind.Movie;

    private IActionResult TmdbFailed(HttpRequestException ex) =>
        ApiJson(new { error = "TMDb 请求失败：" + ex.Message }, StatusCodes.Status502BadGateway);

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
