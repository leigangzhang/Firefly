using System.Diagnostics;
using FireFly.Core.Models;
using FireFly.Core.Rules;

namespace FireFly.Core.Downloads;

public interface IDownloader
{
    string Name { get; }

    Task SubmitAsync(Release release, CancellationToken ct);

    DownloadProgress Probe(DownloadTask task);
}

/// <summary>
/// macOS 桌面迅雷。只走"正门"：
/// - 提交（M1）：<c>open "magnet:..."</c>，由系统交给迅雷；下载目录由用户事先在迅雷里设好。
/// - 进度（M2）：只看文件系统，不读写迅雷的数据库和配置。
/// 实测不通过时的回退点见《初步设计》第八节：M1 回退到 defaults write，M2 回退到只读 etm_map.db。
/// </summary>
public sealed class MacThunderDownloader : IDownloader
{
    private readonly Func<string> _downloadDir;
    private readonly Func<RulesDocument> _rules;
    private readonly Func<string, CancellationToken, Task<int>> _open;

    public MacThunderDownloader(Func<string> downloadDir, Func<RulesDocument> rules, Func<string, CancellationToken, Task<int>>? open = null)
    {
        _downloadDir = downloadDir;
        _rules = rules;
        _open = open ?? OpenWithSystemAsync;
    }

    public string Name => "迅雷（macOS）";

    public string DownloadDir => _downloadDir();

    public async Task SubmitAsync(Release release, CancellationToken ct)
    {
        if (!release.Magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不是磁力链接");
        }

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

        var expected = task.Release.Files
            .Where(f => rules.IsVideo(f.Path) && f.Size >= rules.MinVideoBytes)
            .ToList();

        return expected.Count > 0
            ? ProbeByFileList(expected, files, rules)
            : ProbeByReleaseName(task.Release, files, rules);
    }

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
        var name = release.Name.Trim();
        var related = files.Where(f => f.FullName.Contains(Path.DirectorySeparatorChar + name, StringComparison.Ordinal)).ToList();
        var hasTemp = related.Any(f => rules.Thunder.TempSuffixes.Any(s => f.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        var videos = related.Where(f => rules.IsVideo(f.Name) && f.Length >= rules.MinVideoBytes).ToList();
        var done = related.Sum(f => f.Length);
        var total = Math.Max(release.SizeBytes, done);
        var complete = !hasTemp && videos.Count > 0 && done >= release.SizeBytes;
        return new DownloadProgress(done, total, complete, complete ? videos.Select(v => v.FullName).ToList() : Array.Empty<string>());
    }

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
