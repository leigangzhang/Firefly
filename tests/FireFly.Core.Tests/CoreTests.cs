using System.Security.Cryptography;
using FireFly.Core;
using FireFly.Core.Downloads;
using FireFly.Core.Library;
using FireFly.Core.Licensing;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using FireFly.Core.Storage;
using Xunit;

namespace FireFly.Core.Tests;

public class ReleaseParserTests
{
    private const string Uhd = "【高清影视之家发布 www.HDBTHD.com】一战再战[HDR+杜比视界双版本][国英多音轨+特效中文字幕].2025.USA.V2.Bluray.2160p.TrueHD7.1.DoVi.HDR10.x265.10bit-DreamHD";

    [Fact]
    public void ParsesRealUhdTitle()
    {
        var t = ReleaseParser.Parse(Uhd);
        Assert.Equal(2160, t.Resolution);
        Assert.Equal("H.265", t.Codec);
        Assert.Equal("杜比视界+HDR", t.Hdr);
        Assert.Equal("BluRay", t.MediaSource);
        Assert.Equal("TrueHD", t.Audio);
        Assert.Equal(2025, t.Year);
        Assert.Equal("HDBTHD", t.Publisher);
        Assert.Equal("国英多音轨+特效中文字幕", t.Subtitles);
        Assert.Null(t.Season);
    }

    [Theory]
    [InlineData("【高清剧集网发布 www.DDHDTV.com】王冠 第二季[全10集][简繁英字幕].2017.1080p.NF.WEB-DL.DDP5.1.H264", 2)]
    [InlineData("The.Crown.S02.1080p.BluRay.x264", 2)]
    [InlineData("The.Crown.S01E05.1080p.WEB-DL", 1)]
    [InlineData("Rick and Morty Season 3 1080p", 3)]
    [InlineData("灵笼 第十二季 2160p", 12)]
    [InlineData("奥本海默.2023.2160p.BluRay.DTS-HD.MA.5.1", null)]
    public void ParsesSeason(string name, int? season) => Assert.Equal(season, ReleaseParser.ParseSeason(name));

    [Theory]
    [InlineData("The Crown S02E05 1080p", 5)]
    [InlineData("王冠.第03集", 3)]
    [InlineData("Show.EP07.2160p", 7)]
    [InlineData("Show.E12", 12)]
    [InlineData("Oppenheimer.2023.2160p", null)]
    public void ParsesEpisode(string name, int? episode) => Assert.Equal(episode, ReleaseParser.ParseEpisode(name));

    [Theory]
    [InlineData("十", 10)]
    [InlineData("十二", 12)]
    [InlineData("二十", 20)]
    [InlineData("二十三", 23)]
    [InlineData("三", 3)]
    public void ParsesChineseNumbers(string raw, int expected) => Assert.Equal(expected, ReleaseParser.ChineseNumber(raw));

    [Fact]
    public void ParsesDdhdtvPublisherAnd1080p()
    {
        var t = ReleaseParser.Parse("【高清剧集网发布 www.DDHDTV.com】苦尽柑来遇见你[全16集][中文字幕].2025.1080p.NF.WEB-DL.H264.DDP2.0-ZeroTV");
        Assert.Equal("DDHDTV", t.Publisher);
        Assert.Equal(1080, t.Resolution);
        Assert.Equal("WEB-DL", t.MediaSource);
        Assert.Equal("H.264", t.Codec);
        Assert.Equal("DDP", t.Audio);
        Assert.Equal("中文字幕", t.Subtitles);
    }
}

public class SearchServiceTests
{
    private static readonly RulesDocument Rules = RulesLoader.LoadDefault();

