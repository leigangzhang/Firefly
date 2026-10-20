using System.Text.Json;
using FireFly.Core.Models;

namespace FireFly.Core.Rules;

/// <summary>规则文件：搜索源、发布者关键词、迅雷临时后缀等会随上游变化的东西都在这里。</summary>
public sealed class RulesDocument
{
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

    public bool IsVideo(string path) =>
        VideoExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool IsSubtitle(string path) =>
        SubtitleExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public sealed class SourceRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>用哪段代码解析响应，不是站点名。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>同一个源的多个镜像地址，按顺序尝试，上次成功的排到最前。</summary>
    public List<string> Endpoints { get; set; } = new();

    public int PageSize { get; set; } = 10;

    public int MaxPages { get; set; } = 1;

    public bool Enabled { get; set; } = true;
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

public static class RulesLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>内置默认规则（编进 DLL）。</summary>
    public static RulesDocument LoadDefault()
    {
        using var stream = typeof(RulesLoader).Assembly
            .GetManifestResourceStream("FireFly.Core.Rules.rules.default.json")
            ?? throw new InvalidOperationException("内置规则文件缺失");
        return JsonSerializer.Deserialize<RulesDocument>(stream, Options)
            ?? throw new InvalidOperationException("内置规则文件无法解析");
    }

    /// <summary>
    /// 本地覆盖文件存在且版本不低于内置版本时使用它，否则用内置默认。
    /// 远程下发（签名校验）在 Phase 2 接入，届时写的也是这个覆盖文件。
    /// </summary>
    public static RulesDocument Load(string? overridePath)
    {
        var builtin = LoadDefault();
        if (string.IsNullOrEmpty(overridePath) || !File.Exists(overridePath))
        {
            return builtin;
        }

        try
        {
            var custom = JsonSerializer.Deserialize<RulesDocument>(File.ReadAllText(overridePath), Options);
            if (custom is null || custom.Version < builtin.Version || custom.Sources.Count == 0)
            {
                return builtin;
            }

            if (custom.Discover.Categories.Count == 0)
            {
                custom.Discover = builtin.Discover;
            }

            return custom;
        }
        catch (JsonException)
        {
            return builtin;
        }
    }

    public static RulesDocument Parse(string json) =>
        JsonSerializer.Deserialize<RulesDocument>(json, Options) ?? throw new InvalidOperationException("规则无法解析");
}
