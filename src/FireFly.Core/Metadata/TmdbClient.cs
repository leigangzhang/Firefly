using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FireFly.Core.Models;
using FireFly.Core.Rules;
using FireFly.Core.Storage;

namespace FireFly.Core.Metadata;

public sealed record TmdbItem(
    string TmdbId,
    MediaKind Kind,
    string Title,
    string? OriginalTitle,
    int? Year,
    string? Overview,
    string? PosterUrl,
    double? Rating,
    int? SeasonCount,
    IReadOnlyList<int>? GenreIds = null);

/// <summary>SeasonEpisodes：剧集各季的集数（季号 → 集数），电影为空。</summary>
public sealed record TmdbNames(string? OriginalTitle, IReadOnlyList<string> AlternateTitles, IReadOnlyDictionary<int, int>? SeasonEpisodes = null)
{
    /// <summary>TMDb 类型 ID，用来判断资源类型（动漫、纪录片、综艺）。</summary>
    public IReadOnlyList<int> Genres { get; init; } = Array.Empty<int>();
}

public sealed record TmdbPageResult(int Page, int TotalPages, int TotalResults, IReadOnlyList<TmdbItem> Items);

/// <summary>导演 / 编剧 / 主创 / 演员。Role 是职务或饰演的角色，Episodes 只有剧集演员有。</summary>
public sealed record TmdbPerson(string Name, string? Role, string? ProfileUrl, int? Episodes = null);

public sealed record TmdbLogo(string Name, string? LogoUrl);

public sealed record TmdbVideo(string Key, string Name, string Type, string? Language);

public sealed record TmdbSeason(int Number, string Name, int EpisodeCount, int? Year, string? PosterUrl, string? AirDate = null, string? Overview = null, double? Rating = null);

/// <summary>一季里最近播出的一集（季卡片底部那一行）。Aired 为 false 表示这一季还没开播，此时给的是第一集的预定日期。</summary>
public sealed record TmdbEpisodeBrief(int Season, int Episode, string Name, string? AirDate, bool IsFinale, bool Aired, int AiredCount);

public sealed record TmdbWatchProviders(string Region, string? Link, IReadOnlyList<TmdbLogo> Stream, IReadOnlyList<TmdbLogo> Rent, IReadOnlyList<TmdbLogo> Buy);

/// <summary>详情页要的全部信息，一次请求（append_to_response）取回。</summary>
public sealed record TmdbDetail(
    string TmdbId,
    MediaKind Kind,
    string Title,
    string? OriginalTitle,
    string? OriginalLanguage,
    int? Year,
    string? ReleaseDate,
    string? Overview,
    string? Tagline,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    int VoteCount,
    int? Runtime,
    string? Status,
    IReadOnlyList<string> Genres,
    string? Certification,
    int? SeasonCount,
    int? EpisodeCount,
    long? Budget,
    long? Revenue,
    string? Homepage,
    IReadOnlyList<string> Countries,
    IReadOnlyList<TmdbPerson> Directors,
    IReadOnlyList<TmdbPerson> Cast,
    IReadOnlyList<TmdbVideo> Videos,
    IReadOnlyList<TmdbLogo> Companies,
    IReadOnlyList<TmdbLogo> Networks,
    TmdbWatchProviders? Providers,
    IReadOnlyList<TmdbSeason> Seasons,
    int? CurrentSeason = null,
    IReadOnlyList<int>? GenreIds = null,
    IReadOnlyList<TmdbItem>? Recommendations = null);

/// <summary>TMDb 连接设置。ApiBase / ImageBase 为空时用默认入口；入口被屏蔽时在配置页改成可用的地址。</summary>
public sealed record TmdbSettings(string ApiKey, string Language, string? ApiBase = null, string? ImageBase = null)
{
    // 部分网络会把 api.themoviedb.org 解析到不可达 IP；api.tmdb.org 是同一 API 的可用入口。
    public const string DefaultApiBase = "https://api.tmdb.org/3";
    public const string DefaultImageBase = "https://image.tmdb.org/t/p";

