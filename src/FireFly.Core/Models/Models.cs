using System.Text.Json.Serialization;

namespace FireFly.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaKind
{
    Movie,
    Series,
}

/// <summary>要找的东西：一部电影，或一部剧的某一季。</summary>
public sealed record MediaQuery
{
    public MediaKind Kind { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? OriginalTitle { get; init; }

    /// <summary>
    /// 别名（TMDb 的中港台译名等）。资源站常用和 TMDb 主标题不同的译名，
    /// 例如 TMDb"权力的游戏前传：龙族"，资源站叫"龙之家族"或原名"House of the Dragon"。
    /// </summary>
    public IReadOnlyList<string> AlternateTitles { get; init; } = Array.Empty<string>();

    public int? Year { get; init; }

    /// <summary>剧集的季号；电影为空。</summary>
    public int? Season { get; init; }

    public string? TmdbId { get; init; }

    /// <summary>该季在 TMDb 上的集数，用来区分"整季包"和名字只写了季号的分集包；未知为空。</summary>
    public int? SeasonEpisodes { get; init; }

    /// <summary>资源类型（MediaCategories 里的 key），决定请求哪些站点、用哪些关键词；为空时按电影 / 剧集。</summary>
    public string? Category { get; init; }

    /// <summary>实际使用的类型：没指定时电影按 movie、剧集按 tv。</summary>
    public string EffectiveCategory => Category ?? (Kind == MediaKind.Movie ? MediaCategories.Movie : MediaCategories.Tv);

    /// <summary>标识一部片 / 一季的键，用作搜索缓存键。</summary>
    public string TitleKey =>
        (Kind == MediaKind.Movie
            ? $"movie:{TmdbId ?? Title}:{Year}"
            : $"series:{TmdbId ?? Title}:{Year}:s{Season ?? 1}") + ":" + EffectiveCategory;
}

/// <summary>资源类型，和规则里 discover.categories 的 key 一致。</summary>
public static class MediaCategories
{
    public const string Movie = "movie";
    public const string Tv = "tv";
    public const string Anime = "anime";
    public const string Variety = "variety";
    public const string Documentary = "documentary";

    public static readonly IReadOnlyList<string> All = new[] { Movie, Tv, Anime, Variety, Documentary };

    /// <summary>按 TMDb 类型判断：动画（16）→ 动漫，纪录（99）→ 纪录片，剧集里的真人秀 / 脱口秀（10764 / 10767）→ 综艺，其余按电影 / 剧集。</summary>
    public static string Of(MediaKind kind, IEnumerable<int> genres)
    {
        var set = genres.ToHashSet();
        if (set.Contains(16))
        {
            return Anime;
        }

        if (set.Contains(99))
        {
            return Documentary;
        }

        if (kind == MediaKind.Series && (set.Contains(10764) || set.Contains(10767)))
        {
            return Variety;
        }

        return kind == MediaKind.Movie ? Movie : Tv;
    }

    public static bool IsKnown(string? category) => category is not null && All.Contains(category);
}

public sealed record ReleaseFile(string Path, long Size);

/// <summary>一个可下载的版本（一条磁力）。</summary>
public sealed record Release
{
    public string InfoHash { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Magnet { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public IReadOnlyList<ReleaseFile> Files { get; init; } = Array.Empty<ReleaseFile>();

    public DateTimeOffset? CreatedAt { get; init; }

    public string Source { get; init; } = string.Empty;

    public ReleaseTraits Traits { get; init; } = new();

    /// <summary>提交下载时生成的干净名称（见 ReleaseDisplay），展示用；Name 保留原始发布名供匹配文件。</summary>
    public string? DisplayName { get; init; }

    /// <summary>标题命中的第一个优选关键词在优先级列表里的位置（0 最优先）；没命中为空。排序时填写。</summary>
    [JsonIgnore]
    public int? KeywordRank { get; init; }
}

/// <summary>从标题里解析出的展示字段。只用于展示、过滤和排序，不打分。</summary>
public sealed record ReleaseTraits
{
    /// <summary>纵向像素：2160 / 1080 / 720 / 480；未识别为 0。</summary>
    public int Resolution { get; init; }

    public string? Codec { get; init; }

    public string? Hdr { get; init; }

    public string? MediaSource { get; init; }

    public string? Audio { get; init; }

    public string? Subtitles { get; init; }

    public int? Year { get; init; }

    /// <summary>标题里标明的季号；没有标明为空。</summary>
    public int? Season { get; init; }

    public string? Publisher { get; init; }

    /// <summary>只有俄语、乌克兰语、西语等外语音轨或字幕、没有中英文的版本，排序时靠后。</summary>
    public bool ForeignOnly { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState
{
    Submitted,
    Downloading,
    Stalled,
    Archiving,
    Done,
    Failed,
}

public sealed class DownloadTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public MediaQuery Query { get; set; } = new();

    public Release Release { get; set; } = new();

    public TaskState State { get; set; } = TaskState.Submitted;

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public double SpeedBps { get; set; }

    public DateTimeOffset? LastProgressAt { get; set; }

    /// <summary>连续"已完成且大小不变"的检查次数，达到阈值才进入归档。</summary>
    public int StableChecks { get; set; }

    public List<string> ArchivedPaths { get; set; } = new();

    public string? Message { get; set; }

    [JsonIgnore]
    public bool IsActive => State is TaskState.Submitted or TaskState.Downloading or TaskState.Stalled or TaskState.Archiving;
}

public sealed record DownloadProgress(long BytesDone, long BytesTotal, bool Complete, IReadOnlyList<string> CompletedFiles);

public sealed record LibraryPaths(string? MovieRoot, string? SeriesRoot);
