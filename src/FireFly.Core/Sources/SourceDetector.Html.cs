using System.Text;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

/// <summary>网页站点的识别：没有数据接口时，直接从搜索结果网页里认出结果条目、标题、详情链接和磁力。</summary>
public sealed partial class SourceDetector
{
    private static readonly string[] PageParams = { "p", "page", "pn", "pg", "pageno", "pagenum", "page_no", "pageindex", "currentpage", "paged" };

    /// <summary>网页里认出的结果：规则、示例结果条目数、第一条的详情链接（磁力要去详情页找时用）。</summary>
    internal sealed record HtmlGuess(HtmlRule Rule, int Count, bool MagnetInList, Uri? FirstLink);

    /// <summary>
    /// 在搜索结果页里找结果条目：先找文字里有搜索词的链接，再往上找"同一个父元素下、结构相同、至少 2 个也含搜索词"的兄弟块。
    /// 有多组时优先选块里有体积、hash 或磁力的那组（排除侧栏"热门搜索"之类），再选条数多的、离标题近的。
    /// </summary>
    internal static HtmlGuess? GuessHtml(HtmlNode doc, string keyword, Uri pageUrl)
    {
        var key = Norm(keyword);
        if (key.Length == 0)
        {
            return null;
        }

        var body = doc.Descendants().FirstOrDefault(n => n.Tag == "body") ?? doc;
        // 含搜索词的链接，加上"最里层"含搜索词的元素：标题不在链接里、或搜索词被 <b> 高亮切开的站也能认出来
        var hits = body.Descendants()
            .Where(n => !n.IsText && n.Tag is not ("script" or "style" or "option" or "textarea" or "title" or "select") && Norm(n.Text).Contains(key, StringComparison.Ordinal))
            .ToList();
        var hitSet = new HashSet<HtmlNode>(hits);
        var candidates = hits.Where(n => n.Tag == "a").Concat(hits.Where(n => n.Tag != "a" && !n.Elements.Any(hitSet.Contains)).Select(LiftInline)).Distinct().ToList();

        (HtmlNode Item, HtmlNode Title, List<HtmlNode> Group, int Rich, int Depth)? best = null;
        foreach (var c in candidates)
        {
            var depth = 0;
            for (var a = c; a.Parent is not null && a.Tag is not ("body" or "#document") && depth < 10; a = a.Parent, depth++)
            {
                var sig = Signature(a);
                // 至少 2 条含搜索词才算一组结果；整组取同结构的全部兄弟（有的站按种子里的文件名命中，标题里没有搜索词）
                var group = a.Parent.Elements.Where(s => Signature(s) == sig).ToList();
                if (group.Count(s => Norm(s.Text).Contains(key, StringComparison.Ordinal)) < 2)
                {
                    continue;
                }

                var rich = group.Count(s => HtmlSource.ParseSize(s.Text) > 0 || HtmlSource.FindMagnet(s) is not null);
                if (best is null || Better((rich, group.Count, depth), (best.Value.Rich, best.Value.Group.Count, best.Value.Depth)))
                {
                    best = (a, c, group, rich, depth);
                }

                break;
            }
        }

        if (best is not { } b)
        {
            return null;
        }

        var itemSelector = ItemSelector(doc, b.Item, b.Group);
        if (itemSelector is null)
        {
            return null;
        }

        var titleNode = b.Title;
        var title = titleNode == b.Item ? null : RelativeSelector(b.Item, titleNode);
        var linkNode = titleNode.Tag == "a" ? titleNode : titleNode.Ancestors().TakeWhile(x => x != b.Item.Parent).FirstOrDefault(x => x.Tag == "a") ?? MiniHtml.Query(b.Item, "a[href]");
        string? link = null;
        if (linkNode?.Attr("href") is { Length: > 0 } href && !href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            link = linkNode == b.Item ? "@href" : (RelativeSelector(b.Item, linkNode) ?? "a") + "@href";
        }

        var magnetInList = b.Group.Any(g => HtmlSource.FindMagnet(g) is not null);
        Uri? firstLink = null;
        if (link is not null && MiniHtml.Value(b.Group[0], link) is { } h && Uri.TryCreate(pageUrl, h, out var abs))
        {
            firstLink = abs;
        }

        var rule = new HtmlRule { Item = itemSelector, Title = title, Link = link, Detail = !magnetInList };
        return new HtmlGuess(rule, MiniHtml.QueryAll(doc, itemSelector).Count, magnetInList, firstLink);
    }

    private static readonly HashSet<string> InlineTags = new(StringComparer.Ordinal) { "b", "strong", "em", "i", "mark", "font", "span", "u", "small", "big" };

