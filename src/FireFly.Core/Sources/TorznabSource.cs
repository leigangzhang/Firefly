using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

/// <summary>
/// Torznab 接口（Jackett / Prowlarr）：GET {endpoint}/api?t=search&amp;q=..&amp;apikey=..，返回 RSS。
/// 一个 Jackett 地址背后可以是它配置好的所有站点，字段格式固定，不需要映射。
/// 只收有 infohash 或磁力链接的条目；只有 .torrent 下载地址的条目没法交给迅雷的磁力流程，跳过。
/// </summary>
public sealed class TorznabSource : IResourceSource
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";
    private readonly SourceRule _rule;
    private readonly HttpClient _http;
    private int _preferred;

    public TorznabSource(SourceRule rule, HttpClient http)
    {
        _rule = rule;
        _http = http;
    }

    public string Name => _rule.Name;

    /// <summary>一页最多取多少条；Jackett 默认上限 100。</summary>
    public const int PageLimit = 100;

    public async Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null)
    {
        var results = new List<Release>();
        var pages = Math.Max(1, Math.Min(_rule.MaxPages, maxPages ?? _rule.MaxPages));
        for (var page = 0; page < pages; page++)
        {
            var xml = await FetchAsync(keyword, PageLimit, page * PageLimit, ct).ConfigureAwait(false);
            var items = ParseFeed(_rule.Name, xml, out var raw);
            results.AddRange(items);
            if (raw < PageLimit)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>Jackett 的"Copy Torznab Feed"地址以 /torznab/ 结尾，Prowlarr 的以 /api 结尾；统一补成 .../api。</summary>
    public static string ApiUrl(string endpoint)
    {
        var url = endpoint.Trim();
        var query = string.Empty;
        var q = url.IndexOf('?');
        if (q >= 0)
        {
            query = url[(q + 1)..];
            url = url[..q];
        }

        url = url.TrimEnd('/');
        if (!url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            url += "/api";
        }

        return query.Length > 0 ? url + "?" + query : url;
    }

    public static string SearchUrl(string endpoint, string? apiKey, string keyword, int limit, int offset)
    {
        var url = ApiUrl(endpoint);
        var separator = url.Contains('?') ? '&' : '?';
        var key = string.IsNullOrWhiteSpace(apiKey) ? string.Empty : $"&apikey={Uri.EscapeDataString(apiKey.Trim())}";
        return $"{url}{separator}t=search&q={Uri.EscapeDataString(keyword)}&limit={limit}&offset={offset}&extended=1{key}";
    }

    private async Task<string> FetchAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        Exception? last = null;
        var endpoints = _rule.Endpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var index = (_preferred + i) % endpoints.Count;
            try
            {
                var xml = await _http.GetStringAsync(SearchUrl(endpoints[index], _rule.ApiKey, keyword, limit, offset), ct).ConfigureAwait(false);
                ParseFeed(_rule.Name, xml, out _);
                _preferred = index;
                return xml;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SourceException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new SourceException(last is SourceException ? last.Message : $"{Name} 所有地址都不可用", last);
    }

    /// <summary>解析 Torznab RSS；返回 &lt;error&gt; 时抛 SourceException（如 API Key 不对）。raw 是这一页的条目数（含跳过的）。</summary>
    public static IReadOnlyList<Release> ParseFeed(string sourceName, string xml, out int raw)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new SourceException($"{sourceName} 返回的不是 Torznab 格式（{ex.Message}）");
        }

        if (doc.Root?.Name.LocalName == "error")
        {
            var code = (string?)doc.Root.Attribute("code");
            var description = (string?)doc.Root.Attribute("description");
            throw new SourceException($"{sourceName} 返回错误 {code}: {description}");
        }

        var items = doc.Descendants("item").ToList();
        raw = items.Count;
        if (doc.Root?.Name.LocalName != "rss")
        {
            throw new SourceException($"{sourceName} 返回的不是 Torznab 格式");
        }

        var list = new List<Release>();
        foreach (var item in items)
        {
            var attrs = item.Elements(Torznab + "attr")
                .Select(a => (Name: ((string?)a.Attribute("name"))?.ToLowerInvariant(), Value: (string?)a.Attribute("value")))
                .Where(a => a.Name is not null && a.Value is not null)
                .GroupBy(a => a.Name!)
                .ToDictionary(g => g.Key, g => g.First().Value!);
            var link = (string?)item.Element("link");
            var enclosure = (string?)item.Element("enclosure")?.Attribute("url");
            var magnet = attrs.GetValueOrDefault("magneturl")
                ?? new[] { link, enclosure, (string?)item.Element("guid") }.FirstOrDefault(u => u?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true);
            var hash = Normalize(attrs.GetValueOrDefault("infohash")) ?? Normalize(HashFromMagnet(magnet));
            if (hash is null)
            {
                continue;
            }

            var title = ((string?)item.Element("title") ?? hash).Trim();
            list.Add(new Release
            {
                InfoHash = hash,
                Name = title,
                Magnet = string.IsNullOrWhiteSpace(magnet) ? $"magnet:?xt=urn:btih:{hash}" : magnet,
                SizeBytes = Long((string?)item.Element("size")) ?? Long(attrs.GetValueOrDefault("size")) ?? Long((string?)item.Element("enclosure")?.Attribute("length")) ?? 0,
                CreatedAt = DateTimeOffset.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null,
                Source = sourceName,
                Traits = ReleaseParser.Parse(title),
            });
        }

        return list;
    }

    /// <summary>从磁力链接里取 btih（40 位十六进制，或 32 位 Base32）。</summary>
    internal static string? HashFromMagnet(string? magnet)
    {
        if (string.IsNullOrEmpty(magnet))
        {
            return null;
        }

        var i = magnet.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            return null;
        }

        var start = i + "urn:btih:".Length;
        var end = magnet.IndexOf('&', start);
        return end < 0 ? magnet[start..] : magnet[start..end];
    }

    /// <summary>统一成 40 位大写十六进制；Base32 的转换过来；格式不对返回 null。</summary>
    internal static string? Normalize(string? hash)
    {
        hash = hash?.Trim();
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
        {
            return hash.ToUpperInvariant();
        }

        if (hash.Length == 32)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new byte[20];
            int buffer = 0, bits = 0, index = 0;
            foreach (var c in hash.ToUpperInvariant())
            {
                var v = alphabet.IndexOf(c);
                if (v < 0)
                {
                    return null;
                }

                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes[index++] = (byte)((buffer >> bits) & 0xFF);
                }
            }

            return Convert.ToHexString(bytes);
        }

        return null;
    }

    private static long? Long(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}
