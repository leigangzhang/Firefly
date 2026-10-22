using System.Collections.Concurrent;
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
    private readonly object _rulesLock = new();
    private readonly ConcurrentDictionary<string, SourceReport> _health = new(StringComparer.OrdinalIgnoreCase);
    private RulesDocument _rules;
    private IReadOnlyList<IResourceSource> _sources;
    private string _loadedOverlay = string.Empty;
    private DateTime _loadedRulesFile;
    private DateTimeOffset _checkedAt;

    public FireFlyRuntime(IMediaHost host, string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) FireFly/0.1");
        MigrateKeywords();
        _rules = LoadRules(out _loadedOverlay, out _loadedRulesFile);
        _sources = SourceFactory.Create(_rules, _http);
        _checkedAt = DateTimeOffset.UtcNow;

        Host = host;
        Tasks = new JsonStore<TaskList>(Path.Combine(dataDir, "tasks.json"));
        MetadataCache = new DiskCache(Path.Combine(dataDir, "cache", "tmdb"), ".json", MetadataCacheBytes);
        ImageCache = new DiskCache(Path.Combine(dataDir, "cache", "images"), ".img", ImageCacheBytes);
        MetadataCache.Prune();
        ImageCache.Prune();
        Favorites = new Favorites(new JsonStore<FavoriteList>(Path.Combine(dataDir, "favorites.json")));
        Downloader = new MacThunderDownloader(() => DownloadDir, () => Rules);
        Search = new SearchService(() => Rules, category => SearchPlanner.Plan(Rules, Sources, category))
        {
            Report = r => _health[r.Source] = r,
        };
        Tmdb = new TmdbClient(_http, () => new TmdbSettings(
            Config.TmdbApiKey.Trim(),
            TmdbLanguage,
            Pick(Config.TmdbApiBase, Rules.Tmdb.ApiBases),
            Pick(Config.TmdbImageBase, Rules.Tmdb.ImageBases)),
            MetadataCache,
            ImageCache,
            () => Config.TmdbCacheEnabled);
        App = new FireFlyApp(Tasks, Downloader);
        Watcher = new DownloadWatcher(Tasks, Downloader, new Archiver(host, () => Rules));
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

    /// <summary>当前规则。配置页保存了新设置、或 rules.json 改过时自动重新加载（最多每 2 秒检查一次），不用重启 Jellyfin。</summary>
    public RulesDocument Rules
    {
        get
        {
            ReloadIfChanged();
            return _rules;
        }
    }

    public IReadOnlyList<IResourceSource> Sources
    {
        get
        {
            ReloadIfChanged();
            return _sources;
        }
    }

    public string RulesFilePath => Path.Combine(DataDir, "rules.json");

    public HttpClient Http => _http;

    /// <summary>各站点最近一次请求的结果（配置页的状态灯）。</summary>
    public IReadOnlyDictionary<string, SourceReport> Health => _health;

    public void RecordHealth(SourceReport report) => _health[report.Source] = report;

    /// <summary>内置规则叠加 rules.json（不含配置页那一层）的 JSON。</summary>
    public string LowerRulesJson(out List<string> warnings) => RulesLoader.LowerJson(RulesFilePath, out warnings);

    /// <summary>用一份还没保存的配置页设置算出规则（配置页预览、"试一试"用），不影响当前规则。</summary>
    /// <summary>按指定的配置页设置算规则；不传时用已保存的设置。</summary>
    public RulesDocument PreviewRules(string? overlay) => RulesLoader.Load(RulesFilePath, overlay ?? Config.RulesOverlay);

    private RulesDocument LoadRules(out string overlay, out DateTime fileTime)
    {
        overlay = Config.RulesOverlay ?? string.Empty;
        fileTime = File.Exists(RulesFilePath) ? File.GetLastWriteTimeUtc(RulesFilePath) : default;
        return RulesLoader.Load(RulesFilePath, overlay);
    }

    private void ReloadIfChanged()
    {
        var now = DateTimeOffset.UtcNow;
        var overlay = Config.RulesOverlay ?? string.Empty;
        if (overlay == _loadedOverlay && now - _checkedAt < RulesCheckInterval)
        {
            return;
        }

        lock (_rulesLock)
        {
            _checkedAt = now;
            var fileTime = File.Exists(RulesFilePath) ? File.GetLastWriteTimeUtc(RulesFilePath) : default;
            if (overlay == _loadedOverlay && fileTime == _loadedRulesFile)
            {
                return;
            }

            var rules = LoadRules(out _loadedOverlay, out _loadedRulesFile);
            _sources = SourceFactory.Create(rules, _http);
            _rules = rules;
            Search?.ClearCache();
        }
    }

    private static readonly TimeSpan RulesCheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 旧版配置页的"优选关键词"文本框是整体替换。迁移成配置页那一层的 keywords：
    /// 不在内置里的记为新增，内置里没填的记为停用，顺序照旧，搜索行为和以前一样。
    /// </summary>
    private void MigrateKeywords()
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (config is null || string.IsNullOrWhiteSpace(config.PublisherKeywords))
        {
            return;
        }

        var custom = config.PublisherKeywords
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var builtin = RulesLoader.Load(RulesFilePath).EffectiveKeywords().Select(k => k.Word).ToList();
        config.RulesOverlay = KeywordMigration.Overlay(config.RulesOverlay, custom, builtin);
        config.PublisherKeywords = string.Empty;
        plugin!.SaveConfiguration();
    }

    public bool ShowTrailers => Config.ShowTrailers;

    public string TmdbLanguage => string.IsNullOrWhiteSpace(Config.TmdbLanguage) ? "zh-CN" : Config.TmdbLanguage.Trim();

    public int SourceCount => Sources.Count;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public string DownloadDir =>
        string.IsNullOrWhiteSpace(Config.ThunderDownloadDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "FireFly")
            : ExpandHome(Config.ThunderDownloadDir.Trim());

    /// <summary>配置里没选地址时用规则里的第一项；规则里也没有时交给 TmdbSettings 的默认值。</summary>
    private static string? Pick(string configured, IReadOnlyList<FilterOption> options) =>
        string.IsNullOrWhiteSpace(configured) ? options.FirstOrDefault()?.Value : configured;

    public static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;
}
