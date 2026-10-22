using System.Net;
using System.Text;
using System.Text.Json;
using FireFly.Core.Rules;
using FireFly.Core.Sources;
using Xunit;

namespace FireFly.Core.Tests;

/// <summary>网页站点：网页解析、按选择器取结果、详情页找磁力、从搜索结果页自动识别。</summary>
public class HtmlSiteTests
{
    private const string H1 = "0123456789abcdef0123456789abcdef01234567";
    private const string H2 = "89abcdef0123456789abcdef0123456789abcdef";
    private const string H3 = "fedcba9876543210fedcba9876543210fedcba98";

    /// <summary>磁力只在详情页的站：列表是 ul.list > li，侧栏有一组"热门搜索"干扰。</summary>
    private const string ListPage = """
        <!DOCTYPE html>
        <html><head><title>流浪地球 - 搜索</title><script>var x = "<li>流浪地球</li>";</script></head>
        <body>
          <div class="side"><h3>热门</h3>
            <p class="hot"><a href="/s/流浪地球">流浪地球</a></p>
            <p class="hot"><a href="/s/流浪地球2">流浪地球2</a></p>
            <p class="hot"><a href="/s/流浪地球3">流浪地球3</a></p>
          </div>
          <ul class="list">
            <li class="item"><div class="t"><a href="/d/1">流浪地球.2019.2160p.WEB-DL</a></div><span class="meta">大小：18.6 GB 时间：2023-04-05</span></li>
            <li class="item"><div class="t"><a href="/d/2">流浪地球2.2023.1080p.BluRay</a></div><span class="meta">大小：980 MB 时间：2023/4/6</span></li>
            <li class="item"><div class="t"><a href="/d/3">The.Wandering.Earth 流浪地球 4K</a></div><span class="meta">大小：1.2 TiB 时间：2023年4月7日</span></li>
          </ul>
          <div class="pager"><a href="?keyword=流浪地球&p=2">下一页</a></div>
        </body></html>
        """;

    private static string Detail(string hash) => $"""
        <html><body><input type="hidden" name="token" value="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
        <h1>详情</h1><p>特征码：{hash}</p><a class="dl" href="magnet:?xt=urn:btih:{hash}&dn=x">磁力链接</a></body></html>
        """;

    /// <summary>磁力就在列表里的站（表格）。</summary>
    private const string TablePage = $"""
        <html><body><table id="res">
          <tr><th>名称</th><th>大小</th><th></th></tr>
          <tr><td class="n"><a href="/t/1">流浪地球 2019 1080p</a></td><td>2.5 GB</td><td><a href="magnet:?xt=urn:btih:{H1}">磁力</a></td></tr>
          <tr><td class="n"><a href="/t/2">流浪地球 2019 4K HDR</a></td><td>20 GB</td><td><a href="magnet:?xt=urn:btih:{H2}">磁力</a></td></tr>
        </table></body></html>
        """;

    private sealed class Pages : HttpMessageHandler
    {
        private readonly Func<Uri, (HttpStatusCode, string)?> _answer;

        public Pages(Func<Uri, (HttpStatusCode, string)?> answer) => _answer = answer;

        public List<Uri> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(request.RequestUri!);
            }