    public string Api => string.IsNullOrWhiteSpace(ApiBase) ? DefaultApiBase : ApiBase.Trim().TrimEnd('/');

    public string Images => string.IsNullOrWhiteSpace(ImageBase) ? DefaultImageBase : ImageBase.Trim().TrimEnd('/');
}

/// <summary>自检结果：接口和图片域名各自是否可达；SamplePosterUrl 给浏览器再试加载一张图。</summary>
public sealed record TmdbCheck(bool ApiOk, string ApiMessage, bool ImageOk, string ImageMessage, string? SamplePosterUrl);

/// <summary>TMDb：热门、按条件发现、类型表、详情。API Key 由用户自己填，只存在本机配置里。响应在内存里缓存一段时间。</summary>
public sealed class TmdbClient
{
    private const string PosterSize = "/w342";
    private const string BackdropSize = "/w1280";
    private const string ProfileSize = "/w185";
    private const string LogoSize = "/w92";
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan GenreTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    /// <summary>网络请求失败时，落盘缓存里不超过这个时间的旧数据仍然拿来用。</summary>
    // 同类推荐展示的数量；recommendations 不够时用 similar 补齐
    private const int RecommendationCount = 12;
    private static readonly TimeSpan StaleLimit = TimeSpan.FromDays(30);
    private static readonly string[] ImageSizes = { "w92", "w185", "w342", "w500", "w780", "w1280", "original" };
    private static readonly System.Text.RegularExpressions.Regex ImageFile = new(@"^[A-Za-z0-9_-]{1,128}\.(jpg|jpeg|png|svg|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private readonly HttpClient _http;
    private readonly Func<TmdbSettings> _settings;
    private readonly DiskCache? _metadataCache;
    private readonly DiskCache? _imageCache;
    private readonly Func<bool> _cacheEnabled;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, string Json)> _cache = new();

    /// <summary>metadataCache / imageCache 为空时只用内存缓存、图片不经插件。cacheEnabled 为空时视为开启。</summary>
    public TmdbClient(HttpClient http, Func<TmdbSettings> settings, DiskCache? metadataCache = null, DiskCache? imageCache = null, Func<bool>? cacheEnabled = null)
    {
        _http = http;
        _settings = settings;
        _metadataCache = metadataCache;
        _imageCache = imageCache;
        _cacheEnabled = cacheEnabled ?? (() => true);
    }

    private bool CacheEnabled => _cacheEnabled();

    private string Images => _settings().Images;

    public bool Configured => !string.IsNullOrWhiteSpace(_settings().ApiKey);

    public async Task<IReadOnlyList<TmdbItem>> TrendingAsync(MediaKind kind, CancellationToken ct)
    {
        var page = await GetAsync<TmdbPage>($"/trending/{Segment(kind)}/week", null, ListTtl, ct).ConfigureAwait(false);
        return (page?.Results ?? new()).Select(r => r.ToItem(kind, Images)).ToList();
    }

    public async Task<TmdbPageResult> DiscoverAsync(DiscoverPlan plan, CancellationToken ct)
    {
        var page = await GetAsync<TmdbPage>(plan.Path, plan.Parameters, ListTtl, ct).ConfigureAwait(false);
        var kind = plan.Category.Kind;
        return new TmdbPageResult(
            page?.Page ?? 1,
            Math.Min(page?.TotalPages ?? 1, 500),
            page?.TotalResults ?? 0,
            (page?.Results ?? new()).Select(r => r.ToItem(kind, Images)).ToList());
    }

    /// <summary>TMDb 的类型表（已按配置语言翻译）。</summary>
    public async Task<IReadOnlyList<FilterOption>> GenresAsync(MediaKind kind, CancellationToken ct)
    {
        var list = await GetAsync<TmdbGenreList>($"/genre/{Segment(kind)}/list", null, GenreTtl, ct).ConfigureAwait(false);
        return (list?.Genres ?? new())
            .Select(g => new FilterOption(g.Id.ToString(CultureInfo.InvariantCulture), g.Name ?? string.Empty))
            .ToList();
    }

