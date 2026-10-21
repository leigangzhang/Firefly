using System.Text.Json;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

public interface IResourceSource
{
    string Name { get; }

    /// <summary>maxPages 为空时用规则里的页数上限；降级搜索结果多、只取前几页。</summary>
    Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null);
}

public static class SourceFactory
{
    public const string JsonApi = "jsonApi";

    public const string Torznab = "torznab";

    public const string Html = "html";

    /// <summary>按规则建搜索源；类型不认识的源跳过，并记进 rules.Warnings。</summary>
    public static IReadOnlyList<IResourceSource> Create(RulesDocument rules, HttpClient http)
    {
        var sources = new List<IResourceSource>();
        foreach (var rule in rules.Sources.Where(s => s.Enabled))
        {
            if (TryCreate(rule, http, out var source, out var problem))
            {
                sources.Add(source);
            }
            else
            {
                rules.Warnings.Add(problem);
            }
        }

        return sources;
    }

    /// <summary>建一个源（配置页测试站点时也用）；规则不完整时给出原因。</summary>
    public static bool TryCreate(SourceRule rule, HttpClient http, out IResourceSource source, out string problem)
    {
        source = null!;
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            problem = "搜索源缺少 name，已跳过";
            return false;
        }

        if (rule.Endpoints.Count == 0)
        {
            problem = $"搜索源 {rule.Name} 缺少地址（endpoints），已跳过";
            return false;
        }

        if (string.Equals(rule.Kind, JsonApi, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(rule.Response.ItemsPath))
            {
                problem = $"搜索源 {rule.Name} 缺少结果列表路径（response.itemsPath），已跳过";
                return false;
            }

            source = new JsonApiSource(rule, http);
            return true;
        }

        if (string.Equals(rule.Kind, Torznab, StringComparison.OrdinalIgnoreCase))
        {
            source = new TorznabSource(rule, http);
            return true;
        }

        if (string.Equals(rule.Kind, Html, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(rule.Html?.Item))
            {
                problem = $"搜索源 {rule.Name} 缺少结果条目的选择器（html.item），已跳过";
                return false;
            }

            source = new HtmlSource(rule, http);
            return true;
        }

        problem = $"搜索源 {rule.Name} 的类型 {rule.Kind} 不受支持，已跳过";
        return false;
    }
}

/// <summary>
/// 通用的结构化搜索接口：按规则里的参数名拼 GET 请求，按规则里的路径从 JSON 响应取出种子列表和各个字段。
/// 站点换域名、换字段名只改规则文件，不改代码。
/// </summary>
public sealed class JsonApiSource : IResourceSource
{
    private readonly SourceRule _rule;
    private readonly HttpClient _http;
    private int _preferred;

    public JsonApiSource(SourceRule rule, HttpClient http)
    {
        _rule = rule;
        _http = http;
    }

    public string Name => _rule.Name;