    private static Release R(string hash, string name, params (string Path, long Size)[] files) => new()
    {
        InfoHash = hash,
        Name = name,
        Magnet = "magnet:?xt=urn:btih:" + hash,
        Files = files.Select(f => new ReleaseFile(f.Path, f.Size)).ToList(),
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + hash.Length),
        Traits = ReleaseParser.Parse(name),
    };

    private const long Gb = 1L << 30;

    private sealed class FakeSource : IResourceSource
    {
        private readonly Func<string, IReadOnlyList<Release>> _answer;

        public FakeSource(Func<string, IReadOnlyList<Release>> answer) => _answer = answer;

        public List<string> Asked { get; } = new();

        public string Name => "fake";

        public Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct)
        {
            Asked.Add(keyword);
            return Task.FromResult(_answer(keyword));
        }
    }

    [Fact]
    public async Task FallsBackToTitleOnlyWhenPublisherTierIsEmpty()
    {
        var source = new FakeSource(k => k == "奥本海默"
            ? new[] { R("A1", "奥本海默.2023.2160p.BluRay.x265", ("Oppenheimer.mkv", 40 * Gb)) }
            : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });

        var result = await service.SearchAsync(new MediaQuery { Kind = MediaKind.Movie, Title = "奥本海默", Year = 2023 }, false, default);

        Assert.Equal(new[] { "奥本海默 DDHDTV", "奥本海默 高清剧集网", "奥本海默" }, source.Asked);
        Assert.Single(result.Releases);
    }

    [Fact]
    public void FiltersWrongYearWrongSeasonSingleEpisodesAndNoise()
    {
        var service = new SearchService(Array.Empty<IResourceSource>, () => Rules, Array.Empty<string>);
        var movie = new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘", Year = 2021 };
        var movieKept = service.Filter(new[]
        {
            R("M1", "沙丘.2021.2160p", ("Dune.mkv", 30 * Gb)),
            R("M2", "沙丘.1984.1080p", ("Dune.1984.mkv", 10 * Gb)),
            R("M3", "沙丘.2021.sample", ("sample.mkv", 10 << 20)),
            R("M4", "完全不相关的片.2021", ("x.mkv", 5 * Gb)),
            R("M1", "沙丘.2021.2160p 重复", ("Dune.mkv", 30 * Gb)),
        }, movie);
        Assert.Equal(new[] { "M1" }, movieKept.Select(r => r.InfoHash));

        var season2 = new MediaQuery { Kind = MediaKind.Series, Title = "王冠", Year = 2016, Season = 2 };
        var seriesKept = service.Filter(new[]
        {
            R("S2", "王冠 第二季 1080p", ("E01.mkv", 2 * Gb), ("E02.mkv", 2 * Gb)),
            R("S1", "王冠 第一季 1080p", ("E01.mkv", 2 * Gb), ("E02.mkv", 2 * Gb)),
            R("S2E1", "王冠.S02E01.1080p", ("E01.mkv", 2 * Gb)),
            R("NONE", "王冠 1080p", ("E01.mkv", 2 * Gb), ("E02.mkv", 2 * Gb)),
        }, season2);
        Assert.Equal(new[] { "S2" }, seriesKept.Select(r => r.InfoHash));
    }

    [Fact]
    public void OrdersByPublisherThenResolutionThenDate()
    {
        var ordered = SearchService.Order(new[]
        {
            R("A", "x 2160p", ("a.mkv", Gb)),
            R("BB", "【高清剧集网发布 www.DDHDTV.com】x 1080p", ("a.mkv", Gb)),
            R("CCC", "x 2160p newer", ("a.mkv", Gb)),
        }, new[] { "DDHDTV" });
        Assert.Equal(new[] { "BB", "CCC", "A" }, ordered.Select(r => r.InfoHash));
    }

    [Fact]
    public void DiaoTorrentMapsToRelease()
    {
        var release = DiaoSource.ToRelease(new DiaoSource.DiaoTorrent
        {
            Hash = "abcdef",
            Name = "【高清剧集网发布 www.DDHDTV.com】测试.2024.1080p",
            Size = 123,
            CreatedAt = 1_787_365_846,
            Files = new() { new DiaoSource.DiaoFile { Path = "dir/a.mkv", Size = 100 } },
        });
        Assert.Equal("ABCDEF", release.InfoHash);
        Assert.Equal("magnet:?xt=urn:btih:ABCDEF", release.Magnet);
        Assert.Equal("DDHDTV", release.Traits.Publisher);
        Assert.Single(release.Files);
    }
}

