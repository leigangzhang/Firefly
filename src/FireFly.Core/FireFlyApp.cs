using FireFly.Core.Downloads;
using FireFly.Core.Licensing;
using FireFly.Core.Models;
using FireFly.Core.Storage;

namespace FireFly.Core;

public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException(QuotaStatus status)
        : base($"本月免费额度（{status.FreeLimit} 部）已用完")
    {
        Status = status;
    }

    public QuotaStatus Status { get; }
}

/// <summary>提交下载的入口：额度检查 → 交给下载器 → 记额度 → 建任务。</summary>
public sealed class FireFlyApp
{
    private readonly JsonStore<TaskList> _tasks;
    private readonly IDownloader _downloader;
    private readonly QuotaLedger _quota;

    public FireFlyApp(JsonStore<TaskList> tasks, IDownloader downloader, QuotaLedger quota)
    {
        _tasks = tasks;
        _downloader = downloader;
        _quota = quota;
    }

    public async Task<DownloadTask> SubmitAsync(MediaQuery query, Release release, CancellationToken ct)
    {
        var existing = _tasks.Read(l => l.Tasks.FirstOrDefault(t => t.IsActive && string.Equals(t.Release.InfoHash, release.InfoHash, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null)
        {
            return existing;
        }

        if (!_quota.CanSubmit(query.QuotaKey))
        {
            throw new QuotaExceededException(_quota.Status());
        }

        await _downloader.SubmitAsync(release, ct).ConfigureAwait(false);
        _quota.Record(query.QuotaKey);

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
