using System.Text.RegularExpressions;
using FireFly.Core.Library;
using FireFly.Core.Models;

namespace FireFly.Core.Parsing;

/// <summary>
/// 给用户和迅雷看的干净名称：只有片名、季和集数范围，去掉发布站、网址、广告语。
/// 原始发布名只留在插件内部，用于匹配下载目录里的文件。
/// </summary>
public static partial class ReleaseDisplay
{
    /// <summary>电影："沙丘 (2021)"；剧集："牵牛和仙女 S01 全12集"。画质等信息另有标签展示，不放进名称。</summary>
    public static string Name(MediaQuery query, Release release)
    {
        var parts = new List<string>
        {
            query.Kind == MediaKind.Movie ? LibraryNamer.FolderName(query) : LibraryNamer.SafeName(query.Title),
        };
        if (query.Kind == MediaKind.Series)
        {
            parts.Add($"S{query.Season ?? release.Traits.Season ?? 1:00}");
            if (Episodes(release.Name) is { } episodes)
            {
                parts.Add(episodes);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>从发布名里认出集数范围："全12集"、"第08-10集"、"第05集"；认不出返回 null。</summary>
    public static string? Episodes(string name)
    {
        if (AllRegex().Match(name) is { Success: true } all)
        {
            return $"全{int.Parse(all.Groups[1].Value):00}集";
        }

        var range = CnRangeRegex().Match(name);
        if (!range.Success)
        {
            range = EnRangeRegex().Match(name);
        }

        if (range.Success)
        {
            var from = int.Parse(range.Groups[1].Value);
            var to = int.Parse(range.Groups[2].Value);
            return to > from ? $"第{from:00}-{to:00}集" : $"第{from:00}集";
        }

        if (CnSingleRegex().Match(name) is { Success: true } one)
        {
            return $"第{int.Parse(one.Groups[1].Value):00}集";
        }

        return ReleaseParser.ParseEpisode(name) is int e ? $"第{e:00}集" : null;
    }

    /// <summary>把磁力链接里的显示名（dn）换成干净名称；迅雷建任务时先显示这个名字。</summary>
    public static string WithDisplayName(string magnet, string displayName)
    {
        var q = magnet.IndexOf('?');
        if (q < 0)
        {
            return magnet;
        }

        var kept = magnet[(q + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("dn=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var xt = kept.FindIndex(p => p.StartsWith("xt=", StringComparison.OrdinalIgnoreCase));
        kept.Insert(xt + 1, "dn=" + Uri.EscapeDataString(displayName));
        return magnet[..(q + 1)] + string.Join('&', kept);
    }

    [GeneratedRegex(@"全\s*(\d{1,3})\s*集")]
    private static partial Regex AllRegex();

    [GeneratedRegex(@"第\s*(\d{1,3})\s*[-~～到至]\s*(\d{1,3})\s*集")]
    private static partial Regex CnRangeRegex();

    [GeneratedRegex(@"(?<![A-Za-z])(?:S\d{1,2})?E[Pp]?(\d{1,3})\s*-\s*(?:S\d{1,2})?E?[Pp]?(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EnRangeRegex();

    [GeneratedRegex(@"第\s*(\d{1,3})\s*集")]
    private static partial Regex CnSingleRegex();
}
