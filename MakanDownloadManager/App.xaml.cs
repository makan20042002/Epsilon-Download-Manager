using System.IO.Pipes;
using System.Text;
using System.Windows;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;

namespace MakanDownloadManager;

public partial class App : Application
{
    public static DownloadDb Db { get; private set; } = null!;
    public static DownloadManager Manager { get; private set; } = null!;
    public static NativeBridge Bridge { get; private set; } = null!;
    public static QueueService Queues { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static RemoteStatusServer Remote { get; private set; } = null!;
    public static DownloadBasket Basket { get; } = new();
    public static DownloadRuleEngine Rules { get; } = new();
    public static SmartDownloadController SmartDownloads { get; private set; } = null!;
    public static NetworkProfileService NetworkProfile { get; } = new();
    public static V15DownloadIntelligence Intelligence { get; } = new(() => SmartDownloads);
    public static V15BandwidthProfiles BandwidthProfiles { get; private set; } = null!;
    public static V15StatisticsService V15Statistics { get; } = new(() => Manager?.Items ?? (IReadOnlyList<DownloadItem>)Array.Empty<DownloadItem>());
    public static V15HealthService Health { get; } = new(V15Context.Health);
    public static DownloadSleepGuard SleepGuard { get; private set; } = null!;

    /// <summary>Raised after the Options window saved (the main window rebuilds its category tree, etc.).</summary>
    public static event Action? SettingsChanged;
    public static void RaiseSettingsChanged() { ApplySettings(); ThemeManager.Apply(Settings.Theme); SettingsChanged?.Invoke(); }

    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashHandlers();
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // the app lives in the tray; MainWindow's Exit item shuts down

        var background = e.Args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        var torrentArg = e.Args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && DownloadManager.IsTorrentUrl(a));

        // One running copy only: a second launch just brings the first one to the front (and hands over a magnet link / .torrent file, if that's what was opened).
        bool isFirst;
        try
        {
            _singleInstance = new Mutex(true, @"Local\MakanDownloadManager.SingleInstance", out isFirst);
        }
        catch (System.Threading.AbandonedMutexException)
        {
            isFirst = true;
        }

        var currentPid = Environment.ProcessId;
        var otherProcesses = System.Diagnostics.Process.GetProcessesByName("MakanDownloadManager")
            .Where(p => p.Id != currentPid)
            .ToList();

        if (!isFirst && otherProcesses.Count == 0) isFirst = true;

        InitializeServices();
        try { new DiagnosticsService().Info($"Startup: background={background}, isFirst={isFirst}, otherProcesses={otherProcesses.Count}"); } catch { }

        if (!isFirst)
        {
            if (torrentArg != null) AskRunningInstanceToOpen(torrentArg);
            else if (!background) AskRunningInstanceToShow();
            Shutdown();
            return;
        }
        LocUi.Register();
        ThemeManager.Initialize();
        ThemeManager.Apply(Settings.Theme);
        Bridge = new NativeBridge(Manager, () => Settings.DefaultFolder, () => CategoryService.FolderFor("Video", Settings.DefaultFolder), FolderForFile);
        Bridge.CaptureRulesProvider = () => Settings.BuildCaptureRules();
        Bridge.LanguageProvider = () => Settings.Language;
        Bridge.ThemeProvider = () => ThemeManager.Current;
        Bridge.TorrentFolderProvider = () => Settings.TorrentSaveFolder;
        Bridge.YtDlpProvider = () => Manager.YtDlp;
        StartYtDlpUpdateCheck();

