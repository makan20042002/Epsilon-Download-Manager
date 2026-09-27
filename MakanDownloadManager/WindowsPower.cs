using System.Diagnostics;
using System.Windows;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>What a finished queue may do to Windows. Turning the computer off always shows a cancellable countdown first.</summary>
public sealed class WindowsPower : IPowerActions
{
    public void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception) { /* missing file / no handler: nothing to do */ }
    }

    public void ExitApplication() =>
        Application.Current.Dispatcher.BeginInvoke(() => (Application.Current.MainWindow as MainWindow)?.ExitFromScheduler());

    public async Task<bool> PowerOffAsync(PowerAction action, bool force)
    {
        var proceed = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new PowerCountdownWindow(action, 60);
            window.ShowDialog();
            return window.Proceed;
        });
        if (!proceed) return false;

        var f = force ? " /f" : "";
        var psi = action switch
        {
            PowerAction.Restart => new ProcessStartInfo("shutdown", "/r /t 0" + f),
            PowerAction.Hibernate => new ProcessStartInfo("shutdown", "/h"),
            PowerAction.Sleep => new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
            _ => new ProcessStartInfo("shutdown", "/s /t 0" + f)
        };
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        try { Process.Start(psi); return true; } catch (Exception) { return false; }
    }
}