    /// <summary>按片名搜电影和剧集（TMDb /search/multi，丢掉人物结果）。</summary>
    public async Task<TmdbPageResult> SearchAsync(string query, int page, CancellationToken ct)
    {
        var result = await SearchSingleAsync(query, page, ct).ConfigureAwait(false);
        var variant = page == 1 && result.TotalResults < 5 ? QueryVariant(query) : null;
        if (variant is null || string.Equals(variant, query.Trim(), StringComparison.Ordinal))
        {
            return result;
        }

        // TMDb 不做中文近音纠错；原查询结果少时补一次常见错字变体，再合并去重。
        var corrected = await SearchSingleAsync(variant, 1, ct).ConfigureAwait(false);
        var merged = corrected.Items.Concat(result.Items)
            .GroupBy(i => (i.Kind, i.TmdbId))
            .Select(g => g.First())
            .ToList();
        return new TmdbPageResult(1, 1, merged.Count, merged);
    }

    private async Task<TmdbPageResult> SearchSingleAsync(string query, int page, CancellationToken ct)
    {
        var result = await GetAsync<TmdbPage>(
            "/search/multi",
            new Dictionary<string, string>
            {
                ["query"] = query.Trim(),
                ["page"] = Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture),
                ["include_adult"] = "false",
            },
            ListTtl,
            ct).ConfigureAwait(false);
        return ToSearchPage(result, Images);
    }

    /// <summary>常见中文片名错字/近音字；只用于补搜，不替换用户原始查询。</summary>
    internal static string? QueryVariant(string query)
    {
        var text = query.Trim();
        foreach (var (from, to) in ChineseTitleVariants)
        {
            if (text.Contains(from, StringComparison.Ordinal))
            {
                return text.Replace(from, to, StringComparison.Ordinal);
            }
        }

        return null;
    }

    private static readonly (string From, string To)[] ChineseTitleVariants =
    {
        ("权利", "权力"),
    };

    internal static TmdbPageResult ToSearchPage(TmdbPage? page, string images = TmdbSettings.DefaultImageBase) => new(
        page?.Page ?? 1,
        Math.Min(page?.TotalPages ?? 1, 500),
        page?.TotalResults ?? 0,
        (page?.Results ?? new())
            .Where(r => r.MediaType is "movie" or "tv")
            .Select(r => r.ToItem(r.MediaType == "tv" ? MediaKind.Series : MediaKind.Movie, images))
            .ToList());

    /// <summary>
    /// 资源搜索用的译名：原名 + 中港台新的别名（alternative_titles）。
    /// 资源站常用和 TMDb 主标题不同的译名，只靠主标题会搜不到。
    /// </summary>
    public async Task<TmdbNames> NamesAsync(MediaKind kind, string tmdbId, CancellationToken ct)
    {
        var seg = Segment(kind);
        var id = Uri.EscapeDataString(tmdbId);
        var detail = await GetJsonAsync($"/{seg}/{id}", new Dictionary<string, string> { ["append_to_response"] = "alternative_titles" }, DetailTtl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(detail);
        return ParseNames(kind, doc.RootElement);
    }

    private static readonly string[] ChineseRegions = { "CN", "TW", "HK", "SG" };

    internal static TmdbNames ParseNames(MediaKind kind, JsonElement r)
    {
        var original = Str(r, kind == MediaKind.Movie ? "original_title" : "original_name");
        var alt = r.TryGetProperty("alternative_titles", out var a) ? a : default;
        var list = Arr(alt, kind == MediaKind.Movie ? "titles" : "results")
            .Where(t => Str(t, "iso_3166_1") is { } c && ChineseRegions.Contains(c))
            .OrderBy(t => Array.IndexOf(ChineseRegions, Str(t, "iso_3166_1")))
            .Select(t => Str(t, "title"))
            .OfType<string>()
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        var seasons = Arr(r, "seasons")
            .Select(x => (No: Int(x, "season_number"), Count: Int(x, "episode_count")))
            .Where(x => x.No is not null && x.Count is > 0)
            .GroupBy(x => x.No!.Value)
            .ToDictionary(g => g.Key, g => g.First().Count!.Value);
        var genres = Arr(r, "genres").Select(g => Int(g, "id")).OfType<int>().ToList();
        return new TmdbNames(original, list, seasons) { Genres = genres };
    }

    public async Task<TmdbItem?> DetailAsync(MediaKind kind, string tmdbId, CancellationToken ct)
    {
        var r = await GetAsync<TmdbResult>($"/{Segment(kind)}/{Uri.EscapeDataString(tmdbId)}", null, DetailTtl, ct).ConfigureAwait(false);
        return r?.ToItem(kind, Images);
    }

    /// <summary>详情页：基本信息 + 预告片 + 演职员 + 分级 + 播放平台 + 同类推荐。</summary>
    public async Task<TmdbDetail?> FullDetailAsync(MediaKind kind, string tmdbId, IReadOnlyList<string> watchRegions, CancellationToken ct)
    {
        var append = kind == MediaKind.Movie
            ? "videos,credits,release_dates,watch/providers,recommendations,similar"
            : "videos,aggregate_credits,content_ratings,watch/providers,recommendations,similar";
        var json = await GetJsonAsync(
            $"/{Segment(kind)}/{Uri.EscapeDataString(tmdbId)}",
            new Dictionary<string, string> { ["append_to_response"] = append, ["include_video_language"] = "zh,en,null" },
            DetailTtl,
            ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return ParseDetail(kind, doc.RootElement, watchRegions, images: Images);
    }

    /// <summary>
    /// 自检：接口入口能否用当前 Key 取到热门，图片域名能否连上。
    /// 顺带返回一张海报地址，配置页再让浏览器经插件的图片缓存加载一次，确认整条链路通。
    /// </summary>
    public async Task<TmdbCheck> CheckAsync(CancellationToken ct)
    {
        var settings = _settings();
        bool apiOk;
        string apiMessage;
        string? sample = null;
        try
        {
            var page = JsonSerializer.Deserialize<TmdbPage>(await GetJsonAsync("/trending/movie/week", null, ListTtl, ct).ConfigureAwait(false), JsonOptions);
            sample = page?.Results?.Select(r => r.ToItem(MediaKind.Movie, settings.Images).PosterUrl).FirstOrDefault(u => u is not null);
            (apiOk, apiMessage) = (true, settings.Api + " 可用");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException && !ct.IsCancellationRequested)
        {
            (apiOk, apiMessage) = (false, $"{settings.Api} 不可用：{ex.Message}");
        }

        bool imageOk;
        string imageMessage;
        try
        {
            // 只看能不能连上：任何 HTTP 响应都说明域名可达，解析失败或超时才算不可达
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, sample ?? settings.Images + LogoSize + "/");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            (imageOk, imageMessage) = (true, $"{settings.Images} 可达（HTTP {(int)response.StatusCode}）");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            (imageOk, imageMessage) = (false, $"{settings.Images} 不可达：{ex.Message}");
        }

        return new TmdbCheck(apiOk, apiMessage, imageOk, imageMessage, sample);
    }

    private async Task<T?> GetAsync<T>(string path, IReadOnlyDictionary<string, string>? query, TimeSpan ttl, CancellationToken ct)
    {
        var json = await GetJsonAsync(path, query, ttl, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<string> GetJsonAsync(string path, IReadOnlyDictionary<string, string>? query, TimeSpan ttl, CancellationToken ct)
    {
        var settings = _settings();
        var (key, language) = (settings.ApiKey, settings.Language);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("未配置 TMDb API Key");
        }

        var extra = string.Concat((query ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"&{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{settings.Api}{path}?api_key={Uri.EscapeDataString(key)}&language={Uri.EscapeDataString(language)}{extra}";
        var now = DateTimeOffset.UtcNow;
        var cacheEnabled = CacheEnabled;
        if (!cacheEnabled)
        {
            _cache.Clear();
        }
        else if (_cache.TryGetValue(url, out var hit) && hit.Expires > now)
        {
            return hit.Json;
        }

        // 落盘缓存的键不含 API Key：换 Key 不影响缓存，缓存文件里也不留 Key
        var diskKey = $"{settings.Api}{path}?language={language}{extra}";
        if (cacheEnabled && _metadataCache?.Get(diskKey, ttl) is { } fresh)
        {
            var cached = Encoding.UTF8.GetString(fresh.Data);
            Remember(url, now + ttl - fresh.Age, cached);
            return cached;
        }

        string json;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"TMDb 返回 {(int)response.StatusCode}", null, response.StatusCode);
            }

            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: null or >= System.Net.HttpStatusCode.InternalServerError } or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 连不上或 TMDb 出错时用旧缓存兜底；4xx（Key 错、条目不存在）照常报错
            if (cacheEnabled && _metadataCache?.Get(diskKey, StaleLimit) is { } stale)
            {
                return Encoding.UTF8.GetString(stale.Data);
            }

            throw;
        }

        if (cacheEnabled)
        {
            Remember(url, now + ttl, json);
            _metadataCache?.Set(diskKey, Encoding.UTF8.GetBytes(json));
        }
        return json;
    }

    private void Remember(string url, DateTimeOffset expires, string json)
    {
        if (_cache.Count > 500)
        {
            _cache.Clear();
        }

        _cache[url] = (expires, json);
    }

    /// <summary>
    /// 海报等图片：先查本地缓存，没有再从图片地址取并存下来。图片按路径不会变，缓存不过期，只按容量淘汰。
    /// size / file 来自浏览器请求，只接受 TMDb 的尺寸和文件名格式，避免被当成任意地址的转发。
    /// </summary>
    public async Task<(byte[] Data, string ContentType)?> GetImageAsync(string size, string file, CancellationToken ct)
    {
        if (!ImageSizes.Contains(size) || !ImageFile.IsMatch(file))
        {
            return null;
        }

        var type = ContentType(file);
        var key = size + "/" + file;
        var cacheEnabled = CacheEnabled;
        if (cacheEnabled && _imageCache?.Get(key) is { } hit)
        {
            return (hit.Data, type);
        }

        using var response = await _http.GetAsync($"{_settings().Images}/{size}/{file}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (cacheEnabled)
        {
            _imageCache?.Set(key, data);
        }
        return (data, type);
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    internal static TmdbDetail ParseDetail(MediaKind kind, JsonElement r, IReadOnlyList<string> watchRegions, DateOnly? today = null, string images = TmdbSettings.DefaultImageBase)
    {
        var movie = kind == MediaKind.Movie;
        var title = Str(r, movie ? "title" : "name") ?? string.Empty;
        var original = Str(r, movie ? "original_title" : "original_name");
        var date = Str(r, movie ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
        int? runtime = movie
            ? Int(r, "runtime")
            : Arr(r, "episode_run_time").Select(e => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 0).FirstOrDefault(v => v > 0) is var rt and > 0 ? rt : null;

        var directors = new List<TmdbPerson>();
        var cast = new List<TmdbPerson>();
        if (movie && r.TryGetProperty("credits", out var credits))
        {
            directors.AddRange(Arr(credits, "crew")
                .Where(c => Str(c, "job") is "Director" or "Screenplay" or "Writer")
                .OrderBy(c => Str(c, "job") == "Director" ? 0 : 1)
                .GroupBy(c => Str(c, "name") ?? string.Empty)
                .Select(g => new TmdbPerson(
                    g.Key,
                    string.Join(" / ", g.Select(c => Str(c, "job") == "Director" ? "导演" : "编剧").Distinct()),
                    g.Select(c => Image(images, ProfileSize, Str(c, "profile_path"))).FirstOrDefault(u => u is not null)))
                .Take(6));
            cast.AddRange(Arr(credits, "cast").Take(18)
                .Select(c => new TmdbPerson(Str(c, "name") ?? string.Empty, Str(c, "character"), Image(images, ProfileSize, Str(c, "profile_path")))));
        }
        else if (!movie)
        {
            directors.AddRange(Arr(r, "created_by").Take(6)
                .Select(c => new TmdbPerson(Str(c, "name") ?? string.Empty, "主创", Image(images, ProfileSize, Str(c, "profile_path")))));
            if (r.TryGetProperty("aggregate_credits", out var agg))
            {
                cast.AddRange(Arr(agg, "cast").Take(18).Select(c => new TmdbPerson(
                    Str(c, "name") ?? string.Empty,
                    Arr(c, "roles").Select(role => Str(role, "character")).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                    Image(images, ProfileSize, Str(c, "profile_path")),
                    Int(c, "total_episode_count"))));
            }
        }

        var videos = r.TryGetProperty("videos", out var v)
            ? Arr(v, "results")
                .Where(x => Str(x, "site") == "YouTube" && !string.IsNullOrEmpty(Str(x, "key")))
                .Select(x => new TmdbVideo(Str(x, "key")!, Str(x, "name") ?? string.Empty, Str(x, "type") ?? string.Empty, Str(x, "iso_639_1")))
                .OrderBy(x => x.Type switch { "Trailer" => 0, "Teaser" => 1, _ => 2 })
                .ThenBy(x => x.Language == "zh" ? 0 : 1)
                .Take(8)
                .ToList()
            : new List<TmdbVideo>();

        return new TmdbDetail(
            (Int(r, "id") ?? 0).ToString(CultureInfo.InvariantCulture),
            kind,
            title,
            string.Equals(original, title, StringComparison.Ordinal) ? null : original,
            Str(r, "original_language"),
            year,
            date,
            Str(r, "overview"),
            Str(r, "tagline"),
            Image(images, PosterSize, Str(r, "poster_path")),
            Image(images, BackdropSize, Str(r, "backdrop_path")),
            r.TryGetProperty("vote_average", out var va) && va.ValueKind == JsonValueKind.Number ? va.GetDouble() : null,
            Int(r, "vote_count") ?? 0,
            runtime,
            Str(r, "status"),
            Arr(r, "genres").Select(g => Str(g, "name")).OfType<string>().ToList(),
            Certification(kind, r, watchRegions),
            Int(r, "number_of_seasons"),
            Int(r, "number_of_episodes"),
            movie ? Long(r, "budget") : null,
            movie ? Long(r, "revenue") : null,
            Str(r, "homepage"),
            movie
                ? Arr(r, "production_countries").Select(c => Str(c, "iso_3166_1")).OfType<string>().ToList()
                : Arr(r, "origin_country").Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : null).OfType<string>().ToList(),
            directors,
            cast,
            videos,
            Logos(r, "production_companies", images),
            Logos(r, "networks", images),
            Providers(r, watchRegions, images),
            Arr(r, "seasons")
                .Where(se => (Int(se, "season_number") ?? 0) > 0)
                .Select(se => new TmdbSeason(
                    Int(se, "season_number") ?? 0,
                    Str(se, "name") ?? string.Empty,
                    Int(se, "episode_count") ?? 0,
                    Str(se, "air_date") is { Length: >= 4 } ad && int.TryParse(ad[..4], out var sy) ? sy : null,
                    Image(images, PosterSize, Str(se, "poster_path")),
                    Str(se, "air_date"),
                    Str(se, "overview"),
                    se.TryGetProperty("vote_average", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.GetDouble() > 0 ? sv.GetDouble() : null))
                .ToList(),
            movie ? null : CurrentSeason(r, today ?? DateOnly.FromDateTime(DateTime.Now)),
            Arr(r, "genres").Select(g => Int(g, "id")).OfType<int>().ToList(),
            Recommendations(kind, r, images));
    }

    /// <summary>同类推荐：先取 TMDb 的 recommendations（按用户行为），不够时用 similar（按类型和关键词）补齐，去重并排除自己。</summary>
    private static List<TmdbItem> Recommendations(MediaKind kind, JsonElement r, string images)
    {
        var self = Int(r, "id")?.ToString(CultureInfo.InvariantCulture);
        IEnumerable<TmdbItem> Results(string name) => r.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? (section.Deserialize<TmdbPage>(JsonOptions)?.Results ?? new()).Select(x => x.ToItem(kind, images))
            : Enumerable.Empty<TmdbItem>();

        var list = Results("recommendations").ToList();
        if (list.Count < RecommendationCount)
        {
            list.AddRange(Results("similar"));
        }

        return list
            .Where(i => i.TmdbId != self)
            .GroupBy(i => i.TmdbId)
            .Select(g => g.First())
            .Take(RecommendationCount)
            .ToList();
    }

    /// <summary>当前季：最近播出那一集所在的季；没有时取已开播的最大季，再没有取第 1 季。</summary>
    private static int? CurrentSeason(JsonElement r, DateOnly today)
    {
        if (r.TryGetProperty("last_episode_to_air", out var last) && Int(last, "season_number") is int n and > 0)
        {
            return n;
        }

        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var seasons = Arr(r, "seasons").Where(se => (Int(se, "season_number") ?? 0) > 0).ToList();
        if (seasons.Count == 0)
        {
            return null;
        }

        var aired = seasons.Where(se => Str(se, "air_date") is { } d && string.CompareOrdinal(d, todayText) <= 0).Select(se => Int(se, "season_number") ?? 0).ToList();
        return aired.Count > 0 ? aired.Max() : seasons.Min(se => Int(se, "season_number") ?? 0);
    }

    /// <summary>某一季最近播出的一集；整季还没开播时返回第一集。</summary>
    public async Task<TmdbEpisodeBrief?> SeasonLatestEpisodeAsync(string tmdbId, int season, CancellationToken ct)
    {
        var json = await GetJsonAsync($"/tv/{Uri.EscapeDataString(tmdbId)}/season/{season}", null, DetailTtl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return ParseSeasonLatest(doc.RootElement, season, DateOnly.FromDateTime(DateTime.Now));
    }

    internal static TmdbEpisodeBrief? ParseSeasonLatest(JsonElement r, int season, DateOnly today)
    {
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var episodes = Arr(r, "episodes").OrderBy(e => Int(e, "episode_number") ?? 0).ToList();
        if (episodes.Count == 0)
        {
            return null;
        }

        var aired = episodes.Where(e => Str(e, "air_date") is { } d && string.CompareOrdinal(d, todayText) <= 0).ToList();
        var pick = aired.Count > 0 ? aired[^1] : episodes[0];
        var isLast = (Int(pick, "episode_number") ?? 0) == (Int(episodes[^1], "episode_number") ?? -1);
        var finale = aired.Count > 0 && (Str(pick, "episode_type") == "finale" || (isLast && aired.Count == episodes.Count));
        return new TmdbEpisodeBrief(
            season,
            Int(pick, "episode_number") ?? 0,
            Str(pick, "name") ?? string.Empty,
            Str(pick, "air_date"),
            finale,
            aired.Count > 0,
            aired.Count);
    }

    /// <summary>电影取 release_dates，剧集取 content_ratings；按 watchRegions 的顺序找第一个非空分级。</summary>
    private static string? Certification(MediaKind kind, JsonElement r, IReadOnlyList<string> regions)
    {
        var results = kind == MediaKind.Movie
            ? r.TryGetProperty("release_dates", out var rd) ? Arr(rd, "results").ToList() : new List<JsonElement>()
            : r.TryGetProperty("content_ratings", out var cr) ? Arr(cr, "results").ToList() : new List<JsonElement>();
        foreach (var region in regions)
        {
            var entry = results.FirstOrDefault(x => Str(x, "iso_3166_1") == region);
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var cert = kind == MediaKind.Movie
                ? Arr(entry, "release_dates").Select(d => Str(d, "certification")).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
                : Str(entry, "rating");
            if (!string.IsNullOrWhiteSpace(cert))
            {
                return cert.Trim();
            }
        }

        return null;
    }

    private static TmdbWatchProviders? Providers(JsonElement r, IReadOnlyList<string> regions, string images)
    {
        if (!r.TryGetProperty("watch/providers", out var wp) || !wp.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var region in regions)
        {
            if (results.TryGetProperty(region, out var entry))
            {
                List<TmdbLogo> Pick(string name) => Arr(entry, name)
                    .Select(p => new TmdbLogo(Str(p, "provider_name") ?? string.Empty, Image(images, LogoSize, Str(p, "logo_path"))))
                    .ToList();
                var stream = Pick("flatrate").Concat(Pick("free")).Concat(Pick("ads")).DistinctBy(p => p.Name).ToList();
                return new TmdbWatchProviders(region, Str(entry, "link"), stream, Pick("rent"), Pick("buy"));
            }
        }

        return null;
    }

    private static List<TmdbLogo> Logos(JsonElement r, string name, string images) =>
        Arr(r, name).Select(c => new TmdbLogo(Str(c, "name") ?? string.Empty, Image(images, LogoSize, Str(c, "logo_path")))).ToList();

    private static string? Image(string images, string size, string? path) => string.IsNullOrEmpty(path) ? null : images + size + path;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) && i > 0 ? i : null;

    private static string Segment(MediaKind kind) => kind == MediaKind.Movie ? "movie" : "tv";

    internal sealed class TmdbPage
    {
        public int Page { get; set; }

        [JsonPropertyName("total_pages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("total_results")]
        public int TotalResults { get; set; }

        public List<TmdbResult>? Results { get; set; }
    }

    internal sealed class TmdbGenreList
    {
        public List<TmdbGenre>? Genres { get; set; }
    }

    internal sealed class TmdbGenre
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    internal sealed class TmdbResult
    {
        public long Id { get; set; }

        public string? Title { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        public string? Overview { get; set; }

        [JsonPropertyName("poster_path")]
        public string? PosterPath { get; set; }

        [JsonPropertyName("vote_average")]
        public double? VoteAverage { get; set; }

        [JsonPropertyName("number_of_seasons")]
        public int? NumberOfSeasons { get; set; }

        [JsonPropertyName("genre_ids")]
        public List<int>? GenreIds { get; set; }

        /// <summary>只有 /search/multi 和 /trending/all 会带：movie、tv、person。</summary>
        [JsonPropertyName("media_type")]
        public string? MediaType { get; set; }

        public TmdbItem ToItem(MediaKind kind, string images = TmdbSettings.DefaultImageBase)
        {
            var title = (kind == MediaKind.Movie ? Title : Name) ?? Title ?? Name ?? string.Empty;
            var original = kind == MediaKind.Movie ? OriginalTitle : OriginalName;
            var date = kind == MediaKind.Movie ? ReleaseDate : FirstAirDate;
            int? year = date is { Length: >= 4 } && int.TryParse(date[..4], out var y) ? y : null;
            return new TmdbItem(
                Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                kind,
                title,
                string.Equals(original, title, StringComparison.Ordinal) ? null : original,
                year,
                Overview,
                string.IsNullOrEmpty(PosterPath) ? null : images + PosterSize + PosterPath,
                VoteAverage,
                NumberOfSeasons,
                GenreIds);
        }
    }
}
