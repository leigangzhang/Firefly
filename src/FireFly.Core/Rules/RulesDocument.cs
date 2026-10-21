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

    /// <summary>站点顺序（按 name）：越靠前，同等条件下结果越靠前。没列到的站点按原顺序排在后面。</summary>
    public List<string> SourceOrder { get; set; } = new();

    /// <summary>旧写法：发布者关键词整体替换，对所有站点、所有类型生效。写了它就忽略 keywords.items。</summary>
    public List<string> PublisherKeywords { get; set; } = new();

    /// <summary>优选关键词：每个词可以限定站点和类型；disable 停用词，order 决定优先级。</summary>
    public KeywordsRule Keywords { get; set; } = new();

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

    /// <summary>生效的关键词，按优先级排好：停用的去掉，order 里列到的在前，其余保持原顺序。</summary>
    public IReadOnlyList<KeywordItem> EffectiveKeywords()
    {
        if (PublisherKeywords.Count > 0)
        {
            return PublisherKeywords.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => new KeywordItem { Word = w.Trim() }).ToList();
        }

        var disabled = new HashSet<string>(Keywords.Disable, StringComparer.OrdinalIgnoreCase);
        var items = Keywords.Items
            .Where(k => !string.IsNullOrWhiteSpace(k.Word) && !disabled.Contains(k.Word))
            .GroupBy(k => k.Word.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return SortByOrder(items, k => k.Word, Keywords.Order);
    }

    /// <summary>按名字列表排序：列到的按列表顺序在前，没列到的保持原顺序排在后面。</summary>
    public static List<T> SortByOrder<T>(IEnumerable<T> items, Func<T, string> key, IReadOnlyList<string> order)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            rank.TryAdd(order[i], i);
        }

        return items
            .Select((item, index) => (item, index))
            .OrderBy(x => rank.TryGetValue(key(x.item), out var r) ? r : int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    public bool IsVideo(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool IsSubtitle(string path) =>
        SubtitleExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>用哪段代码请求和解析，不是站点名：jsonApi、torznab 或 html。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>这个站点负责的资源类型（movie / tv / anime / variety / documentary，即 discover.categories 的 key）；空 = 全部。</summary>
    public List<string> Types { get; set; } = new();

    /// <summary>torznab 的 API Key（Jackett / Prowlarr）。</summary>
    public string? ApiKey { get; set; }

    /// <summary>同一个源的多个镜像地址，按顺序尝试，上次成功的排到最前。站点换域名时改这里。</summary>
    public List<string> Endpoints { get; set; } = new();

    public int PageSize { get; set; } = 10;

    public int MaxPages { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    public JsonApiRequest Request { get; set; } = new();

    public JsonApiResponse Response { get; set; } = new();

    /// <summary>html（网页站点）的解析规则。</summary>
    public HtmlRule? Html { get; set; }
}

/// <summary>
/// 网页站点：endpoints 是搜索地址模板，{keyword} 换成搜索词、{page} 换成页码。
/// 字段写法"选择器@属性"，相对每条结果；不写属性取文字。磁力、体积、时间可以不写，插件在每条结果里自己找。
/// </summary>
public sealed class HtmlRule
{
    /// <summary>每条结果的选择器，如 div.search-item。</summary>
    public string Item { get; set; } = string.Empty;

    public string? Title { get; set; }

    /// <summary>详情页链接，如 h3 a@href。</summary>
    public string? Link { get; set; }

    public string? Size { get; set; }

    public string? Date { get; set; }

    /// <summary>磁力链接；不写时在结果里找 magnet: 链接或 40 位 hash。</summary>
    public string? Magnet { get; set; }

    /// <summary>磁力在详情页：结果里找不到时打开 Link 指向的页面去找。</summary>
    public bool Detail { get; set; }

    /// <summary>每次搜索最多打开多少个详情页。</summary>
    public int DetailLimit { get; set; } = 20;

    /// <summary>{page} 的第一页是几（多数站是 1）。</summary>
    public int FirstPage { get; set; } = 1;
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

    /// <summary>unix（秒）、unixms（毫秒）或 iso（ISO 8601 文本）。</summary>
    public string CreatedAtFormat { get; set; } = "unix";

    public string? Files { get; set; }

    public string FilePath { get; set; } = "path";

    public string FileSize { get; set; } = "size";
}

public sealed class KeywordsRule
{
    /// <summary>关键词。JSON 里可以直接写字符串（不限范围），也可以写 { "word", "sources", "types" }。覆盖层按 word 合并。</summary>
    public List<KeywordItem> Items { get; set; } = new();

    /// <summary>停用的词（通常是内置词）。</summary>
    public List<string> Disable { get; set; } = new();

    /// <summary>优先级顺序：越靠前，搜索时越先用、结果越靠前。</summary>
    public List<string> Order { get; set; } = new();
}

[JsonConverter(typeof(KeywordItemConverter))]
public sealed class KeywordItem
{
    public string Word { get; set; } = string.Empty;

    /// <summary>只用于这些站点（按 name）；空 = 全部站点。</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>只用于这些类型；空 = 全部类型。</summary>
    public List<string> Types { get; set; } = new();

    public bool AppliesTo(string source, string? category) =>
        (Sources.Count == 0 || Sources.Contains(source, StringComparer.OrdinalIgnoreCase))
        && (category is null || Types.Count == 0 || Types.Contains(category, StringComparer.OrdinalIgnoreCase));
}

/// <summary>关键词既可以是字符串也可以是对象。</summary>
public sealed class KeywordItemConverter : JsonConverter<KeywordItem>
{
    public override KeywordItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new KeywordItem { Word = reader.GetString() ?? string.Empty };
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var item = new KeywordItem();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            switch (p.Name.ToLowerInvariant())
            {
                case "word":
                    item.Word = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : string.Empty;
                    break;
                case "sources":
                    item.Sources = Strings(p.Value);
                    break;
                case "types":
                    item.Types = Strings(p.Value);
                    break;
            }
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, KeywordItem value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(options.PropertyNamingPolicy?.ConvertName("Word") ?? "Word", value.Word);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("Sources") ?? "Sources");
        JsonSerializer.Serialize(writer, value.Sources, options);
        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName("Types") ?? "Types");
        JsonSerializer.Serialize(writer, value.Types, options);
        writer.WriteEndObject();
    }

    private static List<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList()
            : new List<string>();
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
    public static RulesDocument Load(string? overridePath) => Load(overridePath, null);

    /// <summary>
    /// 三层叠加：内置规则 → 数据目录的 rules.json → 配置页的改动（uiOverlay，格式和 rules.json 覆盖片段相同）。
    /// 某一层有问题时跳过那一层，原因放进 Warnings。
    /// </summary>
    public static RulesDocument Load(string? overridePath, string? uiOverlay)
    {
        var json = LowerJson(overridePath, out var warnings);
        var hasOverride = json != DefaultJson();
        if (!string.IsNullOrWhiteSpace(uiOverlay))
        {
            try
            {
                var merged = MergeJson(json, uiOverlay, "配置页的设置", warnings);
                hasOverride |= merged != json;
                json = merged;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                warnings.Add($"配置页保存的站点 / 关键词设置无法读取，已忽略：{ex.Message}");
            }
        }

        var rules = Parse(json);
        rules.HasOverride = hasOverride;
        rules.Warnings.AddRange(warnings);
        return rules;
    }

    /// <summary>内置规则叠加 rules.json 之后的 JSON（不含配置页那一层），配置页用它判断哪些是内置的。</summary>
    public static string LowerJson(string? overridePath, out List<string> warnings)
    {
        warnings = new List<string>();
        var json = DefaultJson();
        if (string.IsNullOrEmpty(overridePath) || !File.Exists(overridePath))
        {
            return json;
        }

        try
        {
            return MergeJson(json, File.ReadAllText(overridePath), "rules.json", warnings);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            warnings.Add($"rules.json 无法读取，已使用内置规则：{ex.Message}");
            return json;
        }
    }

    /// <summary>把覆盖文件叠加到基础规则上。</summary>
    public static RulesDocument Merge(string baseJson, string overrideJson)
    {
        var warnings = new List<string>();
        var json = MergeJson(baseJson, overrideJson, "rules.json", warnings);
        var merged = Parse(json);
        merged.HasOverride = json != baseJson;
        merged.Warnings.AddRange(warnings);
        return merged;
    }

    /// <summary>把一层覆盖叠加到基础 JSON 上，返回叠加后的 JSON；这一层不能用时原样返回基础 JSON，原因写进 warnings。</summary>
    public static string MergeJson(string baseJson, string overrideJson, string layer, List<string> warnings)
    {
        var baseNode = JsonNode.Parse(baseJson, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("基础规则不是 JSON 对象");
        var overlay = JsonNode.Parse(overrideJson, NodeOptions, DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException($"{layer} 不是 JSON 对象");
        var builtinVersion = (int?)baseNode["version"] ?? 0;

        var format = (int?)overlay["format"] ?? 1;
        if (format > RulesDocument.SupportedFormat)
        {
            warnings.Add($"{layer} 的格式版本 {format} 高于插件支持的 {RulesDocument.SupportedFormat}，已忽略；请升级插件");
            return baseJson;
        }

        // 0.2 及以前的 rules.json 是整份内置规则的拷贝（没有 format 字段），叠加上去会把新版内置规则改回旧的
        if (overlay["format"] is null && (int?)overlay["version"] is int legacy && legacy < builtinVersion)
        {
            warnings.Add($"{layer} 是旧版规则（版本 {legacy}），已忽略；需要覆盖时按新格式只写要改的部分");
            return baseJson;
        }

        overlay.Remove("format");
        overlay.Remove("version");
        DeepMerge(baseNode, overlay, string.Empty);
        return baseNode.ToJsonString();
    }

    public static RulesDocument Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<RulesDocument>(json, Options) ?? throw new InvalidOperationException("规则无法解析");
        if (rules.SourceOrder.Count > 0)
        {
            rules.Sources = RulesDocument.SortByOrder(rules.Sources, s => s.Name, rules.SourceOrder);
        }

        return rules;
    }

    private static string DefaultJson()
    {
        using var stream = typeof(RulesLoader).Assembly
            .GetManifestResourceStream("FireFly.Core.Rules.rules.default.json")
            ?? throw new InvalidOperationException("内置规则文件缺失");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeepMerge(JsonObject target, JsonObject overlay, string path)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            overlay.Remove(key);
            var existing = target[key];
            var childPath = path.Length == 0 ? key.ToLowerInvariant() : path + "." + key.ToLowerInvariant();
            if (existing is JsonObject to && value is JsonObject from)
            {
                DeepMerge(to, from, childPath);
            }
            else if (existing is JsonArray ta && value is JsonArray fa && MergeKeys.TryGetValue(childPath, out var mergeKey))
            {
                MergeByKey(ta, fa, mergeKey, childPath);
            }
            else
            {
                target[key] = value;
            }
        }
    }

    /// <summary>按某个字段合并而不是整体替换的数组：sources 按 name，keywords.items 按 word。</summary>
    private static readonly Dictionary<string, string> MergeKeys = new()
    {
        ["sources"] = "name",
        ["keywords.items"] = "word",
    };

    private static void MergeByKey(JsonArray target, JsonArray overlay, string keyName, string path)
    {
        foreach (var item in overlay.ToList())
        {
            overlay.Remove(item);
            var key = KeyOf(item, keyName);
            var index = key is null ? -1 : target.Select((t, i) => (t, i)).Where(x => string.Equals(KeyOf(x.t, keyName), key, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).DefaultIfEmpty(-1).First();
            if (index < 0)
            {
                target.Add(item);
            }
            else if (item is JsonObject obj)
            {
                if (target[index] is JsonObject match)
                {
                    DeepMerge(match, obj, path);
                }
                else
                {
                    target[index] = obj;
                }
            }

            // 覆盖层里只写了一个已存在的字符串（如关键词）时不用改
        }
    }

    /// <summary>数组元素的合并键：字符串元素就是它自己，对象取 keyName 字段。</summary>
    private static string? KeyOf(JsonNode? node, string keyName) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when o[keyName] is JsonValue v && v.TryGetValue<string>(out var s) => s,
        _ => null,
    };
}

/// <summary>把旧版"整体替换"的关键词列表换算成配置页那一层的 keywords（新增 / 停用 / 顺序）。</summary>
public static class KeywordMigration
{
    public static string Overlay(string? existing, IReadOnlyList<string> custom, IReadOnlyList<string> builtin)
    {
        var root = string.IsNullOrWhiteSpace(existing) ? new JsonObject() : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        var added = custom.Where(w => !builtin.Contains(w, StringComparer.OrdinalIgnoreCase)).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray();
        var disabled = builtin.Where(w => !custom.Contains(w, StringComparer.OrdinalIgnoreCase)).Select(w => (JsonNode?)JsonValue.Create(w)).ToArray();
        root["keywords"] = new JsonObject
        {
            ["items"] = new JsonArray(added),
            ["disable"] = new JsonArray(disabled),
            ["order"] = new JsonArray(custom.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
