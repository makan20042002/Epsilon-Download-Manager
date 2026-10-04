using System.Runtime.InteropServices;
using System.Windows.Threading;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>Keeps Windows awake while at least one file is actively downloading. The display may still turn off.</summary>
public sealed class DownloadSleepGuard : IDisposable
{
    [Flags]
    enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern ExecutionState SetThreadExecutionState(ExecutionState state);

    readonly Func<bool> _enabled;
    readonly Func<IReadOnlyList<DownloadItem>> _items;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _holding;

    public DownloadSleepGuard(Func<bool> enabled, Func<IReadOnlyList<DownloadItem>> items)
    {
        _enabled = enabled;
        _items = items;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Refresh()
    {
        var shouldHold = _enabled() && _items().Any(x => x.Status == nameof(DownloadStatus.Downloading));
        if (shouldHold == _holding) return;
        var result = SetThreadExecutionState(shouldHold
            ? ExecutionState.Continuous | ExecutionState.SystemRequired
            : ExecutionState.Continuous);
        if (result != 0) _holding = shouldHold;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_holding) SetThreadExecutionState(ExecutionState.Continuous);
        _holding = false;
    }
}