        MainWindow window;
        try { window = new MainWindow(); }
        catch (Exception ex)
        {
            try { new DiagnosticsService().Error("Creating the main window failed", ex); } catch (Exception) { }
            MessageBox.Show("Makan could not create its window:\n\n" + ex + "\n\nPlease send this text to the developer.", "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        MainWindow = window;
        Bridge.UiCommand += cmd => Dispatcher.BeginInvoke(() => window.HandleBridgeCommand(cmd));
        // Nothing starts by itself: files, batches and videos from the browser all go through the "Download File Info" dialog first
        // (unless the user turned "always ask" off in Settings).
        static bool Ask() => Settings.AskBeforeDownload;
        Bridge.AskBeforeStart = Ask;
        Bridge.DownloadPrompt = p => { if (!Ask()) return false; Dispatcher.BeginInvoke(() => window.PromptDownload(p)); return true; };
        Bridge.BatchPrompt = b => { if (!Ask()) return false; Dispatcher.BeginInvoke(() => window.PromptBatch(b)); return true; };
        Bridge.StreamPrompt = r => { if (!Ask()) return false; Dispatcher.BeginInvoke(() => window.PromptStream(r)); return true; };
        // "Download all links" always shows its picker window: choosing is the whole point.
        Bridge.LinksPrompt = l => { Dispatcher.BeginInvoke(() => window.PromptLinks(l)); return true; };
        Manager.ItemAdded += _ => { if (Settings.ShowWindowOnCapture) Dispatcher.BeginInvoke(() => window.ShowAndActivate()); };
        if (!background) window.ShowAndActivate();
        Queues.OnStartup();   // queues set to "start download when Makan starts"
        Manager.ResumeTorrentSeeding();
        if (!background && !Settings.FirstRunDone) Dispatcher.BeginInvoke(() => new FirstRunWindow(window).Show(), System.Windows.Threading.DispatcherPriority.Loaded);
        if (torrentArg != null) OpenTorrentAtStartup(torrentArg, window);
    }

    /// <summary>Makan itself was launched by double-clicking a .torrent file or a magnet link (Windows found it registered as the
    /// handler). Same effect as adding it any other way: it goes straight into the ordinary queue.</summary>
    static void OpenTorrentAtStartup(string urlOrPath, MainWindow window)
    {
        var result = Manager.AddTorrent(urlOrPath, Settings.TorrentSaveFolder);
        if (result.Error != null) { window.ShowAndActivate(); Dlg.Show(window, result.Error, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!result.Duplicate) { window.ShowAndActivate(); TorrentWindow.ShowFor(result.Item!, window); }
    }

    internal static void InitializeServices()
    {
        var dir = PortableModeService.DataDirectory;
        Directory.CreateDirectory(dir);
        Db = new DownloadDb(Path.Combine(dir, "downloads.db"));
        try { Db.Initialize(); }
        catch (InvalidDataException) when (Db.RestoreBackup()) { Db.Initialize(); }
        SmartDownloads = new SmartDownloadController(Db);
        Settings = new AppSettings(Db);
        BandwidthProfiles = new V15BandwidthProfiles(Db, kbps => { Settings.SpeedKbps = kbps; RaiseSettingsChanged(); });
        try { Basket.ImportJson(Db.Get("basket_json") ?? "[]"); } catch (Exception ex) { new DiagnosticsService().Error("The saved link basket could not be loaded; starting with an empty one", ex); }
        CategoryService.LoadJson(Settings.CategoriesJson);
        MigrateOldFolderSettings();
        Loc.SetLanguage(Settings.Language);
        Manager = new DownloadManager(Db, autoResumeUnfinished: Settings.AutoResume) { SmartController = SmartDownloads };
        Remote = new RemoteStatusServer(Manager, () => Settings.DefaultFolder, () => Settings.TorrentSaveFolder);
        Manager.RuleEngine = Rules;
        Manager.TorrentEngineFactory = CreateTorrentEngine;
        Manager.TorrentSeedOnStart = Settings.TorrentSeedOnStart;
        SleepGuard = new DownloadSleepGuard(() => Settings.KeepAwakeWhileDownloading, () => Manager.Items);

        ApplySettings();
        Queues = new QueueService(Manager, new DbQueueStore(Db), new WindowsPower());
    }

    /// <summary>Built the first time a magnet link or .torrent file is used, so nobody who never touches torrents opens a network port.</summary>
    static TorrentEngine CreateTorrentEngine()
    {
        var options = new TorrentEngineOptions
        {
            ListenPort = Settings.TorrentPort, EnableDht = Settings.TorrentDht, EnablePex = Settings.TorrentPex, EnablePortMapping = Settings.TorrentPortMapping,
            UploadSlots = Settings.TorrentUploadSlots, MaxPeersPerTorrent = Settings.TorrentMaxPeers,
            SeedAfterCompletion = Settings.TorrentSeedAfterCompletion, SeedRatioLimit = Settings.TorrentSeedRatioLimit,
            StateDirectory = Path.Combine(PortableModeService.DataDirectory, "torrents")
        };
        var engine = new TorrentEngine(options);
        engine.Start();
        engine.UploadLimit.Rate = Settings.TorrentUploadKbps * 1024;
        engine.DownloadLimit.Rate = Settings.TorrentDownloadKbps * 1024;
        return engine;
    }

    /// <summary>Pushes the options to the running download engine (called at start-up and after the Options window saved).</summary>
    static void ApplySettings()
    {
        Manager.MaxActive = Settings.MaxActive;
        Manager.DefaultConnections = Settings.Connections;
        Manager.LimitScope = Settings.SpeedLimitScope == "per_file" ? SpeedLimitScope.PerDownload : SpeedLimitScope.Combined;
        Manager.GlobalLimitBytesPerSec = Settings.SpeedKbps * 1024;
        Manager.MultiNetworkEnabled = Settings.MultiNetwork;
        Manager.TempDirectory = string.IsNullOrWhiteSpace(Settings.TempDirectory) ? null : Settings.TempDirectory;
        Manager.SetFileDateFromServer = Settings.SetFileDateFromServer;
        Manager.IgnoreModifiedOnResume = Settings.IgnoreModifiedOnResume;
        Manager.AdaptiveConnectionsEnabled = Settings.AdaptiveConnections && Settings.SmartDownloads;
        Manager.DefaultUserAgent = Settings.UserAgent;
        Manager.FfmpegPath = string.IsNullOrWhiteSpace(Settings.FfmpegPath) ? null : Settings.FfmpegPath;
        Manager.TorrentSeedOnStart = Settings.TorrentSeedOnStart;
        if (Manager.Torrents is { } torrents) { torrents.UploadLimit.Rate = Settings.TorrentUploadKbps * 1024; torrents.DownloadLimit.Rate = Settings.TorrentDownloadKbps * 1024; }   // port/DHT/slots need a restart; the limits apply live
        Manager.TorrentDownloadScheduleEnabled = Settings.TorrentDownloadScheduleEnabled;
        Manager.TorrentDownloadStart = ParseTimeOfDay(Settings.TorrentDownloadStartTime);
        Manager.TorrentDownloadStop = ParseTimeOfDay(Settings.TorrentDownloadStopTime);
        Manager.TorrentSeedScheduleEnabled = Settings.TorrentSeedScheduleEnabled;
        Manager.TorrentSeedStart = ParseTimeOfDay(Settings.TorrentSeedStartTime);
        Manager.TorrentSeedStop = ParseTimeOfDay(Settings.TorrentSeedStopTime);
        ApplyRemoteSettings();
        Manager.BoostDownloadSpeedEnabled = Settings.BoostDownloadSpeed;
        Manager.ProxyMode = Settings.ProxyMode;
        Manager.ProxyHost = Settings.ProxyHost;
        Manager.ProxyPort = Settings.ProxyPort;
        Manager.ProxyUseForHttp = Settings.ProxyUseForHttp;
        Manager.ProxyUseForHttps = Settings.ProxyUseForHttps;
        Manager.ProxyUsername = Settings.ProxyUsername;
        Manager.ProxyPassword = Settings.ProxyPassword;
        SleepGuard?.Refresh();
        RefreshYtDlp();
    }

    /// <summary>Starts, stops, or leaves the remote status server alone; a changed port needs a restart of the
    /// server (not the whole app), which this handles by stopping first whenever anything relevant changed.</summary>
    static void ApplyRemoteSettings()
    {
        if (Settings.RemoteEnabled && (!Remote.IsRunning || Remote.Port != Settings.RemotePort || Remote.Token != Settings.RemoteToken))
        {
            Remote.Stop();
            try { Remote.Start(Settings.RemotePort, Settings.RemoteToken); }
            catch (Exception ex) { new DiagnosticsService().Error("The remote status server could not start (the port may already be in use)", ex); }
        }
        else if (!Settings.RemoteEnabled && Remote.IsRunning) Remote.Stop();
    }

    static TimeSpan ParseTimeOfDay(string text) => TimeSpan.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : TimeSpan.Zero;

    /// <summary>Looks for yt-dlp again (after Options changed, or the tools were installed).</summary>
    public static void RefreshYtDlp() =>
        Manager.YtDlp = YtDlpTools.Create(Settings.YtDlpPath, Settings.FfmpegPath, YtDlpTools.DefaultToolsDirectory, Settings.YtCookiesBrowser);

    /// <summary>YouTube changes all the time: a yt-dlp that Makan installed itself updates itself when it is more than five days old.</summary>
    static void StartYtDlpUpdateCheck() => _ = Task.Run(async () =>
    {
        try
        {
            if (Manager.YtDlp is { } yt && yt.ExePath.StartsWith(YtDlpTools.DefaultToolsDirectory, StringComparison.OrdinalIgnoreCase))
                await yt.UpdateIfOldAsync(TimeSpan.FromDays(5), CancellationToken.None);
        }
        catch (Exception) { /* offline, or the file is in use: try again next time */ }
    });

    /// <summary>Earlier versions remembered one folder per category as "dir_Video" etc.; keep those choices as the categories' folders.</summary>
    static void MigrateOldFolderSettings()
    {
        if (!string.IsNullOrWhiteSpace(Settings.CategoriesJson)) return;
        foreach (var name in CategoryService.Names)
            if (Db.Get("dir_" + name) is { Length: > 0 } old) CategoryService.SetFolder(name, old);
        Settings.CategoriesJson = CategoryService.ToJson();
    }

    /// <summary>Which folder a file of this name goes to (its category's folder).</summary>
    public static string FolderForFile(string fileName) => CategoryService.FolderFor(CategoryService.For(fileName), Settings.DefaultFolder);

    /// <summary>Starts Makan again (used after the language changed) and closes this copy.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            // wait a moment so this copy has released the single-instance lock, then start the new one
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception) { return; }
        (Current.MainWindow as MainWindow)?.ExitFromScheduler();
    }

