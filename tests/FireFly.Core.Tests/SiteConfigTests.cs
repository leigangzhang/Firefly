using System.Text.Json;
using FireFly.Core.Metadata;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using Xunit;

namespace FireFly.Core.Tests;

/// <summary>配置页的站点和关键词：三层叠加、按类型选站点、按站点取关键词、顺序优先级。</summary>
public class SiteConfigTests
{
    private const string Base = """
        {"format":1,"version":5,
         "sources":[
           {"name":"a","kind":"jsonApi","endpoints":["https://a/api"],"response":{"itemsPath":"items"}},
           {"name":"b","kind":"jsonApi","endpoints":["https://b/api"],"response":{"itemsPath":"items"}}],
         "keywords":{"items":["甲","乙"]}}
        """;

    private sealed class FakeSource : IResourceSource
    {
        private readonly Func<string, IReadOnlyList<Release>> _answer;

        public FakeSource(string name, Func<string, IReadOnlyList<Release>> answer)
        {
            Name = name;
            _answer = answer;
        }

        public string Name { get; }

        public List<string> Asked { get; } = new();

        public Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null)
        {
            lock (Asked)
            {
                Asked.Add(keyword);
            }

            return Task.FromResult(_answer(keyword).Select(r => r with { Source = Name }).ToList() as IReadOnlyList<Release>);
        }
    }

    private static Release R(string hash, string name, int resolution = 1080) => new()
    {
        InfoHash = hash,
        Name = name,
        Traits = ReleaseParser.Parse(name) with { Resolution = resolution },
    };

    [Fact]
    public void KeywordsMergeByWordAndKeepBuiltinOnes()
    {
        var rules = RulesLoader.Merge(Base, """
            {"keywords":{"items":[{"word":"乙","types":["anime"]},{"word":"丙","sources":["b"]}],"disable":["甲"],"order":["丙","乙"]}}
            """);
        var words = rules.EffectiveKeywords();
        Assert.Equal(new[] { "丙", "乙" }, words.Select(k => k.Word));
        Assert.Equal(new[] { "anime" }, words.Single(k => k.Word == "乙").Types);
        Assert.Equal(new[] { "b" }, words.Single(k => k.Word == "丙").Sources);
    }

    [Fact]
    public void LegacyPublisherKeywordsReplaceAllItems()
    {
        var rules = RulesLoader.Merge(Base, """{"publisherKeywords":["丁"]}""");
        Assert.Equal(new[] { "丁" }, rules.EffectiveKeywords().Select(k => k.Word));
    }

    [Fact]
    public void SourceOrderReordersSources()
    {
        var rules = RulesLoader.Merge(Base, """{"sourceOrder":["b"],"sources":[{"name":"c","kind":"torznab","endpoints":["http://j"]}]}""");
        Assert.Equal(new[] { "b", "a", "c" }, rules.Sources.Select(s => s.Name));
    }

    [Fact]
    public void ConfigPageLayerGoesOnTopOfRulesFile()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "rules.json");
        File.WriteAllText(file, """{"sources":[{"name":"diao.im","kind":"jsonApi","endpoints":["https://from-file/api"],"response":{"itemsPath":"data"}}],"keywords":{"items":["高清剧集网"]}}""");
        var rules = RulesLoader.Load(file, """{"sources":[{"name":"diao.im","types":["movie"]}],"keywords":{"items":["新词"]}}""");
        var diao = rules.Sources.Single(s => s.Name == "diao.im");
        Assert.Equal(new[] { "https://from-file/api" }, diao.Endpoints);
        Assert.Equal(new[] { "movie" }, diao.Types);
        Assert.Contains(rules.EffectiveKeywords(), k => k.Word == "新词");
        Assert.Contains(rules.EffectiveKeywords(), k => k.Word == "高清剧集网");

        var broken = RulesLoader.Load(file, "{not json");
        Assert.Contains(broken.Warnings, w => w.Contains("配置页"));
        Assert.Equal(new[] { "https://from-file/api" }, broken.Sources.Single(s => s.Name == "diao.im").Endpoints);
    }

    [Fact]
    public void PlannerPicksSitesForTheTypeAndTheirKeywords()
    {
        var rules = RulesLoader.Merge(Base, """
            {"sources":[{"name":"a","types":["movie","tv"]},{"name":"b","types":["anime"]}],
             "keywords":{"items":[{"word":"甲","sources":["a"]},{"word":"字幕组","types":["anime"]}]}}
            """);
        var sources = new IResourceSource[] { new FakeSource("a", _ => Array.Empty<Release>()), new FakeSource("b", _ => Array.Empty<Release>()) };

        var anime = SearchPlanner.Plan(rules, sources, MediaCategories.Anime);
        Assert.Equal(new[] { "b" }, anime.Sources.Select(s => s.Source.Name));
        Assert.Equal(new[] { "乙", "字幕组" }, anime.Sources[0].Keywords);
        Assert.False(anime.NoSiteForCategory);

        var movie = SearchPlanner.Plan(rules, sources, MediaCategories.Movie);
        Assert.Equal(new[] { "甲", "乙" }, movie.Sources.Single().Keywords);

        // 没有站点负责综艺：改用全部站点并标出来
        var variety = SearchPlanner.Plan(rules, sources, MediaCategories.Variety);
        Assert.True(variety.NoSiteForCategory);
        Assert.Equal(2, variety.Sources.Count);
    }

    [Fact]
    public async Task AnimeSearchOnlyAsksAnimeSiteWithItsKeywords()
    {
        var rules = RulesLoader.Merge(Base, """
            {"sources":[{"name":"a","types":["movie"]},{"name":"b","types":["anime"]}],
             "keywords":{"items":[{"word":"甲","sources":["a"]},{"word":"乙","sources":["a"]},{"word":"字幕组","types":["anime"]}]}}
            """);
        var a = new FakeSource("a", _ => Array.Empty<Release>());
        var b = new FakeSource("b", k => k.Contains("字幕组") ? new[] { R("H1", "[字幕组] 某动画 S01 1080p") } : Array.Empty<Release>());
        var sources = new IResourceSource[] { a, b };
        var service = new SearchService(() => rules, c => SearchPlanner.Plan(rules, sources, c));
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "某动画", Season = 1, Category = MediaCategories.Anime };

        var result = await service.SearchAsync(query, false, SearchStage.Preferred, CancellationToken.None);

        Assert.Empty(a.Asked);
        Assert.All(b.Asked, k => Assert.Contains("字幕组", k));
        Assert.Equal(2, result.Releases.Single().KeywordRank);
        Assert.Equal(new SearchPlanSummary(MediaCategories.Anime, 1, 1, false), result.Plan);
    }

    [Fact]
    public void OrderFollowsKeywordThenSiteOrder()
    {
        var rules = RulesLoader.Merge(Base, """{"keywords":{"order":["乙","甲"]}}""");
        var sources = new IResourceSource[] { new FakeSource("a", _ => Array.Empty<Release>()), new FakeSource("b", _ => Array.Empty<Release>()) };
        var plan = SearchPlanner.Plan(rules, sources, MediaCategories.Movie);
        var ordered = SearchService.Order(new[]
        {
            R("1", "片 甲 2160p", 2160) with { Source = "a" },
            R("2", "片 乙 720p", 720) with { Source = "b" },
            R("3", "片 1080p") with { Source = "b" },
            R("4", "片 1080p") with { Source = "a" },
        }, plan);
        Assert.Equal(new[] { "2", "1", "4", "3" }, ordered.Select(r => r.InfoHash));
        Assert.Null(ordered[2].KeywordRank);
    }

    [Fact]
    public void TorznabFeedParsesInfohashAndMagnet()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel>
                <item>
                  <title>流浪地球2.2023.2160p.WEB-DL</title>
                  <size>19971597926</size>
                  <pubDate>Wed, 05 Apr 2023 08:12:00 +0000</pubDate>
                  <link>http://jackett/dl/1.torrent</link>
                  <torznab:attr name="infohash" value="0123456789abcdef0123456789abcdef01234567" />
                </item>
                <item>
                  <title>流浪地球.2019.1080p</title>
                  <link>magnet:?xt=urn:btih:AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQT&amp;dn=x</link>
                </item>
                <item><title>只有种子文件</title><link>http://jackett/dl/3.torrent</link></item>
              </channel>
            </rss>
            """;
        var list = TorznabSource.ParseFeed("j", xml, out var raw);
        Assert.Equal(3, raw);
        Assert.Equal(2, list.Count);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", list[0].InfoHash);
        Assert.Equal(19971597926, list[0].SizeBytes);
        Assert.Equal(2023, list[0].CreatedAt!.Value.Year);
        Assert.Equal("000102030405060708090A0B0C0D0E0F10111213", list[1].InfoHash);
        Assert.StartsWith("magnet:", list[1].Magnet);
    }

    [Fact]
    public void TorznabErrorAndUrls()
    {
        var ex = Assert.Throws<SourceException>(() => TorznabSource.ParseFeed("j", """<error code="100" description="Invalid API Key" />""", out _));
        Assert.Contains("Invalid API Key", ex.Message);
        Assert.Equal("http://h:9117/api/v2.0/indexers/all/results/torznab/api", TorznabSource.ApiUrl("http://h:9117/api/v2.0/indexers/all/results/torznab/"));
        Assert.Equal("http://h:9696/1/api", TorznabSource.ApiUrl("http://h:9696/1/api"));
        Assert.Equal("http://h/api?t=search&q=%E7%89%87&limit=100&offset=0&extended=1&apikey=k", TorznabSource.SearchUrl("http://h", "k", "片", 100, 0));
    }

    [Fact]
    public void JsonApiTakesHashFromMagnetField()
    {
        var rule = new SourceRule { Name = "x", Response = new JsonApiResponse { ItemsPath = "list", Fields = new JsonApiFields { InfoHash = "magnet", Name = "title" } } };
        using var doc = JsonDocument.Parse("""{"list":[{"title":"片","magnet":"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=a"}]}""");
        var r = JsonApiSource.ParsePage(rule, doc.RootElement, out _).Single();
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", r.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:0123", r.Magnet);
    }

    [Fact]
    public void AutoMapRecognisesDiaoStyleResponse()
    {
        const string json = """
            {"status":200,"message":"ok","data":{"has_more":true,"torrents":[
              {"hash":"0123456789abcdef0123456789abcdef01234567","name":"流浪地球2.2023.2160p","size":19971597926,"created_at":1680682320,"magnet_uri":"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567","files":[{"index":0,"path":"RARBG.cert","size":1500,"extension":"cert"},{"index":1,"path":"a.mkv","size":19971597926,"extension":"mkv"}]},
              {"hash":"1123456789abcdef0123456789abcdef01234567","name":"流浪地球.2019.1080p","size":9800000000,"created_at":1560988800,"magnet_uri":"magnet:?xt=urn:btih:1123456789abcdef0123456789abcdef01234567","files":[]}]}}
            """;
        using var doc = JsonDocument.Parse(json);
        var map = SourceDetector.AutoMap(doc.RootElement, "流浪地球")!;
        Assert.Equal("data.torrents", map.ItemsPath);
        Assert.Equal("hash", map.Fields.InfoHash);
        Assert.Equal("name", map.Fields.Name);
        Assert.Equal("magnet_uri", map.Fields.Magnet);
        Assert.Equal("size", map.Fields.Size);
        Assert.Equal("created_at", map.Fields.CreatedAt);
        Assert.Equal("unix", map.Fields.CreatedAtFormat);
        Assert.Equal("files", map.Fields.Files);
        Assert.Equal("path", map.Fields.FilePath);
        Assert.Equal("size", map.Fields.FileSize);
        Assert.Equal("status", map.StatusPath);
        Assert.Equal("200", map.StatusOk);
        Assert.Equal("message", map.MessagePath);
        Assert.Equal("data.has_more", map.HasMorePath);
    }

    [Fact]
    public void AutoMapHandlesOtherNamesAndIsoDates()
    {
        const string json = """
            {"code":0,"data":{"list":[
              {"title":"流浪地球2 4K","btih":"0123456789abcdef0123456789abcdef01234567","len":"19971597926","ts":"2023-04-05T08:12:00Z","seeders":12},
              {"title":"流浪地球 1080p","btih":"1123456789abcdef0123456789abcdef01234567","len":"9800000000","ts":"2019-06-20T00:00:00Z","seeders":3}]}}
            """;
        using var doc = JsonDocument.Parse(json);
        var map = SourceDetector.AutoMap(doc.RootElement, "流浪地球")!;
        Assert.Equal("data.list", map.ItemsPath);
        Assert.Equal("btih", map.Fields.InfoHash);
        Assert.Equal("title", map.Fields.Name);
        Assert.Equal("len", map.Fields.Size);
        Assert.Equal("ts", map.Fields.CreatedAt);
        Assert.Equal("iso", map.Fields.CreatedAtFormat);
        Assert.Equal("code", map.StatusPath);
        Assert.Equal("0", map.StatusOk);
    }

    [Fact]
    public void AutoMapPicksByteSizedFieldForFilesWhenNamesDontSay()
    {
        const string json = """
            {"items":[{"btih":"0123456789abcdef0123456789abcdef01234567","title":"流浪地球 1080p",
              "fl":[{"i":0,"p":"a.nfo","b":2048},{"i":1,"p":"a.mkv","b":5000000000}]}]}
            """;
        using var doc = JsonDocument.Parse(json);
        var map = SourceDetector.AutoMap(doc.RootElement, "流浪地球")!;
        Assert.Equal("fl", map.Fields.Files);
        Assert.Equal("p", map.Fields.FilePath);
        Assert.Equal("b", map.Fields.FileSize);
    }

    [Fact]
    public void AutoMapReturnsNullWithoutHashes()
    {
        using var doc = JsonDocument.Parse("""{"list":[{"title":"流浪地球","url":"https://x"}]}""");
        Assert.Null(SourceDetector.AutoMap(doc.RootElement, "流浪地球"));
    }

    [Fact]
    public void DetectorFindsKeywordParamAndGuessesApiPaths()
    {
        var page = new Uri("https://m.diao.im/search?keyword=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83&page=1");
        var query = SourceDetector.ParseQuery(page.Query);
        Assert.Equal("keyword", SourceDetector.FindKeywordParam(query, null));
        var guesses = SourceDetector.GuessUrls(page, "keyword", "流浪地球").Select(u => u.AbsolutePath).ToList();
        Assert.Equal("/api/search", guesses[0]);
        Assert.True(SourceDetector.SameSite("https://m.diao.im/api/search", new Uri("https://www.diao.im/x")));

        var paths = SourceDetector.ExtractApiPaths("""fetch("/api/user/info");axios.get('/api/torrents/search',{});var u="https://x.example/api/search/";""").ToList();
        Assert.Equal("/api/torrents/search", paths[0]);
        Assert.Contains("https://x.example/api/search", paths);
        Assert.Contains("/api/user/info", paths);
    }

    [Theory]
    [InlineData(MediaKind.Movie, new[] { 28 }, "movie")]
    [InlineData(MediaKind.Movie, new[] { 16, 10751 }, "anime")]
    [InlineData(MediaKind.Series, new[] { 16 }, "anime")]
    [InlineData(MediaKind.Series, new[] { 99 }, "documentary")]
    [InlineData(MediaKind.Series, new[] { 10764 }, "variety")]
    [InlineData(MediaKind.Movie, new[] { 10764 }, "movie")]
    [InlineData(MediaKind.Series, new[] { 18 }, "tv")]
    public void CategoryFromTmdbGenres(MediaKind kind, int[] genres, string expected) =>
        Assert.Equal(expected, MediaCategories.Of(kind, genres));

    [Fact]
    public void TmdbNamesCarryGenres()
    {
        using var doc = JsonDocument.Parse("""{"original_name":"x","genres":[{"id":16,"name":"动画"},{"id":10759}]}""");
        Assert.Equal(new[] { 16, 10759 }, TmdbClient.ParseNames(MediaKind.Series, doc.RootElement).Genres);
    }

    [Fact]
    public void OldKeywordTextBoxMigratesToAddDisableOrder()
    {
        var overlay = KeywordMigration.Overlay("""{"sources":[{"name":"a","enabled":false}]}""", new[] { "新词", "高清剧集网" }, Array.Empty<string>());
        var rules = RulesLoader.Load(null, overlay);
        Assert.Equal(new[] { "新词", "高清剧集网" }, rules.EffectiveKeywords().Select(k => k.Word));
        Assert.Contains("\"enabled\":false", overlay);
    }

    [Fact]
    public void ReleaseBuildShipsNoSitesOrKeywords()
    {
        var rules = RulesLoader.LoadDefault();
        Assert.Empty(rules.Sources);
        Assert.Empty(rules.EffectiveKeywords());
        Assert.Empty(rules.Warnings);
    }

    [Fact]
    public void BracketedGroupNameDoesNotGlueToTitle()
    {
        var query = new MediaQuery { Kind = MediaKind.Series, Title = "某动画" };
        Assert.True(SearchService.NameMentions("[某字幕组] 某动画 S01 1080p", query));
        Assert.True(SearchService.NameMentions("【某字幕组】某动画 第一季", query));
        Assert.False(SearchService.NameMentions("另某动画 S01", query));
    }
}
