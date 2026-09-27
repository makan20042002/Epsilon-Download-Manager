namespace MakanDownloadManager.Services;

/// <summary>
/// An opt-in way to give Makan's downloads more of the connection: pauses a short list of Windows services that
/// commonly compete for bandwidth in the background (Windows Update, the Background Intelligent Transfer Service
/// other apps use for their own updates, and Delivery Optimization) for as long as something is actively downloading,
/// then puts back only the ones it actually stopped. This needs administrator rights (stopping a system service
/// always does) - Makan itself installs without them, so this reports a clear reason instead of silently doing
/// nothing when it can't. It also cannot force other running programs to actually stop transferring data mid-flight;
/// pausing the service only stops new activity from that particular source.
/// <para>Both checks below are injectable so the pause/resume bookkeeping - the part actually worth testing - can be
/// exercised without a real Windows service or real administrator rights.</para>
/// </summary>
public sealed class NetworkBoost
{
    public static readonly string[] ServiceNames = { "wuauserv", "bits", "DoSvc" };

    readonly Func<string, bool> _runCommand;
    readonly Func<bool> _isElevated;
    readonly HashSet<string> _pausedByUs = new();

    public bool Active { get; private set; }
    public string? LastError { get; private set; }

    public NetworkBoost(Func<string, bool>? runCommand = null, Func<bool>? isElevated = null)
    {
        _runCommand = runCommand ?? RunScReal;
        _isElevated = isElevated ?? IsElevatedReal;
    }

    /// <summary>Stops whichever of the listed services are running. Safe to call repeatedly - already active is a
    /// no-op, so a caller does not need to track "did I already pause this" itself.</summary>
    public bool Pause()
    {
        if (Active) return LastError == null;
        LastError = null;
        if (!_isElevated())
        {
            LastError = "This needs administrator rights - right-click Makan.exe and choose \"Run as administrator\", or turn this off.";
            return false;
        }
        var allOk = true;
        foreach (var name in ServiceNames)
        {
            if (_runCommand($"stop {name}")) _pausedByUs.Add(name);
            else allOk = false;
        }
        Active = true;
        if (!allOk) LastError = "Some services could not be paused (they may already be stopped, disabled, or Windows refused).";
        return allOk;
    }

    /// <summary>Restarts only the services this instance actually stopped - one already stopped for some other
    /// reason before Pause() ran is left alone.</summary>
    public void Resume()
    {
        if (!Active) return;
        foreach (var name in _pausedByUs) _runCommand($"start {name}");
        _pausedByUs.Clear();
        Active = false;
    }

    static bool IsElevatedReal()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }   // not Windows, or the check itself was refused: assume the safer "no"
    }

    static bool RunScReal(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
}
