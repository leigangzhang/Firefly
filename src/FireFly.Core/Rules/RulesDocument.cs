using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FireFly.Core.Models;

namespace FireFly.Core.Rules;

/// <summary>规则文件：搜索源、发布者关键词、迅雷临时后缀等会随上游变化的东西都在这里。</summary>
public sealed class RulesDocument
{
    /// <summary>插件能读懂的最高规则格式。格式不兼容地升级时加一，旧插件会拒绝读新格式的文件。</summary>
    public const int SupportedFormat = 1;

    /// <summary>规则文件格式版本（结构）；本地覆盖文件不写时按 1 处理。</summary>
    public int Format { get; set; } = SupportedFormat;

    /// <summary>规则内容版本，每次改内置规则加一。</summary>
    public int Version { get; set; }

    public string? UpdatedAt { get; set; }

    public List<SourceRule> Sources { get; set; } = new();

    public List<string> PublisherKeywords { get; set; } = new();

    public ThunderRule Thunder { get; set; } = new();

    public List<string> VideoExtensions { get; set; } = new();

    public List<string> SubtitleExtensions { get; set; } = new();

    public long MinVideoBytes { get; set; }

    /// <summary>热门榜单的筛选项目录（分类、地区、出品、分级）。TMDb 的公司 / 电视网 ID 写错时改这里即可。</summary>
    public DiscoverRule Discover { get; set; } = new();

    /// <summary>配置页下拉框里可选的 TMDb 接口 / 图片地址，第一项是默认值。入口被屏蔽时由社区在这里补充可用地址。</summary>
    public TmdbRule Tmdb { get; set; } = new();

    /// <summary>是否叠加了数据目录里的 rules.json。</summary>
    [JsonIgnore]
    public bool HasOverride { get; set; }

    /// <summary>加载时发现的问题（覆盖文件读不了、某个源的类型不认识等），配置页状态区会显示。</summary>
    [JsonIgnore]
    public List<string> Warnings { get; set; } = new();

    public bool IsVideo(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool IsSubtitle(string path) =>
        SubtitleExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>用哪段代码请求和解析，不是站点名。目前只有 jsonApi。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>同一个源的多个镜像地址，按顺序尝试，上次成功的排到最前。站点换域名时改这里。</summary>
    public List<string> Endpoints { get; set; } = new();

    public int PageSize { get; set; } = 10;

    public int MaxPages { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public JsonApiRequest Request { get; set; } = new();

    public JsonApiResponse Response { get; set; } = new();
}

/// <summary>jsonApi 的请求：GET {endpoint}?{keyword}=..&amp;{limit}=..&amp;{offset}=..</summary>
public sealed class JsonApiRequest
{
    public string KeywordParam { get; set; } = "keyword";

    public string LimitParam { get; set; } = "limit";

    public string OffsetParam { get; set; } = "offset";

    /// <summary>站点允许的单页上限，pageSize 超过时按它截断。</summary>
    public int MaxLimit { get; set; } = 10;
}

/// <summary>jsonApi 的响应映射。路径用点分隔，如 data.torrents；字段路径相对于列表里的每一项。</summary>
public sealed class JsonApiResponse
{
    /// <summary>业务状态码所在路径；为空表示不检查。</summary>
    public string? StatusPath { get; set; }

    /// <summary>表示成功的状态值，按字符串比较。</summary>
    public string StatusOk { get; set; } = "200";

    public string? MessagePath { get; set; }

    public string ItemsPath { get; set; } = string.Empty;

    /// <summary>"还有下一页"标志所在路径；为空时按本页是否取满来判断。</summary>
    public string? HasMorePath { get; set; }

    public JsonApiFields Fields { get; set; } = new();
}

public sealed class JsonApiFields
{
    public string InfoHash { get; set; } = "hash";

    public string Name { get; set; } = "name";

    /// <summary>为空或取不到时用 InfoHash 拼磁力。</summary>
    public string? Magnet { get; set; }

    public string? Size { get; set; }

    public string? CreatedAt { get; set; }

    /// <summary>unix（秒）或 iso（ISO 8601 文本）。</summary>
    public string CreatedAtFormat { get; set; } = "unix";

    public string? Files { get; set; }

    public string FilePath { get; set; } = "path";

    public string FileSize { get; set; } = "size";
}

public sealed class TmdbRule
{
    public List<FilterOption> ApiBases { get; set; } = new();

    public List<FilterOption> ImageBases { get; set; } = new();
}

public sealed class ThunderRule
{
    public List<string> TempSuffixes { get; set; } = new();
}

public sealed class DiscoverRule
{
    public List<DiscoverCategory> Categories { get; set; } = new();

    public List<FilterOption> Regions { get; set; } = new();

    /// <summary>电影的"出品"：TMDb 制片公司（with_companies）。</summary>
    public List<FilterOption> Companies { get; set; } = new();

    /// <summary>剧集的"出品"：TMDb 电视网 / 平台（with_networks）。</summary>
    public List<FilterOption> Networks { get; set; } = new();

    public string CertificationCountry { get; set; } = "US";

    public List<FilterOption> Certifications { get; set; } = new();

    /// <summary>详情页"播放平台"依次尝试的地区。</summary>
    public List<string> WatchRegions { get; set; } = new() { "CN", "US" };
}

public sealed class DiscoverCategory
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>TMDb 路径段：movie 或 tv。</summary>
    public string Segment { get; set; } = "movie";

    /// <summary>分类自带的类型限制，如动漫 = 16；"|" 表示或，"," 表示且。</summary>
    public string? WithGenres { get; set; }

    public string? WithoutGenres { get; set; }

    public MediaKind Kind => Segment == "tv" ? MediaKind.Series : MediaKind.Movie;
}

public sealed record FilterOption(string Value, string Label);

/// <summary>
/// 规则加载顺序：先读编进 DLL 的内置规则，再把数据目录里的 rules.json 叠加上去。
/// 叠加是按字段的深度合并：对象逐个字段覆盖；sources 按 name 合并（同名覆盖其中写到的字段，新名字追加在后）；
/// 其他数组整体替换。所以覆盖文件只需要写要改的部分，比如只改某个源的 endpoints。
/// </summary>
public static class RulesLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>内置默认规则（编进 DLL）。</summary>
    public static RulesDocument LoadDefault() => Parse(DefaultJson());