    public async Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct, int? maxPages = null)
    {
        var results = new List<Release>();
        var limit = Math.Clamp(_rule.PageSize, 1, Math.Max(1, _rule.Request.MaxLimit));
        var pages = Math.Max(1, Math.Min(_rule.MaxPages, maxPages ?? _rule.MaxPages));
        for (var page = 0; page < pages; page++)
        {
            using var doc = await FetchAsync(keyword, limit, page * limit, ct).ConfigureAwait(false);
            var items = ParsePage(_rule, doc.RootElement, out var hasMore);
            if (items.Count == 0)
            {
                break;
            }

            results.AddRange(items);
            if (!hasMore)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>从一页响应里取出版本列表；业务状态码不对时抛 SourceException。</summary>
    public static IReadOnlyList<Release> ParsePage(SourceRule rule, JsonElement root, out bool hasMore)
    {
        var response = rule.Response;
        if (!string.IsNullOrEmpty(response.StatusPath))
        {
            var status = Text(Find(root, response.StatusPath));
            if (status != response.StatusOk)
            {
                throw new SourceException($"{rule.Name} 返回状态 {status}: {Text(Find(root, response.MessagePath))}");
            }
        }

        var list = Find(root, response.ItemsPath);
        var items = list is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(e => ToRelease(rule, e)).Where(r => r.InfoHash.Length > 0).ToList()
            : new List<Release>();
        var rawCount = list is { ValueKind: JsonValueKind.Array } a ? a.GetArrayLength() : 0;
        hasMore = string.IsNullOrEmpty(response.HasMorePath)
            ? rawCount >= Math.Max(1, Math.Min(rule.PageSize, rule.Request.MaxLimit))
            : Find(root, response.HasMorePath) is { ValueKind: JsonValueKind.True };
        return items;
    }

    private async Task<JsonDocument> FetchAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        Exception? last = null;
        var endpoints = _rule.Endpoints;
        var request = _rule.Request;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var index = (_preferred + i) % endpoints.Count;
            var separator = endpoints[index].Contains('?') ? '&' : '?';
            var url = $"{endpoints[index]}{separator}{request.KeywordParam}={Uri.EscapeDataString(keyword)}&{request.LimitParam}={limit}&{request.OffsetParam}={offset}";
            try
            {
                using var stream = await _http.GetStreamAsync(url, ct).ConfigureAwait(false);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                try
                {
                    // 先检查业务状态码，状态不对时换下一个镜像
                    ParsePage(_rule, doc.RootElement, out _);
                }
                catch
                {
                    doc.Dispose();
                    throw;
                }

                _preferred = index;
                return doc;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or SourceException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new SourceException($"{Name} 所有地址都不可用", last);
    }

    internal static Release ToRelease(SourceRule rule, JsonElement item)
    {
        var fields = rule.Response.Fields;
        var rawHash = (Text(Find(item, fields.InfoHash)) ?? string.Empty).Trim();
        var magnet = Text(Find(item, fields.Magnet));
        // 有的站没有单独的 hash 字段，只给磁力链接；Base32 的 hash 也统一成十六进制
        var hash = TorznabSource.Normalize(rawHash.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? TorznabSource.HashFromMagnet(rawHash) : rawHash)
            ?? TorznabSource.Normalize(TorznabSource.HashFromMagnet(magnet))
            ?? rawHash.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(magnet) && rawHash.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            magnet = rawHash;
        }

        var name = Text(Find(item, fields.Name)) ?? hash;
        var files = Find(item, fields.Files) is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Select(f => new ReleaseFile(Text(Find(f, fields.FilePath)) ?? string.Empty, Number(Find(f, fields.FileSize)))).ToList()
            : new List<ReleaseFile>();
        return new Release
        {
            InfoHash = hash,
            Name = name,
            Magnet = string.IsNullOrWhiteSpace(magnet) ? $"magnet:?xt=urn:btih:{hash}" : magnet,
            SizeBytes = Number(Find(item, fields.Size)),
            Files = files,
            CreatedAt = Date(Find(item, fields.CreatedAt), fields.CreatedAtFormat),
            Source = rule.Name,
            Traits = ReleaseParser.Parse(name),
        };
    }

    /// <summary>按点分隔的路径取值；路径为空返回 null。</summary>
    public static JsonElement? Find(JsonElement root, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? Text(JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.String => e.Value.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.Value.GetRawText(),
        _ => null,
    };

    private static long Number(JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.Number when e.Value.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(e.Value.GetString(), out var n) => n,
        _ => 0,
    };

    private static DateTimeOffset? Date(JsonElement? e, string format)
    {
        if (string.Equals(format, "iso", StringComparison.OrdinalIgnoreCase))
        {
            return DateTimeOffset.TryParse(Text(e), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        }

        var value = Number(e);
        if (value <= 0)
        {
            return null;
        }

        return string.Equals(format, "unixms", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }
}

public sealed class SourceException : Exception
{
    public SourceException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
