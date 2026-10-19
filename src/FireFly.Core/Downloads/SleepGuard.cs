using System.Diagnostics;

namespace FireFly.Core.Downloads;

/// <summary>有任务在下载时阻止 Mac 睡眠：起一个 caffeinate -i -w &lt;本进程&gt;，没有任务时结束它。</summary>
public sealed class SleepGuard : IDisposable
{
    private Process? _caffeinate;

    public void Update(bool downloading)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        if (downloading && _caffeinate is not { HasExited: false })
        {
            try
            {
                _caffeinate = Process.Start(new ProcessStartInfo("caffeinate", new[] { "-i", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                {
                    UseShellExecute = false,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _caffeinate = null;
            }
        }
        else if (!downloading)
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        if (_caffeinate is { HasExited: false })
        {
            _caffeinate.Kill();
        }

        _caffeinate?.Dispose();
        _caffeinate = null;
    }
}
