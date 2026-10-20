using System.Security.Cryptography;
using FireFly.Core;
using FireFly.Core.Downloads;
using FireFly.Core.Library;
using FireFly.Core.Licensing;
using FireFly.Core.Metadata;
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

        public Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null)
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
    public async Task TriesOriginalTitleWithPublisherBeforeFallingBack()
    {
        var full = R("H3", "【高清剧集网发布 www.DDHDTV.com】龙之家族.第三季[全08集].House.of.the.Dragon.S03.2160p",
            ("House.of.the.Dragon.S03E01.mkv", 5 * Gb), ("House.of.the.Dragon.S03E02.mkv", 5 * Gb));
        var source = new FakeSource(k => k == "House of the Dragon DDHDTV" ? new[] { full } : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });
        var query = new MediaQuery
        {
            Kind = MediaKind.Series, Title = "权力的游戏前传：龙族", OriginalTitle = "House of the Dragon",
            AlternateTitles = new[] { "龙之家族" }, Year = 2022, Season = 3,
        };

        var result = await service.SearchAsync(query, false, default);

        // 冒号换成空格；剧集按 不带季号 / 第三季 / S03 三种写法搜
        Assert.Equal(
            new[] { "权力的游戏前传 龙族 DDHDTV", "权力的游戏前传 龙族 第三季 DDHDTV", "权力的游戏前传 龙族 S03 DDHDTV", "权力的游戏前传 龙族 高清剧集网", "权力的游戏前传 龙族 第三季 高清剧集网", "权力的游戏前传 龙族 S03 高清剧集网" },
            source.Asked.Take(6));
        Assert.Contains("House of the Dragon S03", source.Asked);
        Assert.Contains("House of the Dragon 第三季", source.Asked);
        Assert.Equal(new[] { "H3" }, result.Releases.Select(r => r.InfoHash));
    }

    [Fact]
    public async Task SeriesSeasonSearchAddsSingleEpisodeWhenOnlyFullSeasonFound()
    {
        var full = R("F1", "权力的游戏.第四季[全10集].Game.of.Thrones.S04.2160p",
            ("Game.of.Thrones.S04E01.mkv", 5 * Gb), ("Game.of.Thrones.S04E02.mkv", 5 * Gb));
        var single = R("S1", "权力的游戏.Game.of.Thrones.S04E01.1080p", ("Game.of.Thrones.S04E01.mkv", 3 * Gb));
        var source = new FakeSource(k => k == "权力的游戏 DDHDTV" ? new[] { full }
            : k == "权力的游戏 S04" ? new[] { single }
            : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });
        var query = new MediaQuery
        {
            Kind = MediaKind.Series, Title = "权力的游戏", OriginalTitle = "Game of Thrones", Year = 2011, Season = 4,
        };

        var result = await service.SearchAsync(query, false, default);

        Assert.Contains("权力的游戏 S04", source.Asked);
        Assert.Contains(result.Releases, r => r.InfoHash == "F1");
        Assert.Contains(result.Releases, r => r.InfoHash == "S1");
    }

    [Fact]
    public async Task SeasonSearchPrefersPublisherKeywordsBeforeBareFallback()
    {
        var full = R("F2", "权力的游戏.第四季[全10集].Game.of.Thrones.S04.2160p",
            ("Game.of.Thrones.S04E01.mkv", 5 * Gb), ("Game.of.Thrones.S04E02.mkv", 5 * Gb));
        var single = R("S2", "权力的游戏.Game.of.Thrones.S04E01.1080p", ("Game.of.Thrones.S04E01.mkv", 3 * Gb));
        var source = new FakeSource(k => k == "权力的游戏 DDHDTV" ? new[] { full }
            : k == "权力的游戏 S04 DDHDTV" ? new[] { single }
            : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });
        var query = new MediaQuery
        {
            Kind = MediaKind.Series, Title = "权力的游戏", OriginalTitle = "Game of Thrones", Year = 2011, Season = 4,
        };

        var result = await service.SearchAsync(query, false, default);

        var premiumIndex = source.Asked.IndexOf("权力的游戏 S04 DDHDTV");
        var bareIndex = source.Asked.IndexOf("权力的游戏 S04");
        Assert.True(premiumIndex >= 0);
        Assert.True(bareIndex < 0 || premiumIndex < bareIndex);
        Assert.Contains(result.Releases, r => r.InfoHash == "F2");
        Assert.Contains(result.Releases, r => r.InfoHash == "S2");
    }

    [Fact]
    public async Task SeasonSearchAppendsBareFallbackWhenAPackTypeHasFewerThanFive()
    {
        var full = R("F3", "权力的游戏.第四季[全10集].Game.of.Thrones.S04.2160p",
            ("Game.of.Thrones.S04E01.mkv", 5 * Gb), ("Game.of.Thrones.S04E02.mkv", 5 * Gb));
        var single = R("S3", "权力的游戏.Game.of.Thrones.S04E01.1080p", ("Game.of.Thrones.S04E01.mkv", 3 * Gb));
        var multi = Enumerable.Range(1, 5).Select(i => R($"M{i}", "权力的游戏.Game.of.Thrones.S04E01-E02.1080p",
            ("Game.of.Thrones.S04E01.mkv", 3 * Gb), ("Game.of.Thrones.S04E02.mkv", 3 * Gb))).ToList();
        var source = new FakeSource(k => k == "权力的游戏 DDHDTV" ? new[] { full }
            : k == "权力的游戏 S04 DDHDTV" ? new[] { single }
            : k == "权力的游戏 S04" ? multi
            : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });
        var query = new MediaQuery
        {
            Kind = MediaKind.Series, Title = "权力的游戏", OriginalTitle = "Game of Thrones", Year = 2011, Season = 4,
        };

        var result = await service.SearchAsync(query, false, default);

        Assert.Contains("权力的游戏 S04", source.Asked);
        Assert.Contains(result.Releases, r => r.InfoHash == "F3");
        Assert.Contains(result.Releases, r => r.InfoHash == "S3");
        Assert.All(multi, m => Assert.Contains(result.Releases, r => r.InfoHash == m.InfoHash));
    }

    [Fact]
    public void AlternateTitleCountsAsNameMatch()
    {
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "权力的游戏前传：龙族", AlternateTitles = new[] { "龙之家族" } };
        Assert.True(SearchService.NameMentions("龙之家族.第三季.1080p", query));
        Assert.False(SearchService.NameMentions("权力的游戏.第八季.1080p", query));
        Assert.Equal(new[] { "a", "b", "c", "d" }, SearchService.SearchTitles(new MediaQuery { Title = "a", OriginalTitle = "A", AlternateTitles = new[] { "b", "c", "d", "e" } }));
    }

    [Fact]
    public void SearchTitlesReplaceColonsWithSpace()
    {
        // 资源站实测："降世神通：七净土"、"降世神通七净土" 都是 0 条，"降世神通 七净土" 有结果；英文带冒号同样 0 条
        var query = new MediaQuery { Title = "蜘蛛侠：崭新之日", OriginalTitle = "Spider-Man: Brand New Day" };
        Assert.Equal(new[] { "蜘蛛侠 崭新之日", "Spider-Man Brand New Day" }, SearchService.SearchTitles(query));
    }

    [Theory]
    [InlineData(1, "一")]
    [InlineData(10, "十")]
    [InlineData(12, "十二")]
    [InlineData(20, "二十")]
    [InlineData(35, "三十五")]
    public void SeasonNumberInChinese(int n, string expected) => Assert.Equal(expected, SearchService.ChineseNumeral(n));

    [Fact]
    public async Task PreferredStageReturnsFirstThenFallbackAppends()
    {
        var pref = R("P1", "【高清剧集网发布 www.BPHDTV.com】黑帮领地[第10集].MobLand.S01.2160p", ("MobLand.S01E10.mkv", 3 * Gb));
        var bare = R("B1", "MobLand.S01E09.1080p.WEB", ("MobLand.S01E09.mkv", 2 * Gb));
        var source = new FakeSource(k => k == "MobLand 第一季 高清剧集网" ? new[] { pref } : k == "MobLand S01" ? new[] { bare } : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "高清剧集网" });
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "MobLand", Year = 2025, Season = 1 };

        var first = await service.SearchAsync(query, false, SearchStage.Preferred, default);
        Assert.True(first.NeedsFallback);
        Assert.Equal(new[] { "P1" }, first.Releases.Select(r => r.InfoHash));
        Assert.DoesNotContain("MobLand S01", source.Asked);

        var asked = source.Asked.Count;
        var second = await service.SearchAsync(query, true, SearchStage.Fallback, default);
        Assert.False(second.NeedsFallback);
        Assert.Equal(new[] { "P1", "B1" }, second.Releases.Select(r => r.InfoHash));
        // 降级阶段接着优选结果做，不重搜优选关键词
        Assert.DoesNotContain(source.Asked.Skip(asked), k => k.Contains("高清剧集网"));

        var cached = await service.SearchAsync(query, false, SearchStage.Preferred, default);
        Assert.True(cached.FromCache);
        Assert.False(cached.NeedsFallback);
        Assert.Equal(2, cached.Releases.Count);
    }

    [Fact]
    public async Task SearchesTitleWithColonReplacedBySpace()
    {
        var movie = R("C1", "沙丘：第二部.2024.2160p.BluRay", ("Dune.Part.Two.mkv", 40 * Gb));
        var source = new FakeSource(k => k == "沙丘 第二部 DDHDTV" ? new[] { movie } : Array.Empty<Release>());
        var service = new SearchService(() => new[] { source }, () => Rules, () => new[] { "DDHDTV", "高清剧集网" });

        var result = await service.SearchAsync(new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘：第二部", Year = 2024 }, false, default);

        Assert.Contains("沙丘 第二部 DDHDTV", source.Asked);
        Assert.DoesNotContain(source.Asked, k => k.Contains('：'));
        Assert.Equal(new[] { "C1" }, result.Releases.Select(r => r.InfoHash));
    }

    [Fact]
    public void ParsesChineseAlternativeTitles()
    {
        const string json = """
        {"original_name":"House of the Dragon","alternative_titles":{"results":[
          {"iso_3166_1":"US","title":"HOTD"},{"iso_3166_1":"TW","title":"龍族前傳"},{"iso_3166_1":"CN","title":"龙之家族"},{"iso_3166_1":"CN","title":"龙之家族"}]}}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var names = TmdbClient.ParseNames(MediaKind.Series, doc.RootElement);
        Assert.Equal("House of the Dragon", names.OriginalTitle);
        Assert.Equal(new[] { "龙之家族", "龍族前傳" }, names.AlternateTitles);
    }

    [Fact]
    public void FiltersWrongYearWrongSeasonAndNoise()
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
        // 单集种子也保留（前端放在"单集 / 分集"标签下）
        Assert.Equal(new[] { "S2", "S2E1" }, seriesKept.Select(r => r.InfoHash));
    }

    [Fact]
    public void TitleMustStandAloneInReleaseName()
    {
        var odyssey = new MediaQuery { Kind = MediaKind.Movie, Title = "奥德赛", OriginalTitle = "The Odyssey", Year = 2026 };
        Assert.True(SearchService.NameMentions("《奥德赛 (2026)》 电影 BD-1080p", odyssey));
        Assert.True(SearchService.NameMentions("The.Odyssey.2026.1080p.AMZN.WEB-DL", odyssey));
        Assert.False(SearchService.NameMentions("北极熊：一个夏天的奥德赛.720p.BD中文字幕", odyssey));
        Assert.False(SearchService.NameMentions("BBC.海洋奥德赛", odyssey));
        Assert.False(SearchService.NameMentions("TheOdysseys.2026", odyssey));

        var havens = new MediaQuery { Kind = MediaKind.Series, Title = "降世神通：七净土", OriginalTitle = "Avatar: Seven Havens", Season = 1 };
        Assert.True(SearchService.NameMentions("降世神通.七净土S01E04.1080p", havens));
        Assert.True(SearchService.NameMentions("www.UIndex.org    -    Avatar Seven Havens S01E04 1080p", havens));

        var below = new MediaQuery { Kind = MediaKind.Series, Title = "暗潮之下", OriginalTitle = "Below", Season = 1 };
        Assert.False(SearchService.NameMentions("Belowground.S01.1080p", below));
        Assert.True(SearchService.NameMentions("Below.2026.S01E01.1080p", below));
    }

    [Fact]
    public void FiltersAdultAndDemotesCamAndForeignOnly()
    {
        var service = new SearchService(Array.Empty<IResourceSource>, () => Rules, Array.Empty<string>);
        var movie = new MediaQuery { Kind = MediaKind.Movie, Title = "奥德赛", OriginalTitle = "The Odyssey", Year = 2026 };
        var kept = service.Filter(new[]
        {
            R("OK", "The.Odyssey.2026.1080p.WEB-DL", ("a.mkv", 3 * Gb)),
            R("XXX", "The Odyssey XXX. A Porn Parody (1080p)", ("a.mkv", 3 * Gb)),
        }, movie);
        Assert.Equal(new[] { "OK" }, kept.Select(r => r.InfoHash));

        Assert.Equal(ReleaseParser.CamSource, ReleaseParser.Parse("The.Odyssey.2026.1080p.TELESYNC.HEVC").MediaSource);
        Assert.Equal(ReleaseParser.CamSource, ReleaseParser.Parse("The-Odyssey-2026-1080p-TS-V2-WEB.DL").MediaSource);
        Assert.NotEqual(ReleaseParser.CamSource, ReleaseParser.Parse("Show.S01E02.1080p.WEB-DL.x264-K83.mkv.ts").MediaSource);
        Assert.True(ReleaseParser.Parse("Avatar.Seven.Havens.S01E03.rus.LostFilm.TV.avi").ForeignOnly);
        Assert.False(ReleaseParser.Parse("Avatar.S01E02.1080p.WEB-DL.DDP5.1.ENG.ITA.H264-TBK").ForeignOnly);

        var ordered = SearchService.Order(new[]
        {
            R("CAM", "The.Odyssey.2026.2160p.HDTS", ("a.mkv", Gb)),
            R("RUS", "The.Odyssey.2026.2160p.rus", ("a.mkv", Gb)),
            R("WEB", "The.Odyssey.2026.1080p.WEB-DL", ("a.mkv", Gb)),
        }, Array.Empty<string>());
        Assert.Equal(new[] { "WEB", "RUS", "CAM" }, ordered.Select(r => r.InfoHash));
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
    public void ProbeByNameAcceptsDisplayNameFolder()
    {
        using var dir = new TempDir();
        var video = dir.File(System.IO.Path.Combine("沙丘 (2021) 2160p", "a.mkv"), 100 * Mb);
        var release = Movie with { Files = Array.Empty<ReleaseFile>(), SizeBytes = 100 * Mb, DisplayName = "沙丘 (2021) 2160p" };
        var files = new[] { new FileInfo(video) };
        var p = MacThunderDownloader.ProbeByReleaseName(release, files, Rules);
        Assert.True(p.Complete);
        Assert.False(MacThunderDownloader.ProbeByReleaseName(release with { DisplayName = null }, files, Rules).Complete);
    }

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
        Assert.StartsWith("magnet:?xt=urn:btih:H1&dn=", opened);
        Assert.Equal("沙丘 (2021)", Uri.UnescapeDataString(opened!.Split("dn=")[1]));
        Assert.Equal("沙丘 (2021)", task.Release.DisplayName);
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
    public async Task SubmitRefusesNewTitleWhenQuotaIsFull()
    {
        using var dir = new TempDir();
        var store = new JsonStore<TaskList>(System.IO.Path.Combine(dir.Path, "state", "tasks.json"));
        var quota = new QuotaLedger(new JsonStore<QuotaState>(System.IO.Path.Combine(dir.Path, "state", "quota.json")), LicenseTests.Verifier(out _), () => null, 1);
        var opened = 0;
        var downloader = new MacThunderDownloader(() => System.IO.Path.Combine(dir.Path, "dl"), () => Rules, (_, _) => { opened++; return Task.FromResult(0); }, (_, _) => Task.CompletedTask);
        var app = new FireFlyApp(store, downloader, quota);

        var s1 = new MediaQuery { Kind = MediaKind.Series, Title = "降世神通：七净土", Year = 2026, Season = 1, TmdbId = "1" };
        await app.SubmitAsync(s1, Movie with { InfoHash = "A1", Magnet = "magnet:?xt=urn:btih:A1" }, default);
        Assert.Equal(1, quota.Status().Used);
        Assert.True(quota.IsCounted(s1.QuotaKey));

        // 同一季本月已计过数：额度用满后仍可下载这一季的其他版本，不再扣额度
        await app.SubmitAsync(s1, Movie with { InfoHash = "A2", Magnet = "magnet:?xt=urn:btih:A2" }, default);
        Assert.Equal(2, opened);
        Assert.Equal(1, quota.Status().Used);

        // 额度用满后，没计过数的季和电影都会被拒绝，也不会交给迅雷
        var s2 = s1 with { Season = 2 };
        Assert.False(quota.IsCounted(s2.QuotaKey));
        await Assert.ThrowsAsync<QuotaExceededException>(() => app.SubmitAsync(s2, Movie with { InfoHash = "B1", Magnet = "magnet:?xt=urn:btih:B1" }, default));
        var movie = new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘", Year = 2021, TmdbId = "438631" };
        await Assert.ThrowsAsync<QuotaExceededException>(() => app.SubmitAsync(movie, Movie, default));
        Assert.Equal(2, opened);
        Assert.Equal(2, app.ListTasks().Count);
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

public class DiscoverTests
{
    private static readonly DiscoverRule Rule = RulesLoader.LoadDefault().Discover;
    private static readonly DateOnly Today = new(2026, 10, 20);

    [Fact]
    public void DefaultRulesCarryDiscoverCatalog()
    {
        Assert.Contains(Rule.Categories, c => c.Key == "movie" && c.Kind == MediaKind.Movie);
        Assert.Contains(Rule.Categories, c => c.Key == "anime" && c.Kind == MediaKind.Series && c.WithGenres == "16");
        Assert.NotEmpty(Rule.Regions);
        Assert.NotEmpty(Rule.Companies);
        Assert.NotEmpty(Rule.Networks);
        Assert.NotEmpty(Rule.Certifications);
    }

    [Fact]
    public void MovieFiltersMapToDiscoverParameters()
    {
        var plan = DiscoverPlanner.Plan(Rule, new DiscoverSelection("movie", "35", "CN", "2010-2019", "174", "top", "PG-13", 3), Today);
        Assert.Equal("/discover/movie", plan.Path);
        var p = plan.Parameters;
        Assert.Equal("35", p["with_genres"]);
        Assert.Equal("CN", p["with_origin_country"]);
        Assert.Equal("2010-01-01", p["primary_release_date.gte"]);
        Assert.Equal("2019-12-31", p["primary_release_date.lte"]);
        Assert.Equal("174", p["with_companies"]);
        Assert.Equal("US", p["certification_country"]);
        Assert.Equal("PG-13", p["certification"]);
        Assert.Equal("vote_average.desc", p["sort_by"]);
        Assert.Equal("3", p["page"]);
    }

    [Fact]
    public void SeriesCategoriesUseNetworksAndIgnoreRating()
    {
        var plan = DiscoverPlanner.Plan(Rule, new DiscoverSelection("anime", "10759", null, "2025", "213", "new", "R"), Today);
        Assert.Equal("/discover/tv", plan.Path);
        var p = plan.Parameters;
        Assert.Equal("16,10759", p["with_genres"]);
        Assert.Equal("213", p["with_networks"]);
        Assert.False(p.ContainsKey("certification"));
        Assert.Equal("2025-01-01", p["first_air_date.gte"]);
        Assert.Equal("2025-12-31", p["first_air_date.lte"]);
        Assert.Equal("first_air_date.desc", p["sort_by"]);
    }

    [Fact]
    public void NewestOnlyShowsReleasedTitles()
    {
        var p = DiscoverPlanner.Plan(Rule, new DiscoverSelection("movie", Time: "2026", Sort: "new"), Today).Parameters;
        Assert.Equal("2026-10-20", p["primary_release_date.lte"]);
        var tv = DiscoverPlanner.Plan(Rule, new DiscoverSelection("tv"), Today).Parameters;
        Assert.Equal("popularity.desc", tv["sort_by"]);
        Assert.Contains("16", tv["without_genres"]);
    }

    [Fact]
    public void HotSeriesUseRecentWindowButExplicitTimeWins()
    {
        var tv = DiscoverPlanner.Plan(Rule, new DiscoverSelection("tv"), Today).Parameters;
        Assert.Equal("popularity.desc", tv["sort_by"]);
        Assert.Equal("2024-01-01", tv["first_air_date.gte"]);
        Assert.Equal("2026-10-20", tv["first_air_date.lte"]);

        var old = DiscoverPlanner.Plan(Rule, new DiscoverSelection("documentary", Time: "2010-2019", Sort: "hot"), Today).Parameters;
        Assert.Equal("2010-01-01", old["first_air_date.gte"]);
        Assert.Equal("2019-12-31", old["first_air_date.lte"]);

        var movie = DiscoverPlanner.Plan(Rule, new DiscoverSelection("movie"), Today).Parameters;
        Assert.False(movie.ContainsKey("primary_release_date.gte"));
    }

    [Fact]
    public void TimeOptionsAndRows()
    {
        var times = DiscoverPlanner.TimeOptions(2026).Select(o => o.Value).ToList();
        Assert.Equal(new[] { "2026", "2025", "2024", "2023", "2022", "2021", "2020", "2010-2019", "2000-2009", "1990-1999", "-1989" }, times);
        Assert.Equal((null, 1989), DiscoverPlanner.ParseTime("-1989"));

        var genres = new[] { new FilterOption("16", "动画"), new FilterOption("18", "剧情") };
        var movieRows = DiscoverPlanner.Rows(Rule, DiscoverPlanner.Category(Rule, "movie"), genres, 2026);
        Assert.Equal(new[] { "category", "genre", "region", "time", "company", "sort", "rating" }, movieRows.Select(r => r.Key));
        var animeRows = DiscoverPlanner.Rows(Rule, DiscoverPlanner.Category(Rule, "anime"), genres, 2026);
        Assert.DoesNotContain(animeRows, r => r.Key == "rating");
        Assert.Equal(new[] { "18" }, animeRows.Single(r => r.Key == "genre").Options.Select(o => o.Value));
        Assert.Equal(Rule.Networks, animeRows.Single(r => r.Key == "company").Options);
    }

    [Fact]
    public void ParsesMovieDetail()
    {
        const string json = """
        {"id":27205,"title":"盗梦空间","original_title":"Inception","original_language":"en","release_date":"2010-07-15",
         "overview":"简介","tagline":"梦境","poster_path":"/p.jpg","backdrop_path":"/b.jpg","vote_average":8.4,"vote_count":36000,
         "runtime":148,"status":"Released","genres":[{"id":28,"name":"动作"},{"id":878,"name":"科幻"}],"budget":160000000,"revenue":839000000,
         "production_companies":[{"name":"Legendary Pictures","logo_path":"/l.png"}],"production_countries":[{"iso_3166_1":"US"}],
         "videos":{"results":[{"key":"t1","site":"YouTube","type":"Teaser","name":"先导","iso_639_1":"en"},{"key":"t2","site":"YouTube","type":"Trailer","name":"预告","iso_639_1":"en"},{"key":"v","site":"Vimeo","type":"Trailer","name":"x"}]},
         "credits":{"cast":[{"name":"莱昂纳多","character":"柯布","profile_path":"/c.jpg"}],"crew":[{"name":"诺兰","job":"Director"},{"name":"诺兰","job":"Writer"},{"name":"某人","job":"Editor"}]},
         "release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"certification":""},{"certification":"PG-13"}]}]},
         "watch/providers":{"results":{"US":{"link":"https://tmdb/w","flatrate":[{"provider_name":"Netflix","logo_path":"/n.png"}],"rent":[{"provider_name":"Apple TV"}]}}}}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var d = TmdbClient.ParseDetail(MediaKind.Movie, doc.RootElement, new[] { "CN", "US" });
        Assert.Equal("27205", d.TmdbId);
        Assert.Equal("Inception", d.OriginalTitle);
        Assert.Equal(2010, d.Year);
        Assert.Equal(148, d.Runtime);
        Assert.Equal("PG-13", d.Certification);
        Assert.Equal(new[] { "动作", "科幻" }, d.Genres);
        Assert.Equal("导演 / 编剧", d.Directors.Single().Role);
        Assert.Equal("柯布", d.Cast.Single().Role);
        Assert.Equal(new[] { "t2", "t1" }, d.Videos.Select(v => v.Key));
        Assert.Equal("https://image.tmdb.org/t/p/w1280/b.jpg", d.BackdropUrl);
        Assert.Equal("US", d.Providers!.Region);
        Assert.Equal("Netflix", d.Providers.Stream.Single().Name);
        Assert.Single(d.Providers.Rent);
        Assert.Equal("Legendary Pictures", d.Companies.Single().Name);
        Assert.Equal(new[] { "US" }, d.Countries);
    }

    [Fact]
    public void ParsesSeriesDetail()
    {
        const string json = """
        {"id":4614,"name":"海军罪案调查处","original_name":"NCIS","first_air_date":"2003-09-23","status":"Returning Series",
         "episode_run_time":[],"number_of_seasons":23,"number_of_episodes":500,"origin_country":["US"],
         "created_by":[{"name":"Donald P. Bellisario"}],"networks":[{"name":"CBS","logo_path":"/cbs.png"}],
         "aggregate_credits":{"cast":[{"name":"Mark Harmon","roles":[{"character":"Gibbs"}],"total_episode_count":436}]},
         "content_ratings":{"results":[{"iso_3166_1":"US","rating":"TV-14"}]},
         "last_episode_to_air":{"season_number":1,"episode_number":23},
         "seasons":[{"season_number":0,"name":"特别篇","episode_count":3},{"season_number":1,"name":"第 1 季","episode_count":23,"air_date":"2003-09-23","overview":"季简介","vote_average":7.1},{"season_number":2,"name":"第 2 季","episode_count":10,"air_date":"2099-01-01"}]}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var d = TmdbClient.ParseDetail(MediaKind.Series, doc.RootElement, new[] { "CN", "US" });
        Assert.Equal("NCIS", d.OriginalTitle);
        Assert.Null(d.Runtime);
        Assert.Equal("TV-14", d.Certification);
        Assert.Equal("主创", d.Directors.Single().Role);
        Assert.Equal(436, d.Cast.Single().Episodes);
        Assert.Equal("Gibbs", d.Cast.Single().Role);
        Assert.Equal("CBS", d.Networks.Single().Name);
        Assert.Equal(new[] { 1, 2 }, d.Seasons.Select(x => x.Number));
        Assert.Equal(7.1, d.Seasons[0].Rating);
        Assert.Equal("季简介", d.Seasons[0].Overview);
        Assert.Equal(1, d.CurrentSeason);
        Assert.Null(d.Providers);
    }
}

public class ReleaseDisplayTests
{
    private static Release Rel(string name, int res = 1080, string? source = "WEB-DL") =>
        new() { InfoHash = "H", Name = name, Magnet = "magnet:?xt=urn:btih:H&dn=" + Uri.EscapeDataString(name) + "&tr=udp%3A%2F%2Ft", Traits = new ReleaseTraits { Resolution = res, MediaSource = source } };

    [Theory]
    [InlineData("【高清剧集网发布 www.BTHDTV.com】牵牛和仙女[全12集][简繁英字幕].Head.Over.Heels.S01.2025.1080p", "牵牛和仙女 S01 全12集")]
    [InlineData("【高清剧集网发布 www.BPHDTV.com】牵牛和仙女[第08-10集][简繁英字幕].Head.Over.Heels.S01", "牵牛和仙女 S01 第08-10集")]
    [InlineData("Head.Over.Heels.S01E05-E08.1080p", "牵牛和仙女 S01 第05-08集")]
    [InlineData("牵牛和仙女 第3集 www.xx.com", "牵牛和仙女 S01 第03集")]
    [InlineData("牵牛和仙女.1080p", "牵牛和仙女 S01")]
    public void SeriesNamesDropSourceInfo(string raw, string expected)
    {
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "牵牛和仙女", Year = 2025, Season = 1 };
        Assert.Equal(expected, ReleaseDisplay.Name(query, Rel(raw)));
    }

    [Fact]
    public void MovieNameAndMagnetDn()
    {
        var query = new MediaQuery { Kind = MediaKind.Movie, Title = "沙丘", Year = 2021 };
        var rel = Rel("沙丘.6v电影 地址发布页 www.6v123.com", 2160) with { Traits = new ReleaseTraits { Resolution = 2160, Hdr = "HDR" } };
        var name = ReleaseDisplay.Name(query, rel);
        Assert.Equal("沙丘 (2021)", name);

        var magnet = ReleaseDisplay.WithDisplayName(rel.Magnet, name);
        Assert.Equal("magnet:?xt=urn:btih:H&dn=" + Uri.EscapeDataString(name) + "&tr=udp%3A%2F%2Ft", magnet);
        Assert.DoesNotContain("6v123", magnet);
        Assert.Equal("magnet:?xt=urn:btih:X&dn=a", ReleaseDisplay.WithDisplayName("magnet:?xt=urn:btih:X", "a"));
    }
}

public class EpisodeCoverageTests
{
    private const long Gb = 1L << 30;
    private static readonly RulesDocument Rules = RulesLoader.LoadDefault();

    private static Release R(string name, params string[] videos) => new()
    {
        Name = name,
        Files = videos.Select(v => new ReleaseFile(v, 2 * Gb)).Append(new ReleaseFile("更多资源.png", 1 << 20)).ToList(),
        Traits = ReleaseParser.Parse(name),
    };

    [Fact]
    public void ClassifiesPacksAndEpisodes()
    {
        var full = ReleaseDisplay.Coverage(R("牵牛和仙女[全12集].S01.1080p", "E01.mkv", "E02.mkv"), Rules);
        Assert.True(full.FullSeason);

        var range = ReleaseDisplay.Coverage(R("牵牛和仙女[第08-10集].S01.1080p", "S01E08.mkv", "S01E09.mkv", "S01E10.mkv"), Rules);
        Assert.False(range.FullSeason);
        Assert.Equal(new[] { 8, 9, 10 }, range.Episodes);

        var single = ReleaseDisplay.Coverage(R("【高清剧集网发布 www.DDHDTV.com】龙之家族.第三季[第08集].House.of.the.Dragon.S03.2160p", "House.of.the.Dragon.S03E08.Episode.8.2160p.mkv"), Rules);
        Assert.False(single.FullSeason);
        Assert.Equal((8, 8), (single.From, single.To));

        var unnamed = ReleaseDisplay.Coverage(R("Show.S02.1080p", "Show.S02E05.mkv"), Rules);
        Assert.Equal(new[] { 5 }, unnamed.Episodes);

        var pack = ReleaseDisplay.Coverage(R("Show.S02.1080p", "Show.S02E01.mkv", "Show.S02E02.mkv"), Rules);
        Assert.True(pack.FullSeason);
        Assert.Equal((1, 2), (pack.From, pack.To));
    }

    [Fact]
    public void SeasonNamedPackWithFewerEpisodesIsPartial()
    {
        // 降世神通：七净土 S01 实测：名字只写季号、只含 E01-E03 的包
        var partial = ReleaseDisplay.Coverage(R("Avatar.Seven.Havens.S01", "Avatar.Seven.Havens.S01E01.1080p.mkv", "Avatar.Seven.Havens.S01E02.1080p.mkv", "Avatar.Seven.Havens.S01E03.1080p.mkv"), Rules, seasonEpisodes: 10);
        Assert.False(partial.FullSeason);
        Assert.Equal(new[] { 1, 2, 3 }, partial.Episodes);

        var complete = ReleaseDisplay.Coverage(R("Show.S01", "Show.S01E01.mkv", "Show.S01E02.mkv", "Show.S01E03.mkv"), Rules, seasonEpisodes: 3);
        Assert.True(complete.FullSeason);

        var lateStart = ReleaseDisplay.Coverage(R("Show.S01", "Show.S01E04.mkv", "Show.S01E05.mkv"), Rules);
        Assert.False(lateStart.FullSeason);
        Assert.Equal((4, 5), (lateStart.From, lateStart.To));

        // 名字写了"全N集"的仍按整季
        Assert.True(ReleaseDisplay.Coverage(R("牵牛和仙女[全12集].S01", "E01.mkv", "E02.mkv"), Rules, seasonEpisodes: 12).FullSeason);
    }

    [Fact]
    public void ParsesSeasonEpisodeCounts()
    {
        const string json = """
        {"original_name":"Avatar: Seven Havens","seasons":[{"season_number":0,"episode_count":2},{"season_number":1,"episode_count":10}]}
        """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var names = TmdbClient.ParseNames(MediaKind.Series, doc.RootElement);
        Assert.Equal(10, names.SeasonEpisodes![1]);
    }

    [Fact]
    public void SingleEpisodeDisplayNameShowsEpisode()
    {
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "权力的游戏前传：龙族", Season = 3 };
        var rel = R("House.of.the.Dragon.S03.2160p", "House.of.the.Dragon.S03E08.mkv");
        Assert.Equal("权力的游戏前传：龙族 S03 第08集", ReleaseDisplay.Name(query, rel));
    }
}

public class TitleSearchTests
{
    [Fact]
    public void SearchKeepsMoviesAndSeriesOnly()
    {
        const string json = """
        {"page":1,"total_pages":2,"total_results":30,"results":[
          {"id":1,"media_type":"movie","title":"沙丘","release_date":"2021-09-15","vote_average":7.8},
          {"id":2,"media_type":"person","name":"某演员"},
          {"id":3,"media_type":"tv","name":"沙丘：预言","first_air_date":"2024-11-17"}]}
        """;
        var page = System.Text.Json.JsonSerializer.Deserialize<TmdbClient.TmdbPage>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var result = TmdbClient.ToSearchPage(page);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(new[] { ("1", MediaKind.Movie, 2021), ("3", MediaKind.Series, 2024) }, result.Items.Select(i => (i.TmdbId, i.Kind, i.Year ?? 0)));
        Assert.Equal("沙丘：预言", result.Items[1].Title);
    }

    [Fact]
    public void QueryVariantFixesCommonChineseTypo()
    {
        Assert.Equal("权力的游戏", TmdbClient.QueryVariant("权利的游戏"));
        Assert.Null(TmdbClient.QueryVariant("权力的游戏"));
        Assert.Null(TmdbClient.QueryVariant("沙丘"));
    }
}

public class SeasonTests
{
    private static TmdbEpisodeBrief? Parse(string json, string today)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return TmdbClient.ParseSeasonLatest(doc.RootElement, 8, DateOnly.Parse(today));
    }

    private const string Season8 = """
    {"episodes":[{"episode_number":1,"name":"临冬城","air_date":"2019-04-14","episode_type":"standard"},
                 {"episode_number":6,"name":"铁王座","air_date":"2019-05-19","episode_type":"finale"},
                 {"episode_number":5,"name":"钟声","air_date":"2019-05-12","episode_type":"standard"}]}
    """;

    [Fact]
    public void FinishedSeasonShowsFinale()
    {
        var e = Parse(Season8, "2026-10-20")!;
        Assert.Equal((8, 6, "铁王座", "2019-05-19"), (e.Season, e.Episode, e.Name, e.AirDate));
        Assert.True(e.IsFinale);
        Assert.True(e.Aired);
    }

    [Fact]
    public void AiringSeasonShowsLatestAiredEpisode()
    {
        var e = Parse(Season8, "2019-05-13")!;
        Assert.Equal(5, e.Episode);
        Assert.False(e.IsFinale);
        Assert.Equal(2, e.AiredCount);
    }

    [Fact]
    public void UpcomingSeasonShowsFirstEpisode()
    {
        var e = Parse(Season8, "2019-01-01")!;
        Assert.Equal(1, e.Episode);
        Assert.False(e.Aired);
        Assert.Null(Parse("""{"episodes":[]}""", "2019-01-01"));
    }
}

public class FavoriteTests
{
    [Fact]
    public void AddDedupesAndRemoves()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "favorites.json");
        var favorites = new Favorites(new JsonStore<FavoriteList>(path));
        favorites.Add(new FavoriteItem { Kind = MediaKind.Movie, TmdbId = "1", Title = "A" });
        favorites.Add(new FavoriteItem { Kind = MediaKind.Series, TmdbId = "1", Title = "B" });
        favorites.Add(new FavoriteItem { Kind = MediaKind.Movie, TmdbId = "1", Title = "A2" });

        Assert.Equal(new[] { "A2", "B" }, favorites.List().Select(f => f.Title));
        Assert.True(favorites.Contains(MediaKind.Series, "1"));

        var reloaded = new Favorites(new JsonStore<FavoriteList>(path));
        Assert.True(reloaded.Remove(MediaKind.Movie, "1"));
        Assert.False(reloaded.Contains(MediaKind.Movie, "1"));
        Assert.Throws<InvalidOperationException>(() => reloaded.Add(new FavoriteItem { Kind = MediaKind.Movie, TmdbId = "", Title = "x" }));
    }
}