public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "firefly-tests-" + Guid.NewGuid().ToString("N"));

    public string File(string relative, long size)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        using var fs = new FileStream(full, FileMode.Create);
        fs.SetLength(size);
        return full;
    }

    public void Dispose() => Directory.Delete(Path, true);
}

public class DownloadTests
{
    private static readonly RulesDocument Rules = RulesLoader.LoadDefault();
    private const long Mb = 1 << 20;

    private static Release Movie => new()
    {
        InfoHash = "H1",
        Name = "【发布】沙丘.2021.2160p",
        Magnet = "magnet:?xt=urn:btih:H1",
        Files = new[] { new ReleaseFile("Dune.2021.2160p.mkv", 100 * Mb), new ReleaseFile("readme.txt", 1) },
    };

    [Fact]
    public void ProbeSeesTempFileThenCompletedFile()
    {
        using var dir = new TempDir();
        var downloader = new MacThunderDownloader(() => dir.Path, () => Rules, (_, _) => Task.FromResult(0), (_, _) => Task.CompletedTask);
        var task = new DownloadTask { Release = Movie, SubmittedAt = DateTimeOffset.UtcNow };

        Assert.Equal(0, downloader.Probe(task).BytesDone);

        var temp = dir.File("【发布】沙丘.2021.2160p/Dune.2021.2160p.mkv.xltd", 40 * Mb);
        var p1 = downloader.Probe(task);
        Assert.Equal(40 * Mb, p1.BytesDone);
        Assert.False(p1.Complete);

        System.IO.File.Delete(temp);
        dir.File("【发布】沙丘.2021.2160p/Dune.2021.2160p.mkv", 100 * Mb);
        var p2 = downloader.Probe(task);
        Assert.True(p2.Complete);
        Assert.Single(p2.CompletedFiles);
    }

    [Fact]
    public void FinalNameWithTempSiblingIsNotComplete()
    {
        using var dir = new TempDir();
        dir.File("Dune.2021.2160p.mkv", 100 * Mb);
        dir.File("Dune.2021.2160p.mkv.td.cfg", 1);
        var downloader = new MacThunderDownloader(() => dir.Path, () => Rules, (_, _) => Task.FromResult(0), (_, _) => Task.CompletedTask);
        Assert.False(downloader.Probe(new DownloadTask { Release = Movie }).Complete);
    }

    private sealed class FakeHost : IMediaHost
    {
        public FakeHost(string movies, string series) => Paths = new LibraryPaths(movies, series);

        public LibraryPaths Paths { get; }

        public int Refreshes { get; private set; }

        public LibraryPaths GetLibraryPaths() => Paths;

        public void RequestRefresh() => Refreshes++;

        public bool Contains(MediaQuery query) => false;
    }

