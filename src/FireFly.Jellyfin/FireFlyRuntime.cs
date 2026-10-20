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
    private readonly object _tmdbHttpLock = new();
    private (string Proxy, HttpClient Client)? _tmdbHttp;
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
        Favorites = new Favorites(new JsonStore<FavoriteList>(Path.Combine(dataDir, "favorites.json")));
        Downloader = new MacThunderDownloader(() => DownloadDir, () => _rules);
        Search = new SearchService(() => _sources, () => _rules, Keywords);
        Tmdb = new TmdbClient(TmdbHttp, () => new TmdbSettings(
            Config.TmdbApiKey.Trim(),
            string.IsNullOrWhiteSpace(Config.TmdbLanguage) ? "zh-CN" : Config.TmdbLanguage.Trim(),
            Config.TmdbApiBase,
            Config.TmdbImageBase));
        App = new FireFlyApp(Tasks, Downloader);
        Watcher = new DownloadWatcher(Tasks, Downloader, new Archiver(host, () => _rules));
    }

    public string DataDir { get; }

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

    /// <summary>TMDb 用的 HttpClient：没配代理时和搜索源共用一个；配了代理时按代理地址建一个，代理改了就换新的。</summary>
    private HttpClient TmdbHttp()
    {
        var proxy = Config.TmdbProxy.Trim();
        if (proxy.Length == 0)
        {
            return _http;
        }

        lock (_tmdbHttpLock)
        {
            if (_tmdbHttp is { } cached && cached.Proxy == proxy)
            {
                return cached.Client;
            }

            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri))
            {
                throw new InvalidOperationException($"TMDb 代理地址不正确：{proxy}");
            }

            // 旧客户端可能还有请求在用，不主动释放；代理很少改，留给 GC
            var client = new HttpClient(new HttpClientHandler { Proxy = new System.Net.WebProxy(proxyUri), UseProxy = true }) { Timeout = _http.Timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) FireFly/0.1");
            _tmdbHttp = (proxy, client);
            return client;
        }
    }

    public static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;
}
