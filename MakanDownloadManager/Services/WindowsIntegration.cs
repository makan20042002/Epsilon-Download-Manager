using Microsoft.Win32;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace MakanDownloadManager.Services;

/// <summary>Windows-only bits of the Options window: start with Windows, and whether each browser can reach Makan.</summary>
public static class WindowsIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "MakanDownloadManager";
    const string HostName = "com.makan.downloadmanager";

    public static bool IsPackaged
    {
        get
        {
            var length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            return result != AppModelErrorNoPackage;
        }
    }

    public static bool LaunchOnStartup
    {
        get { try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunName) != null; } catch (Exception) { return false; } }
    }

    /// <summary>Adds or removes the per-user "run at sign-in" entry. Makan then starts quietly in the tray.</summary>
    public static bool SetLaunchOnStartup(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(RunName, $"\"{exePath}\" --background");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Reads the Store/MSIX startup task, or the normal Run entry for an unpackaged install.</summary>
    public static async Task<bool> GetLaunchOnStartupAsync()
    {
        if (!IsPackaged) return LaunchOnStartup;
        try
        {
            var task = await StartupTask.GetAsync("EpsilonDownloadManagerStartup");
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }

    /// <summary>Changes the packaged startup task when installed from the Store.</summary>
    public static async Task<bool> SetLaunchOnStartupAsync(bool enabled, string exePath)
    {
        if (!IsPackaged) return SetLaunchOnStartup(enabled, exePath);
        try
        {
            var task = await StartupTask.GetAsync("EpsilonDownloadManagerStartup");
            if (!enabled) { task.Disable(); return true; }
            var state = await task.RequestEnableAsync();
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }

    const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    public sealed record BrowserStatus(string Name, bool Registered);

    /// <summary>Which browsers have Makan's native-messaging host registered (what install-browser-integration.ps1 sets up).</summary>
    public static IReadOnlyList<BrowserStatus> Browsers()
    {
        (string Name, string Key)[] keys =
        {
            ("Google Chrome", $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}"),
            ("Microsoft Edge", $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"),
            ("Brave", $@"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\{HostName}"),
            ("Mozilla Firefox", $@"Software\Mozilla\NativeMessagingHosts\{HostName}")
        };
        var list = new List<BrowserStatus>();
        foreach (var (name, key) in keys)
        {
            var ok = false;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                var manifest = k?.GetValue(null) as string;
                ok = !string.IsNullOrWhiteSpace(manifest) && File.Exists(manifest);
            }
            catch (Exception) { }
            list.Add(new BrowserStatus(name, ok));
        }
        return list;
    }
}
