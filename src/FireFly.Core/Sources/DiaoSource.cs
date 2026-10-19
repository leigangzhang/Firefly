using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Sources;

public interface IResourceSource
{
    string Name { get; }

    Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct);
}

public static class SourceFactory
{
    public static IReadOnlyList<IResourceSource> Create(RulesDocument rules, HttpClient http) =>
        rules.Sources
            .Where(s => s.Enabled)
            .Select(s => s.Kind switch
            {
                "diao" => (IResourceSource)new DiaoSource(s, http),
                _ => null,
            })
            .OfType<IResourceSource>()
            .ToList();
}

/// <summary>
/// diao.im 结构化搜索接口：GET {endpoint}?keyword=&amp;limit=&amp;offset=，
/// 返回 { status, data: { torrents: [ { hash, name, magnet_uri, size, files[], created_at } ], has_more } }。
/// 一页最多 10 条（老项目实测超过会报错）；关键词一行一个词，多词是"全部命中"语义。
/// </summary>
public sealed class DiaoSource : IResourceSource
{
    private readonly SourceRule _rule;
    private readonly HttpClient _http;
    private int _preferred;

    public DiaoSource(SourceRule rule, HttpClient http)
    {
        _rule = rule;
        _http = http;
    }

    public string Name => _rule.Name;

    public async Task<IReadOnlyList<Release>> SearchAsync(string keyword, CancellationToken ct)
    {
        var results = new List<Release>();
        var limit = Math.Clamp(_rule.PageSize, 1, 10);
        for (var page = 0; page < Math.Max(1, _rule.MaxPages); page++)
        {
            var payload = await FetchAsync(keyword, limit, page * limit, ct).ConfigureAwait(false);
            if (payload?.Data?.Torrents is not { Count: > 0 } torrents)
            {
                break;
            }

            results.AddRange(torrents.Select(ToRelease).Where(r => r.InfoHash.Length > 0));
            if (!payload.Data.HasMore)
            {
                break;
            }
        }

        return results;
    }

    private async Task<DiaoResponse?> FetchAsync(string keyword, int limit, int offset, CancellationToken ct)
    {
        Exception? last = null;
        var endpoints = _rule.Endpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var index = (_preferred + i) % endpoints.Count;
            var url = $"{endpoints[index]}?keyword={Uri.EscapeDataString(keyword)}&limit={limit}&offset={offset}";
            try
            {
                var response = await _http.GetFromJsonAsync<DiaoResponse>(url, ct).ConfigureAwait(false);
                if (response is null || response.Status != 200)
                {
                    throw new SourceException($"{Name} 返回状态 {response?.Status}: {response?.Message}");
                }

                _preferred = index;
                return response;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or SourceException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw new SourceException($"{Name} 所有地址都不可用", last);
    }

    internal static Release ToRelease(DiaoTorrent t)
    {
        var hash = (t.Hash ?? string.Empty).Trim().ToUpperInvariant();
        var name = t.Name ?? hash;
        return new Release
        {
            InfoHash = hash,
            Name = name,
            Magnet = string.IsNullOrWhiteSpace(t.MagnetUri) ? $"magnet:?xt=urn:btih:{hash}" : t.MagnetUri!,
            SizeBytes = t.Size,
            Files = (t.Files ?? new()).Select(f => new ReleaseFile(f.Path ?? string.Empty, f.Size)).ToList(),
            CreatedAt = t.CreatedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.CreatedAt) : null,
            Source = "diao.im",
            Traits = ReleaseParser.Parse(name),
        };
    }

    internal sealed class DiaoResponse
    {
        public int Status { get; set; } = 200;

        public string? Message { get; set; }

        public DiaoData? Data { get; set; }
    }

    internal sealed class DiaoData
    {
        public List<DiaoTorrent>? Torrents { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }
    }

    internal sealed class DiaoTorrent
    {
        public string? Hash { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("magnet_uri")]
        public string? MagnetUri { get; set; }

        public long Size { get; set; }

        public List<DiaoFile>? Files { get; set; }

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; }
    }

    internal sealed class DiaoFile
    {
        public string? Path { get; set; }

        public long Size { get; set; }
    }
}

public sealed class SourceException : Exception
{
    public SourceException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