    /// <summary>高亮搜索词的 &lt;b&gt;、&lt;em&gt; 之类不是标题本身，往上取到它所在的块。</summary>
    private static HtmlNode LiftInline(HtmlNode n)
    {
        while (InlineTags.Contains(n.Tag) && n.Parent is { } p && p.Tag is not ("body" or "#document" or "a"))
        {
            n = p;
        }

        return n;
    }

    private static bool Better((int Rich, int Count, int Depth) a, (int Rich, int Count, int Depth) b) =>
        (a.Rich > 0) != (b.Rich > 0) ? a.Rich > 0 : a.Count != b.Count ? a.Count > b.Count : a.Depth < b.Depth;

    /// <summary>去掉空白和标点、转小写，用来判断"文字里有没有搜索词"。</summary>
    internal static string Norm(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static string Signature(HtmlNode n) => n.Tag + "|" + string.Join(".", n.Classes.Where(IsPlainClass).OrderBy(c => c, StringComparer.Ordinal));

    private static bool IsPlainClass(string c) => c.Length > 0 && !char.IsDigit(c[0]) && c.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    /// <summary>tag.class1.class2；没有 class 时只有 tag。</summary>
    private static string SimpleSelector(HtmlNode n)
    {
        var classes = n.Classes.Where(IsPlainClass).ToList();
        return n.Tag + string.Concat(classes.Select(c => "." + c));
    }

    private static string? AnchorSelector(HtmlNode n)
    {
        if (n.Attr("id") is { Length: > 0 } id && id.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_') && !char.IsDigit(id[0]))
        {
            return n.Tag + "#" + id;
        }

        return n.Classes.Any(IsPlainClass) ? SimpleSelector(n) : null;
    }

    /// <summary>选中且只选中这一组结果的选择器：先试 tag.class，再逐级加上带 id 或 class 的祖先。</summary>
    private static string? ItemSelector(HtmlNode doc, HtmlNode item, List<HtmlNode> group)
    {
        var self = SimpleSelector(item);
        var tries = new List<string>();
        if (item.Classes.Any(IsPlainClass))
        {
            tries.Add(self);
        }

        var parent = item.Parent;
        var chain = self;
        var direct = true;
        for (var p = parent; p is not null && p.Tag is not ("#document" or "html"); p = p.Parent)
        {
            if (AnchorSelector(p) is { } anchor)
            {
                tries.Add(anchor + (direct ? " > " : " ") + chain);
            }

            if (p == parent)
            {
                direct = false;
            }

            if (tries.Count > 6)
            {
                break;
            }
        }

        tries.Add("body " + self);
        foreach (var t in tries)
        {
            var found = MiniHtml.QueryAll(doc, t);
            if (group.All(found.Contains) && found.Count <= group.Count + 2)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>条目里指向 target 的选择器：先试 target 自己的 tag.class，再带上中间的祖先。</summary>
    private static string? RelativeSelector(HtmlNode item, HtmlNode target)
    {
        var path = new List<HtmlNode>();
        for (var n = target; n is not null && n != item; n = n.Parent)
        {
            path.Insert(0, n);
        }

        if (path.Count == 0 || path[0].Parent != item && !path[0].Ancestors().Contains(item))
        {
            return null;
        }

        var last = SimpleSelector(target);
        if (MiniHtml.Query(item, last) == target)
        {
            return last;
        }

        if (path.Count >= 2)
        {
            var two = SimpleSelector(path[^2]) + " " + last;
            if (MiniHtml.Query(item, two) == target)
            {
                return two;
            }
        }

        var full = string.Join(" > ", path.Select(SimpleSelector));
        return MiniHtml.Query(item, full) == target ? full : null;
    }

    /// <summary>
    /// 把样例地址变成地址模板：搜索词换成 {keyword}，页码参数换成 {page}；其余参数（排序、筛选）原样保留。
    /// 搜索词在路径里（如 /search/流浪地球/1）也行，后面紧跟的数字段当页码。
    /// </summary>
    internal static (string Template, int FirstPage) HtmlTemplate(Uri page, string? keywordParam, string sample)
    {
        var origin = page.GetLeftPart(UriPartial.Authority);
        var firstPage = 1;
        var segments = page.AbsolutePath.Split('/');
        var path = page.AbsolutePath;
        if (keywordParam is null)
        {
            var at = Array.FindIndex(segments, s => string.Equals(Uri.UnescapeDataString(s), sample, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                segments[at] = "{keyword}";
                if (at + 1 < segments.Length && int.TryParse(segments[at + 1], out var n))
                {
                    segments[at + 1] = "{page}";
                    firstPage = n <= 1 ? n : 1;
                }

                path = string.Join('/', segments);
            }
        }

        var parts = new List<string>();
        foreach (var raw in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? raw : raw[..eq]);
            var value = eq < 0 ? string.Empty : raw[(eq + 1)..];
            if (keywordParam is not null && name == keywordParam)
            {
                parts.Add(raw[..eq] + "={keyword}");
            }
            else if (PageParams.Contains(name, StringComparer.OrdinalIgnoreCase) && int.TryParse(value, out var n))
            {
                parts.Add((eq < 0 ? raw : raw[..eq]) + "={page}");
                firstPage = n <= 1 ? n : 1;
            }
            else
            {
                parts.Add(raw);
            }
        }

        return (origin + path + (parts.Count > 0 ? "?" + string.Join('&', parts) : string.Empty), firstPage);
    }

    /// <summary>路径里的搜索词：等于样例片名的段，没有时取含中文的段。</summary>
    internal static string? PathKeyword(Uri page, string? sample)
    {
        sample = string.IsNullOrWhiteSpace(sample) ? DefaultSample : sample.Trim();
        var segments = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        return segments.FirstOrDefault(s => string.Equals(s, sample, StringComparison.OrdinalIgnoreCase))
            ?? segments.FirstOrDefault(s => s.Any(c => c > '\u2e7f'));
    }

    /// <summary>网页识别成功后的规则。</summary>
    internal static SourceRule BuildHtmlRule(Uri page, string template, int firstPage, HtmlRule html)
    {
        html.FirstPage = firstPage;
        return new SourceRule
        {
            Name = page.Host,
            Kind = SourceFactory.Html,
            Endpoints = new List<string> { template },
            MaxPages = 1,
            Html = html,
        };
    }

    /// <summary>识别第 4、5 步：网页里直接有结果吗、磁力在哪；成功时返回规则和预览。</summary>
    private async Task<DetectResult?> DetectHtmlAsync(List<DetectStep> steps, HtmlNode doc, Uri page, string? keywordParam, string keyword, string sample, CancellationToken ct)
    {
        var guess = GuessHtml(doc, keyword, page);
        if (guess is null)
        {
            steps.Add(new DetectStep("4. 网页里直接有结果", "fail", $"网页里没认出一组包含“{keyword}”的结果"));
            return null;
        }

        steps.Add(new DetectStep("4. 网页里直接有结果", "ok", $"找到 {guess.Count} 条结构相同的结果，标题里有“{keyword}”"));
        var (template, firstPage) = HtmlTemplate(page, keywordParam, sample);
        var rule = BuildHtmlRule(page, template, firstPage, guess.Rule);
        var items = HtmlSource.Extract(rule, doc, page);
        List<Release> preview;
        if (guess.MagnetInList)
        {
            steps.Add(new DetectStep("5. 磁力在哪", "ok", "就在结果列表里"));
            preview = items.Where(i => i.Release is not null).Select(i => i.Release!).Take(5).ToList();
        }
        else
        {
            if (guess.FirstLink is null)
            {
                steps.Add(new DetectStep("5. 磁力在哪", "fail", "结果列表里没有磁力，也没有详情页链接"));
                return null;
            }

            preview = new List<Release>();
            foreach (var item in items.Where(i => i.Link is not null).Take(5))
            {
                if (_requests >= MaxRequests)
                {
                    break;
                }

                _requests++;
                string? magnet = null;
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));
                    magnet = HtmlSource.FindMagnet(MiniHtml.Parse(await _http.GetStringAsync(item.Link, cts.Token).ConfigureAwait(false)));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                }

                if (magnet is not null && HtmlSource.ToRelease(rule.Name, magnet, item.Title, item.Size, item.Date) is { } release)
                {
                    preview.Add(release);
                }
            }

            if (preview.Count == 0)
            {
                steps.Add(new DetectStep("5. 磁力在哪", "fail", "结果列表和详情页里都没找到磁力链接或 hash"));
                return null;
            }

            steps.Add(new DetectStep("5. 磁力在哪", "ok", $"列表里没有，详情页里有；每次搜索最多多打开 {rule.Html!.DetailLimit} 个详情页"));
        }

        if (preview.Count == 0)
        {
            steps.Add(new DetectStep("读取结果", "fail", "认出了结果条目，但没读出磁力"));
            return null;
        }

        steps.Add(new DetectStep("读取结果", "ok", $"预览前 {preview.Count} 条"));
        return new DetectResult(steps, rule, false, preview, template, keywordParam, Array.Empty<SamplePath>(), Array.Empty<SamplePath>(), null);
    }
}
