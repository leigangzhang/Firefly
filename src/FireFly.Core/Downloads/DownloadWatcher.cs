using FireFly.Core.Library;
using FireFly.Core.Models;
using FireFly.Core.Storage;

namespace FireFly.Core.Downloads;

public sealed class WatcherOptions
{
    /// <summary>提交后多久还没看到任何文件，就标记为停滞。</summary>
    public TimeSpan NoFileTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>下载中多久没有增长，就标记为停滞。</summary>
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>"已完成且大小不变"需要连续几次检查，才进入归档。</summary>
    public int StableChecksRequired { get; init; } = 2;
}

/// <summary>任务状态机：每次 Tick 检查所有进行中的任务，推进状态，完成的交给归档。</summary>
public sealed class DownloadWatcher
{
    private readonly JsonStore<TaskList> _store;
    private readonly IDownloader _downloader;
    private readonly Archiver _archiver;
    private readonly WatcherOptions _options;
    private readonly Func<DateTimeOffset> _now;

    public DownloadWatcher(JsonStore<TaskList> store, IDownloader downloader, Archiver archiver, WatcherOptions? options = null, Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _downloader = downloader;
        _archiver = archiver;
        _options = options ?? new WatcherOptions();
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool HasActiveDownloads => _store.Read(l => l.Tasks.Any(t => t.State is TaskState.Submitted or TaskState.Downloading));

    public void Tick()
    {
        var active = _store.Read(l => l.Tasks.Where(t => t.IsActive && t.State != TaskState.Archiving).Select(t => t.Id).ToList());
        foreach (var id in active)
        {
            Step(id);
        }
    }

    private void Step(string id)
    {
        var task = _store.Read(l => l.Tasks.FirstOrDefault(t => t.Id == id));
        if (task is null)
        {
            return;
        }

        DownloadProgress progress;
        try
        {
            progress = _downloader.Probe(task);
        }
        catch (IOException ex)
        {
            Save(id, t => t.Message = "读取下载目录失败：" + ex.Message);
            return;
        }

        var now = _now();
        var readyToArchive = false;
        Save(id, t =>
        {
            var previous = t.BytesDone;
            if (progress.BytesDone > previous)
            {
                var seconds = t.LastProgressAt is { } last ? Math.Max(1, (now - last).TotalSeconds) : 0;
                t.SpeedBps = seconds > 0 ? (progress.BytesDone - previous) / seconds : 0;
                t.LastProgressAt = now;
                t.BytesDone = progress.BytesDone;
                t.StableChecks = 0;
                if (t.State is TaskState.Submitted or TaskState.Stalled)
                {
                    t.State = TaskState.Downloading;
                    t.Message = null;
                }
            }
            else
            {
                t.SpeedBps = 0;
            }

            t.BytesTotal = Math.Max(progress.BytesTotal, t.BytesTotal);

            if (progress.Complete)
            {
                t.StableChecks = progress.BytesDone == previous ? t.StableChecks + 1 : 1;
                if (t.StableChecks >= _options.StableChecksRequired)
                {
                    t.State = TaskState.Archiving;
                    t.Message = "正在移入媒体库";
                    readyToArchive = true;
                }
                else if (t.State != TaskState.Downloading)
                {
                    t.State = TaskState.Downloading;
                }

                return;
            }

            if (t.State == TaskState.Submitted && progress.BytesDone == 0 && now - t.SubmittedAt > _options.NoFileTimeout)
            {
                t.State = TaskState.Stalled;
                t.Message = "迅雷还没有开始下载：请确认迅雷已接收任务、下载目录与 FireFly 设置一致；也可以换一个版本";
            }
            else if (t.State == TaskState.Downloading && t.LastProgressAt is { } lp && now - lp > _options.NoProgressTimeout)
            {
                t.State = TaskState.Stalled;
                t.Message = "下载长时间没有进展（可能缺少做种或被迅雷拦截），可以继续等待或换一个版本";
            }
        });

        if (readyToArchive)
        {
            Archive(id, progress.CompletedFiles);
        }
    }

    private void Archive(string id, IReadOnlyList<string> files)
    {
        var task = _store.Read(l => l.Tasks.First(t => t.Id == id));
        try
        {
            var moved = _archiver.Archive(task.Query, files);
            Save(id, t =>
            {
                t.State = TaskState.Done;
                t.ArchivedPaths = moved.ToList();
                t.Message = $"已入库 {moved.Count} 个文件";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Save(id, t =>
            {
                t.State = TaskState.Failed;
                t.Message = "归档失败：" + ex.Message;
            });
        }
    }

    private void Save(string id, Action<DownloadTask> change) =>
        _store.Update(l =>
        {
            var t = l.Tasks.FirstOrDefault(x => x.Id == id);
            if (t is not null)
            {
                change(t);
                t.UpdatedAt = _now();
            }

            return 0;
        });
}
