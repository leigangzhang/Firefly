using System.Diagnostics;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Downloads;

public interface IDownloader
{
    string Name { get; }

    string DownloadDir { get; }

    Task SubmitAsync(Release release, CancellationToken ct);

    DownloadProgress Probe(DownloadTask task);
}

/// <summary>
/// macOS 桌面迅雷。提交前把迅雷的保存目录改成插件配置的下载目录，再交给系统打开磁力链接。
/// 进度只读文件系统；不读写迅雷任务数据库。
/// </summary>
public sealed class MacThunderDownloader : IDownloader
{
    private const string ThunderPreferenceDomain = "com.xunlei.Thunder";

    private readonly Func<string> _downloadDir;
    private readonly Func<RulesDocument> _rules;
    private readonly Func<string, CancellationToken, Task<int>> _open;
    private readonly Func<string, CancellationToken, Task> _setSavePath;

    public MacThunderDownloader(
        Func<string> downloadDir,
        Func<RulesDocument> rules,
        Func<string, CancellationToken, Task<int>>? open = null,
        Func<string, CancellationToken, Task>? setSavePath = null)
    {
        _downloadDir = downloadDir;
        _rules = rules;
        _open = open ?? OpenWithSystemAsync;
        _setSavePath = setSavePath ?? SetThunderSavePathAsync;
    }

    public string Name => "迅雷（macOS）";

    public string DownloadDir => _downloadDir();

    public async Task SubmitAsync(Release release, CancellationToken ct)
    {
        if (!release.Magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不是磁力链接");
        }

        var savePath = PrepareDownloadDirectory(_downloadDir());
        await _setSavePath(savePath, ct).ConfigureAwait(false);

        var code = await _open(release.Magnet, ct).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"无法把磁力交给迅雷（open 退出码 {code}），请确认已安装迅雷并设为磁力链接的默认打开方式");
        }
    }

    public DownloadProgress Probe(DownloadTask task)
    {
        var dir = _downloadDir();
        if (!Directory.Exists(dir))
        {
            return new DownloadProgress(0, ExpectedTotal(task.Release), false, Array.Empty<string>());
        }

        var rules = _rules();
        var since = task.SubmittedAt.AddMinutes(-5);
        var files = SafeEnumerate(dir)
            .Select(p => new FileInfo(p))
            .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime || f.CreationTimeUtc >= since.UtcDateTime)
            .ToList();

        var expected = Videos(task.Release.Files, rules);
        if (expected.Count > 0)
        {
            return ProbeByFileList(expected, files, rules);
        }

        // 资源站没给文件清单时，先按种子哈希找迅雷的任务文件夹，用里面的种子当清单
        if (ThunderTaskFolder.Find(dir, task.Release.InfoHash) is { } folder
            && Videos(ThunderTaskFolder.ReadTorrentFiles(folder), rules) is { Count: > 0 } fromTorrent)
        {
            var inFolder = SafeEnumerate(folder).Select(p => new FileInfo(p)).ToList();
            return ProbeByFileList(fromTorrent, inFolder, rules);
        }

        return ProbeByReleaseName(task.Release, files, rules);
    }

    private static List<ReleaseFile> Videos(IEnumerable<ReleaseFile> files, RulesDocument rules) =>
        files.Where(f => rules.IsVideo(f.Path) && f.Size >= rules.MinVideoBytes).ToList();

    /// <summary>有文件清单：按文件名逐个找。正式文件名存在、大小到位、旁边没有临时文件，才算这一集完成。</summary>
    internal static DownloadProgress ProbeByFileList(IReadOnlyList<ReleaseFile> expected, IReadOnlyList<FileInfo> files, RulesDocument rules)
    {
        var byName = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.LastWriteTimeUtc).First(), StringComparer.OrdinalIgnoreCase);
        long done = 0;
        var total = expected.Sum(f => f.Size);
        var completed = new List<string>();

        foreach (var want in expected)
        {
            var name = Path.GetFileName(want.Path);
            var temp = rules.Thunder.TempSuffixes
                .Select(s => byName.GetValueOrDefault(name + s))
                .FirstOrDefault(f => f is not null);

            if (byName.TryGetValue(name, out var final) && temp is null)
            {
                done += Math.Min(final.Length, want.Size);
                if (final.Length >= want.Size)
                {
                    completed.Add(final.FullName);
                }
            }
            else if (temp is not null || final is not null)
            {
                // 迅雷可能预分配空间，这里的大小只是近似进度（M2 实测时确认）
                done += Math.Min((temp ?? final)!.Length, want.Size);
            }
        }

        return new DownloadProgress(done, total, completed.Count == expected.Count, completed);
    }

    /// <summary>没有文件清单：找与种子同名的文件夹或文件，里面的视频文件就是目标。</summary>
    internal static DownloadProgress ProbeByReleaseName(Release release, IReadOnlyList<FileInfo> files, RulesDocument rules)
    {
        // 迅雷可能用种子里的原名，也可能用磁力里的显示名建目录，两个都认。
        // 比较时去掉空白：网页站点抓到的标题常在标签边界多出空格，迅雷的目录名里没有
        var names = new[] { release.Name, release.DisplayName }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => Path.DirectorySeparatorChar + StripWhitespace(n!))
            .ToList();
        var related = files.Where(f => names.Any(n => StripWhitespace(f.FullName).Contains(n, StringComparison.Ordinal))).ToList();
        var hasTemp = related.Any(f => rules.Thunder.TempSuffixes.Any(s => f.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        var videos = related.Where(f => rules.IsVideo(f.Name) && f.Length >= rules.MinVideoBytes).ToList();
        var done = related.Sum(f => f.Length);
        var total = Math.Max(release.SizeBytes, done);
        var complete = !hasTemp && videos.Count > 0 && done >= release.SizeBytes;
        return new DownloadProgress(done, total, complete, complete ? videos.Select(v => v.FullName).ToList() : Array.Empty<string>());
    }

    private static string StripWhitespace(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static long ExpectedTotal(Release r) => r.Files.Count > 0 ? r.Files.Sum(f => f.Size) : r.SizeBytes;

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>迅雷只接受带结尾分隔符的目录；先创建目录，路径不可写时在提交前明确失败。</summary>
    private static string PrepareDownloadDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("未配置迅雷下载目录");
        }

        var full = Path.GetFullPath(path);
        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法创建迅雷下载目录：{full}", ex);
        }

        return full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static async Task SetThunderSavePathAsync(string path, CancellationToken ct)
    {
        await RunDefaultsAsync("specifySavePath", path, ct).ConfigureAwait(false);
        await RunDefaultsAsync("LastSavePath", path, ct).ConfigureAwait(false);
    }

    private static async Task RunDefaultsAsync(string key, string value, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo("defaults", new[] { "write", ThunderPreferenceDomain, key, "-string", value })
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("无法启动 defaults");
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"无法设置迅雷下载目录（defaults {key} 退出码 {process.ExitCode}）：{stderr.Trim()}");
        }
    }

    private static async Task<int> OpenWithSystemAsync(string url, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo("open", new[] { url })
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("无法启动 open");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}