            var (status, body) = _answer(request.RequestUri!) ?? (HttpStatusCode.NotFound, "not found");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
        }
    }

    private static (HttpStatusCode, string)? DetailSite(Uri u) =>
        u.AbsolutePath switch
        {
            "/search" => (HttpStatusCode.OK, ListPage),
            "/d/1" => (HttpStatusCode.OK, Detail(H1)),
            "/d/2" => (HttpStatusCode.OK, Detail(H2)),
            "/d/3" => (HttpStatusCode.OK, Detail(H3)),
            _ => null,
        };

    [Fact]
    public void MiniHtmlParsesSloppyMarkupAndSelectors()
    {
        var doc = MiniHtml.Parse("""
            <div id="m" class="a b"><p>one<p>two <b>bold</b></p><ul><li>x<li class="y">y</ul>
            <!-- <li>comment</li> --><img src="i.png"><a href="/1" data-k="v1">链接</a></div>
            <script>if (a < b) document.write("<li>no</li>")</script>
            """);
        Assert.Equal(2, MiniHtml.QueryAll(doc, "div#m p").Count);
        Assert.Equal("two bold", MiniHtml.QueryAll(doc, "p")[1].Text);
        Assert.Equal("【高清剧集网发布】超能路人甲[全8集]", MiniHtml.Parse("<a>【高清剧集网<span>发布</span>】<em>超能路人甲</em>[全8集]</a>").Elements.First().Text);
        Assert.Equal("bold", MiniHtml.Value(doc, "div.a.b > p b"));
        Assert.Equal(2, MiniHtml.QueryAll(doc, "ul > li").Count);
        Assert.Equal("y", MiniHtml.Value(doc, "li.y"));
        Assert.Equal("/1", MiniHtml.Value(doc, "a[data-k=v1]@href"));
        Assert.Equal("/1", MiniHtml.Value(doc, "a[href^=/]@href"));
        Assert.Null(MiniHtml.Query(doc, "li.no"));
        Assert.Equal(2, MiniHtml.QueryAll(doc, "li").Count);
    }

    [Fact]
    public void ParsesSizesAndDates()
    {
        Assert.Equal((long)(18.6 * 1024 * 1024 * 1024), HtmlSource.ParseSize("大小：18.6 GB"));
        Assert.Equal(980L * 1024 * 1024, HtmlSource.ParseSize("980MB"));
        Assert.Equal(0, HtmlSource.ParseSize("1080p"));
        Assert.Equal(new DateTimeOffset(2023, 4, 7, 0, 0, 0, TimeSpan.Zero), HtmlSource.ParseDate("时间：2023年4月7日"));
        Assert.Equal(new DateTimeOffset(2023, 4, 6, 0, 0, 0, TimeSpan.Zero), HtmlSource.ParseDate("2023/4/6"));
        Assert.Null(HtmlSource.ParseDate("2023-13-40"));
    }

    [Fact]
    public void FindMagnetPrefersLinksThenVisibleHash()
    {
        Assert.StartsWith("magnet:?xt=urn:btih:" + H1, HtmlSource.FindMagnet(MiniHtml.Parse(Detail(H1))));
        var textOnly = MiniHtml.Parse($"""<input value="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"><p>特征码：{H2}</p>""");
        Assert.Equal(H2, HtmlSource.FindMagnet(textOnly));
        Assert.Null(HtmlSource.FindMagnet(MiniHtml.Parse("<p>没有</p>")));
    }

    [Fact]
    public void GuessesListStructureAndIgnoresSidebar()
    {
        var page = new Uri("https://x.test/search?keyword=流浪地球&p=1");
        var guess = SourceDetector.GuessHtml(MiniHtml.Parse(ListPage), "流浪地球", page);
        Assert.NotNull(guess);
        Assert.Equal(3, guess!.Count);
        Assert.False(guess.MagnetInList);
        Assert.True(guess.Rule.Detail);
        Assert.Equal(new Uri("https://x.test/d/1"), guess.FirstLink);

        var rule = SourceDetector.BuildHtmlRule(page, "https://x.test/search?keyword={keyword}", 1, guess.Rule);
        var items = HtmlSource.Extract(rule, MiniHtml.Parse(ListPage), page);
        Assert.Equal(3, items.Count);
        Assert.Equal("流浪地球2.2023.1080p.BluRay", items[1].Title);
        Assert.Equal(new Uri("https://x.test/d/2"), items[1].Link);
        Assert.Equal(980L * 1024 * 1024, items[1].Size);
        Assert.All(items, i => Assert.Null(i.Release));
    }

    [Fact]
    public void GuessesTableWithMagnetsInList()
    {
        var page = new Uri("https://y.test/s?q=流浪地球");
        var guess = SourceDetector.GuessHtml(MiniHtml.Parse(TablePage), "流浪地球", page);
        Assert.NotNull(guess);
        Assert.True(guess!.MagnetInList);
        Assert.False(guess.Rule.Detail);
        var rule = SourceDetector.BuildHtmlRule(page, "https://y.test/s?q={keyword}", 1, guess.Rule);
        var releases = HtmlSource.Extract(rule, MiniHtml.Parse(TablePage), page).Select(i => i.Release).ToList();
        Assert.Equal(new[] { H1, H2 }, releases.Select(r => r!.InfoHash.ToLowerInvariant()));
        Assert.Equal("流浪地球 2019 4K HDR", releases[1]!.Name);
        Assert.Equal(20L * 1024 * 1024 * 1024, releases[1]!.SizeBytes);
    }

    [Fact]
    public void BuildsUrlTemplatesFromQueryOrPath()
    {
        var q = new Uri("https://x.test/search?keyword=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83&sos=relevance&p=1");
        Assert.Equal(("https://x.test/search?keyword={keyword}&sos=relevance&p={page}", 1), SourceDetector.HtmlTemplate(q, "keyword", "流浪地球"));

        var path = new Uri("https://z.test/search/%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83/1");
        Assert.Equal("流浪地球", SourceDetector.PathKeyword(path, null));
        Assert.Equal(("https://z.test/search/{keyword}/{page}", 1), SourceDetector.HtmlTemplate(path, null, "流浪地球"));
        Assert.Equal("https://z.test/search/%E5%96%84%E6%84%8F/2", HtmlSource.SearchUrl("https://z.test/search/{keyword}/{page}", "善意", 2));
    }

    [Fact]
    public async Task DetectsHtmlSiteWithMagnetsOnDetailPages()
    {
        var http = new HttpClient(new Pages(DetailSite));
        var result = await new SourceDetector(http).DetectAsync("https://x.test/search?keyword=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83&p=1", null, new RulesDocument(), CancellationToken.None);
        Assert.NotNull(result.Rule);
        Assert.Equal(SourceFactory.Html, result.Rule!.Kind);
        Assert.Equal("https://x.test/search?keyword={keyword}&p={page}", result.Rule.Endpoints[0]);
        Assert.True(result.Rule.Html!.Detail);
        Assert.Equal(3, result.Preview.Count);
        Assert.Contains(result.Steps, s => s.Label.StartsWith("5.", StringComparison.Ordinal) && s.State == "ok");

        // 规则能存成 JSON 再读回来
        var json = JsonSerializer.Serialize(result.Rule, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<SourceRule>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(result.Rule.Html.Item, back.Html!.Item);
    }

    [Fact]
    public async Task HtmlSourceSearchesResolvesDetailsAndDedupes()
    {
        var pages = new Pages(DetailSite);
        var rule = new SourceRule
        {
            Name = "x",
            Kind = SourceFactory.Html,
            Endpoints = new List<string> { "https://x.test/search?keyword={keyword}" },
            Html = new HtmlRule { Item = "ul.list > li", Title = ".t a", Link = ".t a@href", Detail = true, DetailLimit = 2 },
        };
        Assert.True(SourceFactory.TryCreate(rule, new HttpClient(pages), out var source, out _));
        var releases = await source!.SearchAsync("流浪地球", CancellationToken.None);
        Assert.Equal(2, releases.Count);
        Assert.Equal(H1, releases[0].InfoHash.ToLowerInvariant());
        Assert.Equal((long)(18.6 * 1024 * 1024 * 1024), releases[0].SizeBytes);
        Assert.DoesNotContain(pages.Asked, u => u.AbsolutePath == "/d/3");
    }

    /// <summary>标题不在链接里、搜索词被 &lt;b&gt; 切开、链接是个空 &lt;a&gt;（类似 cilisousuo）。</summary>
    private const string SplitTitlePage = """
        <html><body><ul class="list">
          <li class="item"><a class="link" href="/magnet/a1"></a><div class="result-title"><b>流浪</b><b>地球</b>.2019.1080p</div><div class="size">2.1 GB</div></li>
          <li class="item"><a class="link" href="/magnet/a2"></a><div class="result-title">The.Wandering.Earth.<b>流浪地球</b>.4K</div><div class="size">18 GB</div></li>
          <li class="item"><a class="link" href="/magnet/a3"></a><div class="result-title"><b>流浪地球</b>2 国语中字</div><div class="size">3 GB</div></li>
          <li class="item"><a class="link" href="/magnet/a4"></a><div class="result-title">Earth.Movie.<b>流浪地球</b>.2019.WEB</div><div class="size">1 GB</div></li>
          <li class="item"><a class="link" href="/magnet/a5"></a><div class="result-title">合集 4K</div><div class="size">900 GB</div></li>
        </ul></body></html>
        """;

    [Fact]
    public void GuessesTitlesOutsideLinksAndSplitByHighlight()
    {
        var page = new Uri("https://c.test/search?q=流浪地球");
        var guess = SourceDetector.GuessHtml(MiniHtml.Parse(SplitTitlePage), "流浪地球", page);
        Assert.NotNull(guess);
        Assert.Equal(5, guess!.Count);
        Assert.True(guess.Rule.Detail);
        Assert.Equal(new Uri("https://c.test/magnet/a1"), guess.FirstLink);
        var rule = SourceDetector.BuildHtmlRule(page, "https://c.test/search?q={keyword}", 1, guess.Rule);
        var items = HtmlSource.Extract(rule, MiniHtml.Parse(SplitTitlePage), page);
        Assert.Equal("The.Wandering.Earth.流浪地球.4K", items[1].Title);
        Assert.Equal("Earth.Movie.流浪地球.2019.WEB", items[3].Title);
        Assert.Equal("流浪地球2 国语中字", items[2].Title);
        Assert.Equal(18L * 1024 * 1024 * 1024, items[1].Size);
    }

    [Fact]
    public async Task ListedMagnetsSkipApiGuessing()
    {
        var pages = new Pages(u => u.AbsolutePath == "/s" ? (HttpStatusCode.OK, TablePage) : null);
        var result = await new SourceDetector(new HttpClient(pages)).DetectAsync("https://y.test/s?q=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83", null, new RulesDocument(), CancellationToken.None);
        Assert.Equal(SourceFactory.Html, result.Rule!.Kind);
        Assert.Equal(2, result.Preview.Count);
        Assert.Single(pages.Asked);
    }

    [Fact]
    public void Base32MagnetsInListAreNormalised()
    {
        const string page = """
            <table id="topic_list"><tbody>
            <tr><td>2023/05/01 12:00</td><td class="title"><a href="/t/1">[DBD-Raws][流浪地球][1080P]</a></td><td><a class="arrow-magnet" href="magnet:?xt=urn:btih:GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&dn=x">磁</a></td><td>8.5GB</td></tr>
            <tr><td>2023/05/02 12:00</td><td class="title"><a href="/t/2">[DBD-Raws][流浪地球 2][4K]</a></td><td><a class="arrow-magnet" href="magnet:?xt=urn:btih:MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43U&dn=y">磁</a></td><td>40GB</td></tr>
            </tbody></table>
            """;
        var url = new Uri("https://d.test/topics/list?keyword=流浪地球");
        var guess = SourceDetector.GuessHtml(MiniHtml.Parse(page), "流浪地球", url)!;
        Assert.True(guess.MagnetInList);
        var rule = SourceDetector.BuildHtmlRule(url, "https://d.test/topics/list?keyword={keyword}", 1, guess.Rule);
        var r = HtmlSource.Extract(rule, MiniHtml.Parse(page), url).Select(i => i.Release!).ToList();
        Assert.Equal(40, r[0].InfoHash.Length);
        Assert.Equal(new DateTimeOffset(2023, 5, 2, 0, 0, 0, TimeSpan.Zero), r[1].CreatedAt);
        Assert.Equal(40L * 1024 * 1024 * 1024, r[1].SizeBytes);
    }

    [Fact]
    public void FindsSearchFormOnHomePage()
    {
        var home = new Uri("https://x.test/");
        var doc = MiniHtml.Parse("""
            <form action="/login" method="post"><input name="user"><input type="password" name="pw"></form>
            <form action="/topics/list" method="get"><input type="text" name="keyword"><input type="hidden" name="sort" value="new"><button>搜索</button></form>
            """);
        Assert.Equal("https://x.test/topics/list?keyword=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83&sort=new", SourceDetector.SearchFormUrl(doc, home, "流浪地球")!.AbsoluteUri);
        Assert.Null(SourceDetector.SearchFormUrl(MiniHtml.Parse("""<form action="https://other.test/s"><input name="q"></form>"""), home, "a"));
    }

    [Fact]
    public void ReadsOpenSearchTemplate()
    {
        const string xml = """
            <OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/">
              <Url type="application/x-suggestions+json" template="https://m.diao.im/suggest?q={searchTerms}"/>
              <Url type="text/html" template="https://m.diao.im/search?keyword={searchTerms}&amp;p={startPage?}"/>
            </OpenSearchDescription>
            """;
        Assert.Equal("https://m.diao.im/search?keyword=%E6%B5%81%E6%B5%AA%E5%9C%B0%E7%90%83&p=", SourceDetector.OpenSearchTemplate(xml, new Uri("https://m.diao.im/"), "流浪地球")!.AbsoluteUri);
        Assert.Null(SourceDetector.OpenSearchTemplate("not xml", new Uri("https://m.diao.im/"), "a"));
        const string plain = """<OpenSearchDescription><Url type="text/html" template="http://share.x.test/topics/list?keyword={searchTerms}"/></OpenSearchDescription>""";
        Assert.Equal("https://share.x.test/topics/list?keyword=a", SourceDetector.OpenSearchTemplate(plain, new Uri("https://share.x.test/"), "a")!.AbsoluteUri);
    }

    [Fact]
    public async Task DetectsFromBareSiteAddress()
    {
        var pages = new Pages(u => u.AbsolutePath switch
        {
            "/" => (HttpStatusCode.OK, """<html><body><form action="/search"><input type="search" name="keyword"></form></body></html>"""),
            "/search" when u.Query.Contains("keyword=", StringComparison.Ordinal) => (HttpStatusCode.OK, ListPage),
            "/d/1" => (HttpStatusCode.OK, Detail(H1)),
            "/d/2" => (HttpStatusCode.OK, Detail(H2)),
            "/d/3" => (HttpStatusCode.OK, Detail(H3)),
            _ => null,
        });
        var result = await new SourceDetector(new HttpClient(pages)).DetectAsync("https://x.test", null, new RulesDocument(), CancellationToken.None);
        Assert.Contains(result.Steps, s => s.Label == "找搜索入口" && s.State == "ok");
        Assert.Equal(SourceFactory.Html, result.Rule!.Kind);
        Assert.Equal("https://x.test/search?keyword={keyword}", result.Rule.Endpoints[0]);
        Assert.Equal(3, result.Preview.Count);
    }

    [Fact]
    public async Task BareSiteWithoutSearchAsksForAResultPage()
    {
        var pages = new Pages(u => u.AbsolutePath == "/" ? (HttpStatusCode.OK, "<html><body>欢迎</body></html>") : null);
        var result = await new SourceDetector(new HttpClient(pages)).DetectAsync("https://y.test/", null, new RulesDocument(), CancellationToken.None);
        Assert.Null(result.Rule);
        Assert.Contains(result.Steps, s => s.Label == "找搜索入口" && s.State == "fail" && s.Detail.Contains("粘贴进来", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HtmlSourceReportsChallenge()
    {
        var http = new HttpClient(new CfHandler());
        var rule = new SourceRule { Name = "cf", Kind = SourceFactory.Html, Endpoints = new List<string> { "https://cf.test/s?q={keyword}" }, Html = new HtmlRule { Item = "li" } };
        Assert.True(SourceFactory.TryCreate(rule, http, out var source, out _));
        var ex = await Assert.ThrowsAsync<SourceException>(() => source!.SearchAsync("a", CancellationToken.None));
        Assert.Contains("人机验证", ex.Message);
    }

    private sealed class CfHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<title>Just a moment...</title>") };
            r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return Task.FromResult(r);
        }
    }
}
