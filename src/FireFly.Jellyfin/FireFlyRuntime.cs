using FireFly.Core;
using FireFly.Core.Downloads;
using FireFly.Core.Library;
using FireFly.Core.Licensing;
using FireFly.Core.Metadata;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using FireFly.Core.Storage;
using FireFly.Jellyfin.Configuration;

namespace FireFly.Jellyfin;

/// <summary>组装根：把 Core 的各个部件按插件配置接起来。整个插件进程只有一份。</summary>
public sealed class FireFlyRuntime
{
    /// <summary>授权公钥（SubjectPublicKeyInfo，base64）。用 tools/FireFly.LicenseTool keygen 生成后填进来；为空时只有免费额度。</summary>
    public const string LicensePublicKey = "";

    /// <summary>每月免费部数。</summary>
    public const int FreeMonthlyQuota = 5;

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
        Downloader = new MacThunderDownloader(() => DownloadDir, () => _rules);
        Search = new SearchService(() => _sources, () => _rules, Keywords);
        Tmdb = new TmdbClient(_http, () => (Config.TmdbApiKey.Trim(), string.IsNullOrWhiteSpace(Config.TmdbLanguage) ? "zh-CN" : Config.TmdbLanguage.Trim()));
        Quota = new QuotaLedger(
            new JsonStore<QuotaState>(Path.Combine(dataDir, "quota.json")),
            new LicenseVerifier(LicensePublicKey),
            () => Config.LicenseCode,
            FreeMonthlyQuota);
        App = new FireFlyApp(Tasks, Downloader, Quota);
        Watcher = new DownloadWatcher(Tasks, Downloader, new Archiver(host, () => _rules));
    }

    public string DataDir { get; }

    public IMediaHost Host { get; }

    public JsonStore<TaskList> Tasks { get; }

    public MacThunderDownloader Downloader { get; }

    public SearchService Search { get; }

    public TmdbClient Tmdb { get; }

    public QuotaLedger Quota { get; }

    public FireFlyApp App { get; }

    public DownloadWatcher Watcher { get; }

    public SleepGuard SleepGuard { get; } = new();

    public RulesDocument Rules => _rules;

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

    public static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;
}