    [Fact]
    public async Task WatcherWalksTaskToDoneAndArchivesWithStandardName()
    {
        using var dir = new TempDir();
        var downloads = System.IO.Path.Combine(dir.Path, "dl");
        var host = new FakeHost(System.IO.Path.Combine(dir.Path, "Movies"), System.IO.Path.Combine(dir.Path, "TV"));
        var store = new JsonStore<TaskList>(System.IO.Path.Combine(dir.Path, "state", "tasks.json"));
        var quota = new QuotaLedger(new JsonStore<QuotaState>(System.IO.Path.Combine(dir.Path, "state", "quota.json")), LicenseTests.Verifier(out _), () => null, 5);
        string? opened = null;
        var downloader = new MacThunderDownloader(() => downloads, () => Rules, (url, _) => { opened = url; return Task.FromResult(0); }, (_, _) => Task.CompletedTask);
        var app = new FireFlyApp(store, downloader, quota);
        var clock = DateTimeOffset.UtcNow;
        var watcher = new DownloadWatcher(store, downloader, new Archiver(host, () => Rules), now: () => clock);

        var query = new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘", Year = 2021, TmdbId = "438631" };
        var task = await app.SubmitAsync(query, Movie, default);
        Assert.Equal("magnet:?xt=urn:btih:H1", opened);
        Assert.Equal(1, quota.Status().Used);

        Directory.CreateDirectory(downloads);
        var temp = System.IO.Path.Combine(downloads, "Dune.2021.2160p.mkv.xltd");
        using (var fs = new FileStream(temp, FileMode.Create)) { fs.SetLength(10 * Mb); }
        clock = clock.AddSeconds(10);
        watcher.Tick();
        Assert.Equal(TaskState.Downloading, app.ListTasks()[0].State);

        System.IO.File.Move(temp, System.IO.Path.Combine(downloads, "Dune.2021.2160p.mkv"));
        using (var fs = new FileStream(System.IO.Path.Combine(downloads, "Dune.2021.2160p.mkv"), FileMode.Open)) { fs.SetLength(100 * Mb); }
        System.IO.File.WriteAllText(System.IO.Path.Combine(downloads, "Dune.2021.2160p.chs.srt"), "1");
        clock = clock.AddSeconds(10);
        watcher.Tick();
        Assert.Equal(TaskState.Downloading, app.ListTasks()[0].State);
        clock = clock.AddSeconds(10);
        watcher.Tick();

        var done = app.ListTasks()[0];
        Assert.Equal(TaskState.Done, done.State);
        var expected = System.IO.Path.Combine(host.Paths.MovieRoot!, "沙丘 (2021)", "沙丘 (2021).mkv");
        Assert.True(System.IO.File.Exists(expected));
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(host.Paths.MovieRoot!, "沙丘 (2021)", "沙丘 (2021).chs.srt")));
        Assert.Equal(1, host.Refreshes);

        // 同一个任务再提交不会重复建任务，也不会重复扣额度
        await app.SubmitAsync(query, Movie with { InfoHash = "H2", Magnet = "magnet:?xt=urn:btih:H2" }, default);
        Assert.Equal(1, quota.Status().Used);
    }

    [Fact]
    public async Task WatcherCleansReleaseFolderAfterArchive()
    {
        using var dir = new TempDir();
        var downloads = System.IO.Path.Combine(dir.Path, "dl");
        var releaseDir = System.IO.Path.Combine(downloads, "Dune.2021.2160p");
        var host = new FakeHost(System.IO.Path.Combine(dir.Path, "Movies"), System.IO.Path.Combine(dir.Path, "TV"));
        var store = new JsonStore<TaskList>(System.IO.Path.Combine(dir.Path, "state", "tasks.json"));
        var quota = new QuotaLedger(new JsonStore<QuotaState>(System.IO.Path.Combine(dir.Path, "state", "quota.json")), LicenseTests.Verifier(out _), () => null, 5);
        var downloader = new MacThunderDownloader(() => downloads, () => Rules, (_, _) => Task.FromResult(0), (_, _) => Task.CompletedTask);
        var app = new FireFlyApp(store, downloader, quota);
        var clock = DateTimeOffset.UtcNow;
        var watcher = new DownloadWatcher(store, downloader, new Archiver(host, () => Rules), now: () => clock);

        var query = new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘", Year = 2021, TmdbId = "438631" };
        await app.SubmitAsync(query, Movie, default);

        Directory.CreateDirectory(releaseDir);
        var video = System.IO.Path.Combine(releaseDir, "Dune.2021.2160p.mkv");
        using (var fs = new FileStream(video, FileMode.Create)) { fs.SetLength(100 * Mb); }
        System.IO.File.WriteAllText(System.IO.Path.Combine(releaseDir, "advert.torrent"), "x");

        clock = clock.AddSeconds(10);
        watcher.Tick();
        clock = clock.AddSeconds(10);
        watcher.Tick();

        var task = app.ListTasks()[0];
        Assert.Equal(TaskState.Done, task.State);
        Assert.Contains("已清理 1 个下载目录", task.Message);
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(host.Paths.MovieRoot!, "沙丘 (2021)", "沙丘 (2021).mkv")));
        Assert.False(Directory.Exists(releaseDir));
        Assert.True(Directory.Exists(downloads));
    }

    [Fact]
    public async Task SubmitSetsThunderSavePathBeforeOpeningMagnet()
    {
        using var dir = new TempDir();
        var target = System.IO.Path.Combine(dir.Path, "downloads");
        var calls = new List<string>();
        var downloader = new MacThunderDownloader(
            () => target,
            () => Rules,
            (_, _) => { calls.Add("open"); return Task.FromResult(0); },
            (path, _) => { calls.Add("save:" + path); return Task.CompletedTask; });

        await downloader.SubmitAsync(Movie, default);

        Assert.True(Directory.Exists(target));
        Assert.Equal(new[] { "save:" + target + System.IO.Path.DirectorySeparatorChar, "open" }, calls);
    }

    [Fact]
    public void WatcherMarksStalledWhenNothingAppears()
    {
        using var dir = new TempDir();
        var store = new JsonStore<TaskList>(System.IO.Path.Combine(dir.Path, "tasks.json"));
        var clock = DateTimeOffset.UtcNow;
        store.Update(l => { l.Tasks.Add(new DownloadTask { Release = Movie, SubmittedAt = clock }); return 0; });
        var downloader = new MacThunderDownloader(() => dir.Path, () => Rules, (_, _) => Task.FromResult(0), (_, _) => Task.CompletedTask);
        var watcher = new DownloadWatcher(store, downloader, new Archiver(new FakeHost(dir.Path, dir.Path), () => Rules), now: () => clock);

        clock = clock.AddMinutes(11);
        watcher.Tick();
        Assert.Equal(TaskState.Stalled, store.Read(l => l.Tasks[0].State));
    }

    [Fact]
    public void EpisodeNamingUsesSeasonFolders()
    {
        var q = new MediaQuery { Kind = MediaKind.Series, Title = "王冠", Year = 2016, Season = 2 };
        Assert.Equal(System.IO.Path.Combine("/tv", "王冠 (2016)", "Season 02", "王冠 S02E05.mkv"), LibraryNamer.EpisodeTarget("/tv", q, "/dl/The.Crown.S02E05.1080p.MKV"));
        Assert.Equal(System.IO.Path.Combine("/tv", "王冠 (2016)", "Season 02", "花絮.mkv"), LibraryNamer.EpisodeTarget("/tv", q, "/dl/花絮.mkv"));
        Assert.Equal("A B", LibraryNamer.SafeName("A:/B"));
    }
}

