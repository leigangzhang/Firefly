using System.Text.Json;

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
            return custom is not null && custom.Version >= builtin.Version && custom.Sources.Count > 0 ? custom : builtin;
        }
        catch (JsonException)
        {
            return builtin;
        }
    }

    public static RulesDocument Parse(string json) =>
        JsonSerializer.Deserialize<RulesDocument>(json, Options) ?? throw new InvalidOperationException("规则无法解析");
}
