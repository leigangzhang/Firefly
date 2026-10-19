using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Rules;

namespace FireFly.Core.Library;

/// <summary>宿主（Jellyfin / 以后的 Emby）需要提供的能力。</summary>
public interface IMediaHost
{
    LibraryPaths GetLibraryPaths();

    void RequestRefresh();

    bool Contains(MediaQuery query);
}

/// <summary>按 Jellyfin / Emby / Plex 通用的命名规范生成落点。</summary>
public static class LibraryNamer
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '/', '\\', '?', '*', '"', '<', '>', '|' }).Distinct().ToArray();

    public static string SafeName(string name)
    {
        var cleaned = new string(name.Select(c => Invalid.Contains(c) ? ' ' : c).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Unknown" : cleaned;
    }

    public static string FolderName(MediaQuery q) =>
        q.Year is int y ? $"{SafeName(q.Title)} ({y})" : SafeName(q.Title);

    /// <summary>电影：&lt;库&gt;/片名 (年份)/片名 (年份).ext</summary>
    public static string MovieTarget(string root, MediaQuery q, string sourceFile)
    {
        var folder = FolderName(q);
        return Path.Combine(root, folder, folder + Path.GetExtension(sourceFile).ToLowerInvariant());
    }

    /// <summary>剧集：&lt;库&gt;/剧名 (年份)/Season NN/剧名 SNNEMM.ext；认不出集号时保留原文件名。</summary>
    public static string EpisodeTarget(string root, MediaQuery q, string sourceFile)
    {
        var season = q.Season ?? 1;
        var dir = Path.Combine(root, FolderName(q), $"Season {season:00}");
        var episode = ReleaseParser.ParseEpisode(Path.GetFileNameWithoutExtension(sourceFile));
        var fileName = episode is int e
            ? $"{SafeName(q.Title)} S{season:00}E{e:00}{Path.GetExtension(sourceFile).ToLowerInvariant()}"
            : Path.GetFileName(sourceFile);
        return Path.Combine(dir, fileName);
    }
}

/// <summary>下载完成后：规范命名 → 移入媒体库（同盘是原子改名）→ 刷新库。</summary>
public sealed class Archiver
{
    private readonly IMediaHost _host;
    private readonly Func<RulesDocument> _rules;

    public Archiver(IMediaHost host, Func<RulesDocument> rules)
    {
        _host = host;
        _rules = rules;
    }

    /// <param name="videoFiles">已经下载完成的视频文件绝对路径。</param>
    public IReadOnlyList<string> Archive(MediaQuery query, IReadOnlyList<string> videoFiles)
    {
        var paths = _host.GetLibraryPaths();
        var root = query.Kind == MediaKind.Movie ? paths.MovieRoot : paths.SeriesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(query.Kind == MediaKind.Movie ? "没有配置电影库目录" : "没有配置剧集库目录");
        }

        var rules = _rules();
        var moved = new List<string>();

        // 电影只取最大的那个视频（其余多为花絮/样片）；剧集全部移动
        var sources = query.Kind == MediaKind.Movie
            ? videoFiles.OrderByDescending(f => new FileInfo(f).Length).Take(1).ToList()
            : videoFiles.ToList();

        foreach (var source in sources)
        {
            var target = query.Kind == MediaKind.Movie
                ? LibraryNamer.MovieTarget(root, query, source)
                : LibraryNamer.EpisodeTarget(root, query, source);
            target = Unique(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            moved.Add(target);

            foreach (var sub in SidecarSubtitles(source, rules))
            {
                var subTarget = Unique(Path.ChangeExtension(target, null) + SubtitleSuffix(source, sub));
                File.Move(sub, subTarget);
                moved.Add(subTarget);
            }
        }

        _host.RequestRefresh();
        return moved;
    }

    /// <summary>与视频同目录、同名前缀的字幕：movie.srt / movie.chs.ass。</summary>
    internal static IEnumerable<string> SidecarSubtitles(string video, RulesDocument rules)
    {
        var dir = Path.GetDirectoryName(video);
        if (dir is null || !Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        var stem = Path.GetFileNameWithoutExtension(video);
        return Directory.EnumerateFiles(dir)
            .Where(f => rules.IsSubtitle(f) && Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>保留字幕文件名里视频名之后的部分，例如 ".chs.ass"。</summary>
    internal static string SubtitleSuffix(string video, string subtitle)
    {
        var stem = Path.GetFileNameWithoutExtension(video);
        return Path.GetFileName(subtitle)[stem.Length..];
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} - {i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
