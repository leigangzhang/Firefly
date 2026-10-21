using FireFly.Core;
using FireFly.Core.Downloads;
using FireFly.Core.Library;
using FireFly.Core.Metadata;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using FireFly.Core.Storage;
using FireFly.Jellyfin.Configuration;

namespace FireFly.Jellyfin;

/// <summary>组装根：把 Core 的各个部件按插件配置接起来。整个插件进程只有一份。</summary>
public sealed class FireFlyRuntime
{
    private readonly HttpClient _http;
    private RulesDocument _rules;
    private IReadOnlyList<IResourceSource> _sources;

    public FireFlyRuntime(IMediaHost host, string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) FireFly/0.1");
        _rules = RulesLoader.Load(Path.Combine(dataDir, "rules.json"));
        _sources = SourceFactory.Create(_rules, _http);

        Host = host;
        Tasks = new JsonStore<TaskList>(Path.Combine(dataDir, "tasks.json"));
        MetadataCache = new DiskCache(Path.Combine(dataDir, "cache", "tmdb"), ".json", MetadataCacheBytes);
        ImageCache = new DiskCache(Path.Combine(dataDir, "cache", "images"), ".img", ImageCacheBytes);
        MetadataCache.Prune();
        ImageCache.Prune();
        Favorites = new Favorites(new JsonStore<FavoriteList>(Path.Combine(dataDir, "favorites.json")));
        Downloader = new MacThunderDownloader(() => DownloadDir, () => _rules);
        Search = new SearchService(() => _sources, () => _rules, Keywords);
        Tmdb = new TmdbClient(_http, () => new TmdbSettings(
            Config.TmdbApiKey.Trim(),
            TmdbLanguage,
            Pick(Config.TmdbApiBase, _rules.Tmdb.ApiBases),
            Pick(Config.TmdbImageBase, _rules.Tmdb.ImageBases)),
            MetadataCache,
            ImageCache);
        App = new FireFlyApp(Tasks, Downloader);
        Watcher = new DownloadWatcher(Tasks, Downloader, new Archiver(host, () => _rules));
    }

    /// <summary>TMDb 元数据落盘缓存上限。</summary>
    public const long MetadataCacheBytes = 50L * 1024 * 1024;

    /// <summary>海报等图片缓存上限。</summary>
    public const long ImageCacheBytes = 500L * 1024 * 1024;

    public string DataDir { get; }

    public DiskCache MetadataCache { get; }

    public DiskCache ImageCache { get; }

    public IMediaHost Host { get; }

    public JsonStore<TaskList> Tasks { get; }

    public Favorites Favorites { get; }

    public MacThunderDownloader Downloader { get; }

    public SearchService Search { get; }

    public TmdbClient Tmdb { get; }

    public FireFlyApp App { get; }

    public DownloadWatcher Watcher { get; }

    public SleepGuard SleepGuard { get; } = new();

    public RulesDocument Rules => _rules;

    public bool ShowTrailers => Config.ShowTrailers;

    public string TmdbLanguage => string.IsNullOrWhiteSpace(Config.TmdbLanguage) ? "zh-CN" : Config.TmdbLanguage.Trim();

    public int SourceCount => _sources.Count;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public string DownloadDir =>
        string.IsNullOrWhiteSpace(Config.ThunderDownloadDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FireFly")
            : ExpandHome(Config.ThunderDownloadDir.Trim());

    public IReadOnlyList<string> Keywords()
    {
        var custom = Config.PublisherKeywords
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return custom.Count > 0 ? custom : _rules.PublisherKeywords;
    }

    /// <summary>配置里没选地址时用规则里的第一项；规则里也没有时交给 TmdbSettings 的默认值。</summary>
    private static string? Pick(string configured, IReadOnlyList<FilterOption> options) =>
        string.IsNullOrWhiteSpace(configured) ? options.FirstOrDefault()?.Value : configured;

    public static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;
}
