using System.Text.RegularExpressions;
using FireFly.Core.Library;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Parsing;

/// <summary>
/// 给用户和迅雷看的干净名称：只有片名、季和集数范围，去掉发布站、网址、广告语。
/// 原始发布名只留在插件内部，用于匹配下载目录里的文件。
/// </summary>
/// <summary>剧集版本覆盖的集数。FullSeason 为整季包；否则是单集或分集包，Episodes 为包含的集号。</summary>
public sealed record EpisodeCoverage(bool FullSeason, int? From, int? To, IReadOnlyList<int> Episodes);

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
            var cov = Coverage(release, null, query.SeasonEpisodes);
            var episodes = cov.FullSeason
                ? Episodes(release.Name)
                : cov.From is int from ? (cov.To is int to && to > from ? $"第{from:00}-{to:00}集" : $"第{from:00}集") : null;
            if (episodes is not null)
            {
                parts.Add(episodes);
            }
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 判断一个剧集版本是整季包还是单集 / 分集包，以及覆盖哪几集。
    /// 依据依次是：发布名里的"全N集"、集数范围、单集标记；都没有时看文件清单里有几个视频。
    /// seasonEpisodes 是该季在 TMDb 上的集数：名字只写了季号（如 Show.S01）的包，文件里的集数不够这么多，就算分集包。
    /// </summary>
    public static EpisodeCoverage Coverage(Release release, RulesDocument? rules = null, int? seasonEpisodes = null)
    {
        bool IsVideo(ReleaseFile f) => rules is null
            ? f.Size >= DefaultMinVideoBytes
            : rules.IsVideo(f.Path) && f.Size >= rules.MinVideoBytes;
        var videos = release.Files.Where(IsVideo).ToList();
        var fileEpisodes = videos
            .Select(f => ReleaseParser.ParseEpisode(Path.GetFileName(f.Path)))
            .OfType<int>()
            .Distinct()
            .Order()
            .ToList();
        var name = release.Name;

        if (AllRegex().IsMatch(name))
        {
            return new EpisodeCoverage(true, fileEpisodes.FirstOrDefault() is > 0 and var a ? a : null, fileEpisodes.Count > 0 ? fileEpisodes[^1] : null, fileEpisodes);
        }

        var range = CnRangeRegex().Match(name);
        if (!range.Success)
        {
            range = EnRangeRegex().Match(name);
        }

        if (range.Success)
        {
            var from = int.Parse(range.Groups[1].Value);
            var to = Math.Max(from, int.Parse(range.Groups[2].Value));
            return Partial(fileEpisodes.Count > 0 ? fileEpisodes : Enumerable.Range(from, to - from + 1).ToList());
        }

        var single = CnSingleRegex().Match(name) is { Success: true } one ? int.Parse(one.Groups[1].Value) : ReleaseParser.ParseEpisode(name);
        if (single is int e && fileEpisodes.Count <= 1)
        {
            return Partial(new List<int> { e });
        }

        // 名称里没有集数：单个视频且能从文件名认出集数的是单集
        if (videos.Count == 1 && fileEpisodes.Count == 1)
        {
            return Partial(fileEpisodes);
        }

        // 每个视频都认出了集数，且不从第 1 集开始或少于该季总集数：是只写了季号的分集包（如 Show.S01 只含 E01-E03）
        if (fileEpisodes.Count > 0 && fileEpisodes.Count == videos.Count && (fileEpisodes[0] > 1 || fileEpisodes.Count < seasonEpisodes))
        {
            return Partial(fileEpisodes);
        }

        return new EpisodeCoverage(true, fileEpisodes.Count > 0 ? fileEpisodes[0] : null, fileEpisodes.Count > 0 ? fileEpisodes[^1] : null, fileEpisodes);
    }

    private const long DefaultMinVideoBytes = 50L * 1024 * 1024;

    private static EpisodeCoverage Partial(List<int> episodes) =>
        new(false, episodes.Count > 0 ? episodes.Min() : null, episodes.Count > 0 ? episodes.Max() : null, episodes);

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
