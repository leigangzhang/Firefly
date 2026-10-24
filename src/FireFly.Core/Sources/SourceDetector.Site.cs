namespace FireFly.Core.Sources;

/// <summary>只粘贴了站点地址（如 https://example.com）时，先找到这个站怎么搜，再按搜索结果页识别。</summary>
public sealed partial class SourceDetector
{
    /// <summary>首页没有搜索框时试的常见搜索地址；{0} 是 URL 编码后的样例片名。</summary>
    private static readonly string[] CommonSearchPaths =
    {
        "/search?keyword={0}", "/search?q={0}", "/search?wd={0}", "/search?kw={0}", "/search?k={0}",
        "/api/search?keyword={0}", "/api/search?q={0}",
        "/search/{0}", "/s/{0}", "/search/{0}/1", "/?s={0}",
    };

    /// <summary>地址里既没有搜索词参数，也没有搜索词路径：用户只给了站点。</summary>
    internal static bool IsBareSite(Uri page, string? sample) =>
        FindKeywordParam(ParseQuery(page.Query), sample) is null && PathKeyword(page, sample) is null;

    /// <summary>
    /// 先找首页里的搜索框（GET 表单），用样例片名拼出搜索地址；没有时试常见的搜索地址，
    /// 返回 JSON 资源列表、或网页里认得出一组含片名结果的，就当它是搜索地址。
    /// </summary>
    private async Task<Uri?> FindSearchUrlAsync(Uri site, string sample, List<DetectStep> steps, CancellationToken ct)
    {
        var home = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
        var first = await TryAsync(home, sample, ct).ConfigureAwait(false);
        if (first.Blocked)
        {
            steps.Add(new DetectStep("找搜索入口", "fail", $"{site.Host} 开启了 Cloudflare 人机验证，插件在服务器上打不开它的网页"));
            steps.Add(new DetectStep("怎么办", "info", NotSupported));
            return null;
        }

        var homeDoc = first.Html is { } html ? MiniHtml.Parse(html) : null;
        if (await OpenSearchUrlAsync(homeDoc, home, sample, ct).ConfigureAwait(false) is { } fromOpenSearch)
        {
            steps.Add(new DetectStep("找搜索入口", "ok", $"站点声明了搜索地址（OpenSearch），用\u201c{sample}\u201d搜了一次：{Uri.UnescapeDataString(fromOpenSearch.PathAndQuery)}"));
            return fromOpenSearch;
        }

        if (homeDoc is not null && SearchFormUrl(homeDoc, home, sample) is { } fromForm)
        {
            steps.Add(new DetectStep("找搜索入口", "ok", $"首页有搜索框，用“{sample}”搜了一次：{Uri.UnescapeDataString(fromForm.PathAndQuery)}"));
            return fromForm;
        }

        foreach (var path in CommonSearchPaths)
        {
            if (_requests >= MaxRequests)
            {
                break;
            }

            var url = new Uri(home, string.Format(System.Globalization.CultureInfo.InvariantCulture, path, Uri.EscapeDataString(sample)));
            var attempt = await TryAsync(url, sample, ct).ConfigureAwait(false);
            if (attempt.Blocked)
            {
                steps.Add(new DetectStep("找搜索入口", "fail", $"{site.Host} 的搜索页开启了人机验证"));
                steps.Add(new DetectStep("怎么办", "info", NotSupported));
                return null;
            }

            if (attempt.Json?.Mapped is not null || (attempt.Html is { } page && GuessHtml(MiniHtml.Parse(page), sample, url) is not null))
            {
                steps.Add(new DetectStep("找搜索入口", "ok", $"首页没有搜索框，试出搜索地址：{Uri.UnescapeDataString(url.PathAndQuery)}"));
                return url;
            }
        }

        steps.Add(new DetectStep("找搜索入口", "fail", first.Error is { } error && first.Html is null && first.Json is null
            ? $"打不开 {home}：{error}"
            : "首页没有找到搜索框，常见的搜索地址也都不对。请在站内搜一次，把搜索结果页的地址粘贴进来"));
        return null;
    }