    /// <summary>Without this, an exception anywhere Windows doesn't hand to a try/catch - a background Task, the UI thread's
    /// own message loop, an async void event handler's continuation after an await - takes the whole app down with nothing
    /// written anywhere: "it just vanished" instead of a diagnosable report. Installed as the very first thing in OnStartup,
    /// before any window, timer or async call exists to trigger one.</summary>
    static void InstallCrashHandlers()
    {
        Current.DispatcherUnhandledException += (_, e) =>
        {
            LogCrash("the UI thread", e.Exception);
            e.Handled = true;   // keep the app (and any running downloads) alive rather than let one bad dialog take everything down
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("a background thread" + (e.IsTerminating ? " (the process is about to exit - this is the last thing that will be logged)" : ""), e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown error object"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("an unawaited background task", e.Exception);
            e.SetObserved();   // already logged; do not additionally crash the finalizer thread over it
        };
    }

    static void LogCrash(string where, Exception ex)
    {
        try { new DiagnosticsService().Error($"Unhandled exception on {where}", ex); } catch { /* logging itself must never throw - there is nothing left to hand this to */ }
    }

    static void AskRunningInstanceToShow()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", NativeBridge.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            var bytes = new UTF8Encoding(false).GetBytes("{\"kind\":\"show\"}\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            new StreamReader(pipe).ReadLine();
        }
        catch { /* the other instance is still starting up; nothing more to do */ }
    }

    /// <summary>A magnet link or .torrent file was opened while Makan was already running: hand it to that instance (the same
    /// pipe the browser extension uses) and bring its window to the front, exactly like clicking the link inside the browser would.
    /// Each pipe connection answers exactly one request, so this is two short connections, not one.</summary>
    static void AskRunningInstanceToOpen(string urlOrPath)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", NativeBridge.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            var bytes = new UTF8Encoding(false).GetBytes(System.Text.Json.JsonSerializer.Serialize(new { url = urlOrPath }) + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            new StreamReader(pipe).ReadLine();
        }
        catch { /* the other instance is still starting up; nothing more to do */ }
        AskRunningInstanceToShow();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SleepGuard?.Dispose();
        Bridge?.Dispose();
        Remote?.Dispose();
        Queues?.Dispose();
        try { Manager?.ShutdownAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch (Exception) { Manager?.Dispose(); }
        Db?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