    /// <summary>内置规则叠加本地覆盖文件；覆盖文件有问题时退回内置规则，并把原因放进 Warnings。</summary>
    public static RulesDocument Load(string? overridePath)
    {
        if (string.IsNullOrEmpty(overridePath) || !File.Exists(overridePath))
        {
            return LoadDefault();
        }

        try
        {
            return Merge(DefaultJson(), File.ReadAllText(overridePath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            var builtin = LoadDefault();
            builtin.Warnings.Add($"rules.json 无法读取，已使用内置规则：{ex.Message}");
            return builtin;
        }
    }

    /// <summary>把覆盖文件叠加到基础规则上。</summary>
    public static RulesDocument Merge(string baseJson, string overrideJson)
    {
        var baseNode = JsonNode.Parse(baseJson, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("内置规则不是 JSON 对象");
        var overlay = JsonNode.Parse(overrideJson, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("rules.json 不是 JSON 对象");
        var builtin = Parse(baseJson);

        var format = (int?)overlay["format"] ?? 1;
        if (format > RulesDocument.SupportedFormat)
        {
            builtin.Warnings.Add($"rules.json 的格式版本 {format} 高于插件支持的 {RulesDocument.SupportedFormat}，已使用内置规则；请升级插件");
            return builtin;
        }

        // 0.2 及以前的 rules.json 是整份内置规则的拷贝（没有 format 字段），叠加上去会把新版内置规则改回旧的
        if (overlay["format"] is null && (int?)overlay["version"] is int legacy && legacy < builtin.Version)
        {
            builtin.Warnings.Add($"rules.json 是旧版规则（版本 {legacy}），已忽略；需要覆盖时按新格式只写要改的部分");
            return builtin;
        }

        overlay.Remove("format");
        overlay.Remove("version");
        DeepMerge(baseNode, overlay);
        var merged = baseNode.Deserialize<RulesDocument>(Options) ?? throw new InvalidOperationException("规则无法解析");
        merged.HasOverride = true;
        return merged;
    }

    public static RulesDocument Parse(string json) =>
        JsonSerializer.Deserialize<RulesDocument>(json, Options) ?? throw new InvalidOperationException("规则无法解析");

    private static string DefaultJson()
    {
        using var stream = typeof(RulesLoader).Assembly
            .GetManifestResourceStream("FireFly.Core.Rules.rules.default.json")
            ?? throw new InvalidOperationException("内置规则文件缺失");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeepMerge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            overlay.Remove(key);
            var existing = target[key];
            if (existing is JsonObject to && value is JsonObject from)
            {
                DeepMerge(to, from);
            }
            else if (existing is JsonArray ta && value is JsonArray fa && string.Equals(key, "sources", StringComparison.OrdinalIgnoreCase))
            {
                MergeByName(ta, fa);
            }
            else
            {
                target[key] = value;
            }
        }
    }

    private static void MergeByName(JsonArray target, JsonArray overlay)
    {
        foreach (var item in overlay.ToList())
        {
            overlay.Remove(item);
            var name = (item as JsonObject)?["name"]?.GetValue<string>();
            var match = name is null ? null : target.OfType<JsonObject>().FirstOrDefault(t => string.Equals(t["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && item is JsonObject obj)
            {
                DeepMerge(match, obj);
            }
            else
            {
                target.Add(item);
            }
        }
    }
}