    /// <summary>
    /// 站点的 OpenSearch 描述（浏览器"添加搜索引擎"用的标准文件）：首页 &lt;link rel="search"&gt; 指向的，或 /opensearch.xml。
    /// 里面的网页搜索模板把 {searchTerms} 换成样例片名。
    /// </summary>
    private async Task<Uri?> OpenSearchUrlAsync(HtmlNode? homeDoc, Uri home, string sample, CancellationToken ct)
    {
        var href = homeDoc is null ? null : MiniHtml.QueryAll(homeDoc, "link[rel=search]")
            .FirstOrDefault(l => (l.Attr("type") ?? string.Empty).Contains("opensearch", StringComparison.OrdinalIgnoreCase))?.Attr("href");
        if (!Uri.TryCreate(home, href ?? "/opensearch.xml", out var description) || !SameSite(description.ToString(), home) || _requests >= MaxRequests)
        {
            return null;
        }

        _requests++;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.GetAsync(description, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? OpenSearchTemplate(await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false), home, sample) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>从 OpenSearch 描述里取网页搜索模板（type 为 text/html 的 Url，没有时取第一个），换上样例片名。</summary>
    internal static Uri? OpenSearchTemplate(string xml, Uri home, string sample)
    {
        try
        {
            var urls = System.Xml.Linq.XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Url").ToList();
            var url = urls.FirstOrDefault(e => (string?)e.Attribute("type") == "text/html") ?? urls.FirstOrDefault();
            var template = (string?)url?.Attribute("template");
            if (template is null || !template.Contains("{searchTerms}", StringComparison.Ordinal))
            {
                return null;
            }

            var filled = System.Text.RegularExpressions.Regex.Replace(template.Replace("{searchTerms}", Uri.EscapeDataString(sample), StringComparison.Ordinal), @"\{[^}]*\?\}", string.Empty)
                .Replace("{startPage}", "1", StringComparison.Ordinal);
            return Uri.TryCreate(home, filled, out var result) && SameSite(result.ToString(), home) ? KeepScheme(result, home) : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>首页里的搜索表单：GET 方式、有一个文字输入框；隐藏字段原样带上。</summary>
    internal static Uri? SearchFormUrl(HtmlNode doc, Uri home, string sample)
    {
        Uri? best = null;
        var bestScore = -1;
        foreach (var form in MiniHtml.QueryAll(doc, "form"))
        {
            if (string.Equals(form.Attr("method"), "post", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inputs = form.Descendants().Where(n => n.Tag == "input" && !string.IsNullOrEmpty(n.Attr("name"))).ToList();
            var text = inputs.FirstOrDefault(n => n.Attr("type") is null or "" or "text" or "search");
            if (text is null)
            {
                continue;
            }

            var action = form.Attr("action");
            if (action is not null && (action.StartsWith('#') || action.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!Uri.TryCreate(home, string.IsNullOrWhiteSpace(action) ? home.AbsolutePath : action, out var target) || !SameSite(target.ToString(), home))
            {
                continue;
            }

            var name = text.Attr("name")!;
            var score = (text.Attr("type") == "search" ? 2 : 0) + (KeywordParams.Contains(name, StringComparer.OrdinalIgnoreCase) ? 2 : 0)
                + ((action ?? string.Empty).Contains("search", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if (score <= bestScore)
            {
                continue;
            }

            var parts = new List<string> { Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(sample) };
            parts.AddRange(inputs.Where(n => n.Attr("type") == "hidden" && n != text)
                .Select(n => Uri.EscapeDataString(n.Attr("name")!) + "=" + Uri.EscapeDataString(n.Attr("value") ?? string.Empty)));
            best = KeepScheme(new Uri(target.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parts)), home);
            bestScore = score;
        }

        return best;
    }

    /// <summary>站点自己写的搜索地址常是 http；同一个域名时沿用用户粘贴的协议（通常是 https）。</summary>
    private static Uri KeepScheme(Uri url, Uri home) =>
        url.Scheme != home.Scheme && string.Equals(url.Host, home.Host, StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(url) { Scheme = home.Scheme, Port = -1 }.Uri
            : url;
}