public class LicenseTests
{
    internal static LicenseVerifier Verifier(out string privateKey, Func<DateTimeOffset>? now = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        privateKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        return new LicenseVerifier(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), now);
    }

    [Fact]
    public void IssuedCodeVerifiesAndTamperingFails()
    {
        var verifier = Verifier(out var priv);
        var code = LicenseVerifier.Issue(new LicensePayload { Subject = "buyer-1", Plan = "yearly", Expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds() }, priv);

        Assert.True(verifier.Verify(code).IsVip);
        Assert.False(verifier.Verify(code[..^3] + "AAA").IsVip);
        Assert.False(verifier.Verify("garbage").IsVip);
        Assert.False(verifier.Verify(null).IsVip);
    }

    [Fact]
    public void ExpiredCodeIsNotVipButLifetimeIs()
    {
        var verifier = Verifier(out var priv);
        var expired = LicenseVerifier.Issue(new LicensePayload { Subject = "b", Plan = "monthly", Expires = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds() }, priv);
        var lifetime = LicenseVerifier.Issue(new LicensePayload { Subject = "b", Plan = "lifetime", Expires = 0 }, priv);

        Assert.Equal("授权已到期", verifier.Verify(expired).Status);
        Assert.True(verifier.Verify(lifetime).IsVip);
    }

    [Fact]
    public void QuotaCountsDistinctTitlesPerMonthAndResets()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var ledger = new QuotaLedger(new JsonStore<QuotaState>(System.IO.Path.Combine(dir.Path, "q.json")), Verifier(out _), () => null, 2, () => now);

        ledger.Record("movie:a");
        ledger.Record("movie:a");
        ledger.Record("movie:b");
        Assert.Equal(2, ledger.Status().Used);
        Assert.True(ledger.CanSubmit("movie:a"));
        Assert.False(ledger.CanSubmit("movie:c"));

        now = now.AddDays(3);
        Assert.Equal(0, ledger.Status().Used);
        Assert.True(ledger.CanSubmit("movie:c"));
    }
}
