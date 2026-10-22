using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

/// <summary>
/// 网页站点：没有数据接口、搜索结果直接写在网页里的磁力站。按规则里的选择器从结果页取出每条结果；
/// 磁力只在详情页的站，再打开前 DetailLimit 条的详情页去找（最多同时 3 个，结果按链接缓存）。
/// </summary>
public sealed partial class HtmlSource : IResourceSource
{
    private const int DetailConcurrency = 3;
    private const int DetailCacheLimit = 5000;

    /// <summary>详情页链接 → 磁力；同一条结果在不同关键词的搜索里会反复出现。</summary>
    private static readonly ConcurrentDictionary<string, string?> DetailCache = new();

    private readonly SourceRule _rule;
    private readonly HttpClient _http;
    private int _preferred;

    public HtmlSource(SourceRule rule, HttpClient http)
    {
        _rule = rule;
        _http = http;
    }

    public string Name => _rule.Name;

    private HtmlRule Rule => _rule.Html!;

    public async Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null)
    {
        var results = new List<Release>();
        var pending = new List<Pending>();
        var paged = _rule.Endpoints.Any(e => e.Contains("{page}", StringComparison.Ordinal));
        var pages = paged ? Math.Max(1, Math.Min(_rule.MaxPages, maxPages ?? _rule.MaxPages)) : 1;
        for (var page = 0; page < pages; page++)
        {
            var (html, url) = await FetchAsync(keyword, Rule.FirstPage + page, ct).ConfigureAwait(false);
            var items = Extract(_rule, MiniHtml.Parse(html), url);
            results.AddRange(items.Where(i => i.Release is not null).Select(i => i.Release!));
            pending.AddRange(items.Where(i => i.Release is null && i.Link is not null));
            if (items.Count == 0)
            {
                break;
            }
        }

        if (Rule.Detail && pending.Count > 0)
        {
            results.AddRange(await ResolveDetailsAsync(pending.Take(Math.Max(0, Rule.DetailLimit)).ToList(), ct).ConfigureAwait(false));
        }

        return results.GroupBy(r => r.InfoHash).Select(g => g.First()).ToList();
    }

    /// <summary>一条结果：能直接拿到磁力的给 Release，否则给详情页链接和已读出的字段。</summary>
    public sealed record Pending(Release? Release, Uri? Link, string Title, long Size, DateTimeOffset? Date);

    /// <summary>按规则从结果页取出所有结果（识别站点和测试时也用）。</summary>
    public static List<Pending> Extract(SourceRule rule, HtmlNode doc, Uri pageUrl)
    {
        var html = rule.Html!;
        var list = new List<Pending>();
        foreach (var item in MiniHtml.QueryAll(doc, html.Item))
        {
            var title = MiniHtml.Value(item, html.Title) ?? MiniHtml.Value(item, "a") ?? string.Empty;
            if (title.Length == 0)
            {
                continue;
            }

            var href = MiniHtml.Value(item, html.Link) ?? MiniHtml.Value(item, "a@href");
            Uri? link = href is not null && !href.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(pageUrl, href, out var abs) ? abs : null;
            var text = item.Text;
            var size = ParseSize(MiniHtml.Value(item, html.Size) ?? text);
            var date = ParseDate(MiniHtml.Value(item, html.Date) ?? text);
            var magnet = MiniHtml.Value(item, html.Magnet) ?? FindMagnet(item);
            var release = magnet is null ? null : ToRelease(rule.Name, magnet, title, size, date);
            list.Add(new Pending(release, link, title, size, date));
        }

        return list;
    }

    /// <summary>
    /// 网页里的磁力：先找 magnet: 链接，再找页面文字里的 40 位 hash，最后找属性里的。
    /// 文字优先：详情页常把 hash 写成"特征码"，而属性里的 40 位十六进制可能是别的东西（如表单令牌）。
    /// </summary>
    public static string? FindMagnet(HtmlNode scope)
    {
        var nodes = scope.Descendants().Prepend(scope).ToList();
        var magnet = nodes.SelectMany(n => n.Attributes.Values).FirstOrDefault(v => v.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        if (magnet is not null)
        {
            return magnet;
        }

        if (HashPattern().Match(scope.Text) is { Success: true } t)
        {
            return t.Value;
        }

        return nodes.SelectMany(n => n.Attributes.Values).Select(v => HashPattern().Match(v)).FirstOrDefault(m => m.Success)?.Value;
    }

    internal static Release? ToRelease(string source, string magnetOrHash, string title, long size, DateTimeOffset? date)
    {
        var isMagnet = magnetOrHash.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);
        var hash = InfoHash.Normalize(isMagnet ? InfoHash.FromMagnet(magnetOrHash) : magnetOrHash);
        if (hash is null)
        {
            return null;
        }

        return new Release
        {
            InfoHash = hash,
            Name = title,
            Magnet = isMagnet ? magnetOrHash : $"magnet:?xt=urn:btih:{hash}",
            SizeBytes = size,
            CreatedAt = date,
            Source = source,
            Traits = ReleaseParser.Parse(title),
        };
    }

    private async Task<List<Release>> ResolveDetailsAsync(List<Pending> pending, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(DetailConcurrency);
        var tasks = pending.Select(async p =>
        {
            var key = p.Link!.ToString();
            if (!DetailCache.TryGetValue(key, out var magnet))
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    magnet = await FetchDetailAsync(p.Link!, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                if (DetailCache.Count > DetailCacheLimit)
                {
                    DetailCache.Clear();
                }

                DetailCache[key] = magnet;
            }

            return magnet is null ? null : ToRelease(_rule.Name, magnet, p.Title, p.Size, p.Date);
        });
        var releases = await Task.WhenAll(tasks).ConfigureAwait(false);
        return releases.Where(r => r is not null).Select(r => r!).ToList();
    }

    private async Task<string?> FetchDetailAsync(Uri link, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var html = await _http.GetStringAsync(link, cts.Token).ConfigureAwait(false);
            var doc = MiniHtml.Parse(html);
            return MiniHtml.Value(doc, Rule.Magnet) ?? FindMagnet(doc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 单个详情页打不开只少一条结果，不算整个站点失败
            return null;
        }
    }

    private async Task<(string Html, Uri Url)> FetchAsync(string keyword, int page, CancellationToken ct)
    {
        Exception? last = null;
        var endpoints = _rule.Endpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var index = (_preferred + i) % endpoints.Count;
            var url = new Uri(SearchUrl(endpoints[index], keyword, page));
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var mitigated = response.Headers.TryGetValues("cf-mitigated", out var m) ? string.Join(",", m) : null;
                    throw new SourceException(SourceDetector.IsChallenge((int)response.StatusCode, mitigated, response.Headers.Server.ToString(), body)
                        ? $"{Name} 开启了人机验证，插件打不开它的搜索页"
                        : $"{Name} 返回 HTTP {(int)response.StatusCode}");
                }

                _preferred = index;
                return (body, url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SourceException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new SourceException(last is SourceException ? last.Message : $"{Name} 所有地址都不可用", last);
    }

    /// <summary>把地址模板里的 {keyword}、{page} 换成实际值。</summary>
    public static string SearchUrl(string template, string keyword, int page) =>
        template.Replace("{keyword}", Uri.EscapeDataString(keyword), StringComparison.Ordinal)
            .Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>"18.6 GB""980MB""1.2 TiB" → 字节数；认不出为 0。</summary>
    public static long ParseSize(string? text)
    {
        if (string.IsNullOrEmpty(text) || SizePattern().Match(text) is not { Success: true } m)
        {
            return 0;
        }

        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = char.ToUpperInvariant(m.Groups[2].Value[0]);
        var factor = unit switch
        {
            'T' => 1024d * 1024 * 1024 * 1024,
            'G' => 1024d * 1024 * 1024,
            'M' => 1024d * 1024,
            'K' => 1024d,
            _ => 1d,
        };
        return (long)(n * factor);
    }

    /// <summary>文字里的第一个日期：2023-04-05、2023/4/5、2023.04.05、2023年4月5日。</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text) || DatePattern().Match(text) is not { Success: true } m)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 0, 0, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(TB|TiB|GB|GiB|MB|MiB|KB|KiB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"((?:19|20)\d{2})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![0-9A-Za-z])[0-9a-fA-F]{40}(?![0-9A-Za-z])")]
    internal static partial Regex HashPattern();
}
