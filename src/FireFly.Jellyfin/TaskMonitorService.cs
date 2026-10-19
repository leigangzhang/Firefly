using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FireFly.Jellyfin;

/// <summary>随 Jellyfin 启停的后台轮询：每 10 秒推进一次下载任务，并按需阻止 Mac 睡眠。</summary>
public sealed class TaskMonitorService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private readonly FireFlyRuntime _runtime;
    private readonly ILogger<TaskMonitorService> _logger;

    public TaskMonitorService(FireFlyRuntime runtime, ILogger<TaskMonitorService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                _runtime.Watcher.Tick();
                _runtime.SleepGuard.Update(_runtime.Watcher.HasActiveDownloads);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FireFly 下载监控本轮出错");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _runtime.SleepGuard.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
