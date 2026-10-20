using FireFly.Core.Downloads;
using FireFly.Core.Models;
using FireFly.Core.Parsing;
using FireFly.Core.Storage;

namespace FireFly.Core;

/// <summary>提交下载的入口：交给下载器 → 建任务。</summary>
public sealed class FireFlyApp
{
    private readonly JsonStore<TaskList> _tasks;
    private readonly IDownloader _downloader;

    public FireFlyApp(JsonStore<TaskList> tasks, IDownloader downloader)
    {
        _tasks = tasks;
        _downloader = downloader;
    }

    public async Task<DownloadTask> SubmitAsync(MediaQuery query, Release release, CancellationToken ct)
    {
        var existing = _tasks.Read(l => l.Tasks.FirstOrDefault(t => t.IsActive && string.Equals(t.Release.InfoHash, release.InfoHash, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            return existing;
        }

        // 交给迅雷的磁力只带干净的显示名，不带发布站信息
        var displayName = ReleaseDisplay.Name(query, release);
        release = release with { DisplayName = displayName, Magnet = ReleaseDisplay.WithDisplayName(release.Magnet, displayName) };
        await _downloader.SubmitAsync(release, ct).ConfigureAwait(false);

        var task = new DownloadTask
        {
            Query = query,
            Release = release,
            BytesTotal = release.Files.Count > 0 ? release.Files.Sum(f => f.Size) : release.SizeBytes,
            Message = "已交给迅雷，等待开始下载",
        };
        _tasks.Update(l =>
        {
            l.Tasks.Insert(0, task);
            return 0;
        });
        return task;
    }

    public IReadOnlyList<DownloadTask> ListTasks() => _tasks.Read(l => l.Tasks.ToList());

    public bool Dismiss(string id) =>
        _tasks.Update(l => l.Tasks.RemoveAll(t => t.Id == id && t.State != TaskState.Archiving) > 0);
}
