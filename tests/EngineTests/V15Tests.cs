using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>The V11-V15 layer: rules, adaptive connections, server learning, bandwidth profiles, statistics, health, recovery, secrets, and the queue/stop fixes.</summary>
static class V15Tests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    sealed class DictSettings : ISettingsStore
    {
        public readonly Dictionary<string, string> Data = new();
        public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Data[key] = value;
    }

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("Rules: wildcard rules match names and addresses and only change what they say", Rules);
        await Run("Adaptive connections: the user's number is kept until a server throttles", Adaptive);
        await Run("Server learning: throttling is remembered across restarts, success recovers", SmartController);
        await Run("Bandwidth profiles, statistics and health checks", ProfilesStatsHealth);
        await Run("Crash recovery scan and state files", Recovery);
        await Run("Secrets: an unreadable DPAPI value is dropped, diagnostics hide cookies", Secrets);
        await Run("A queued download started by hand while its queue is stopped really starts", StartedByHand);
        await Run("Updater: files are swapped with rollback, portable data is never touched, HTTPS only", Updater);
        await Run("Engine uses the learned limit and keeps a state file only while a download is unfinished", EngineLearning);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static string Sub() { var d = Path.Combine(Dir, "v15" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    static Task Rules()
    {
        var engine = new DownloadRuleEngine()
            .Add(new DownloadRule { Name = "iso", Pattern = "*.iso", Priority = 9, Connections = 12, Folder = @"D:\ISOs" })
            .Add(new DownloadRule { Name = "cdn", Pattern = "https://cdn.example.com/*", Category = "Programs" });

        var iso = new DownloadItem { Url = "https://example.com/get?id=5", FilePath = Path.Combine("dl", "Ubuntu.ISO"), Priority = 5 };
        engine.Apply(iso);
        T.Check("matches the file name (case-insensitive)", iso.Priority == 9 && iso.Connections == 12);
        T.Check("moves the file to the rule's folder, keeping its name", Path.GetFileName(iso.FilePath) == "Ubuntu.ISO" && iso.FilePath.StartsWith(@"D:\ISOs"), iso.FilePath);

        var cdn = new DownloadItem { Url = "https://cdn.example.com/x/tool.bin", FilePath = Path.Combine("dl", "tool.bin"), Priority = 3, Connections = 4 };
        engine.Apply(cdn);
        T.Check("matches the address", cdn.Category == "Programs");
        T.Check("a rule without a priority leaves the download's own priority alone", cdn.Priority == 3 && cdn.Connections == 4);

        var other = new DownloadItem { Url = "https://example.org/a.txt", FilePath = Path.Combine("dl", "a.txt"), Priority = 4 };
        engine.Apply(other);
        T.Check("no rule matches: untouched", other.Priority == 4 && other.FilePath == Path.Combine("dl", "a.txt"));
        var disabled = new DownloadRuleEngine().Add(new DownloadRule { Pattern = "*", Priority = 10, Enabled = false });
        var d = new DownloadItem { Url = "https://e.com/a", FilePath = "a", Priority = 2 }; disabled.Apply(d);
        T.Check("a disabled rule does nothing", d.Priority == 2);
        T.Check("regex characters in a pattern are plain text", new DownloadRuleEngine().Add(new DownloadRule { Pattern = "a+b(1).zip", Priority = 7 }) is { } e && Apply(e, "a+b(1).zip").Priority == 7 && Apply(e, "aab1.zip").Priority == 5);
        return Task.CompletedTask;
    }

    static DownloadItem Apply(DownloadRuleEngine e, string name) { var i = new DownloadItem { Url = "https://x/" + name, FilePath = name, Priority = 5 }; e.Apply(i); return i; }

    static Task Adaptive()
    {
        var a = new AdaptiveConnectionService();
        var host = new Uri("https://files.example.com/a.bin");
        T.Check("a new server gets exactly the connections the user chose", a.Suggest(host, 8) == 8 && a.Suggest(new Uri("https://other.example.com/"), 16) == 16);
        a.ReportThrottle(host);
        T.Check("a throttling server is halved", a.Suggest(host, 8) == 4);
        a.ReportThrottle(host);
        T.Check("and again", a.Suggest(host, 8) == 2);
        T.Check("never below one", Enumerable.Range(0, 6).Select(_ => { a.ReportThrottle(host); return a.Suggest(host, 8); }).Last() == 1);
        T.Check("another host is not affected", a.Suggest(new Uri("https://third.example.com/"), 8) == 8);
        var b = new AdaptiveConnectionService(); var h2 = new Uri("https://h2.example.com/");
        b.Suggest(h2, 4);
        T.Check("raising the setting is honoured at once when nobody complained", b.Suggest(h2, 12) == 12);
        T.Check("lowering it too", b.Suggest(h2, 3) == 3);
        return Task.CompletedTask;
    }

    static Task SmartController()
    {
        var store = new DictSettings();
        var c = new SmartDownloadController(store);
        var host = new Uri("https://cdn.example.com/f.bin");
        T.Check("unknown server: the user's number", c.Suggest(host, 8) == 8);
        c.ReportThrottle(host, 8);
        T.Check("after a 429 with 8 connections the next start uses 4", c.Suggest(host, 8) == 4);
        var restarted = new SmartDownloadController(store);
        T.Check("and that survives a restart", restarted.Suggest(host, 8) == 4);
        restarted.ReportThrottle(host, 4); restarted.ReportThrottle(host, 2);
        T.Check("a server that keeps throttling is used carefully (<= 2)", restarted.Suggest(host, 8) <= 2 && restarted.Suggest(host, 8) >= 1);
        var item = new DownloadItem { Url = host.ToString(), DoneBytes = 10_000_000 };
        for (var i = 0; i < 4; i++) restarted.ReportSuccess(item, TimeSpan.FromSeconds(2));
        T.Check("clean downloads bring the limit back up", restarted.Suggest(host, 8) > 2);
        var snap = restarted.Snapshot()["cdn.example.com"];
        T.Check("statistics: samples counted, average speed measured", snap.Samples == 4 && snap.AverageMbps > 30, $"{snap.Samples} {snap.AverageMbps:0.0}");
        T.Check("a limit is never above what the user asked", restarted.Suggest(host, 3) <= 3);

        var broken = new DictSettings(); broken.Data["v13_smart_profiles"] = "{ not json";
        T.Check("a damaged store is ignored, not fatal", new SmartDownloadController(broken).Suggest(host, 6) == 6);

        var intelligence = new V15DownloadIntelligence(() => restarted);
        T.Check("the Intelligent Center lists the server", intelligence.Servers().Any(s => s.Host == "cdn.example.com"));
        T.Check("and explains it", intelligence.Recommend(new DownloadItem { Url = host.ToString() }).Contains("connections"));
        T.Check("no history: says so", new V15DownloadIntelligence(() => null).Recommend(new DownloadItem { Url = "https://new.example.com/x" }).Contains("No history"));
        T.Check("bad address", new V15DownloadIntelligence(() => null).Recommend(new DownloadItem { Url = "nonsense" }) == "Invalid URL");
        return Task.CompletedTask;
    }

    static Task ProfilesStatsHealth()
    {
        var store = new DictSettings(); long applied = -1;
        var profiles = new V15BandwidthProfiles(store, kbps => applied = kbps);
        T.Check("four default profiles", profiles.Load().Select(p => p.Name).SequenceEqual(new[] { "Unlimited", "Gaming", "Work", "Night" }));
        T.Check("Gaming applies 2048 KB/s", profiles.Apply("gaming") && applied == 2048);
        T.Check("Unlimited applies 0", profiles.Apply("Unlimited") && applied == 0);
        T.Check("unknown profile: nothing applied", !profiles.Apply("nope") && applied == 0);
        T.Check("the active profile is found from the current limit", profiles.ActiveName(2048) == "Gaming" && profiles.ActiveName(0) == "Unlimited" && profiles.ActiveName(777) == null);
        profiles.Save(new[] { new V15BandwidthProfiles.Profile("Call", 512 * 1024, "quiet") });
        T.Check("own profiles are saved", profiles.Load().Single().Name == "Call" && profiles.Apply("Call") && applied == 512);
        store.Data["v15_bandwidth_profiles"] = "###";
        T.Check("a damaged setting falls back to the defaults", profiles.Load().Count == 4);

        var items = new List<DownloadItem>
        {
            new() { Status = "Complete", DoneBytes = 100 }, new() { Status = "Downloading", DoneBytes = 50, SpeedBytesPerSec = 1_000_000, RetryCount = 2 },
            new() { Status = "Downloading", DoneBytes = 10, SpeedBytesPerSec = 3_000_000 }, new() { Status = "Failed" }, new() { Status = "Paused", DoneBytes = 5 }, new() { Status = "Queued" }
        };
        var s = V15StatisticsService.Build(items);
        T.Check("counts", s is { Total: 6, Completed: 1, Active: 2, Failed: 1, Paused: 1, Queued: 1, Retries: 2 });
        T.Check("downloaded bytes include partial progress, finished bytes only the finished", s.DownloadedBytes == 165 && s.CompletedBytes == 100);
        T.Check("speed: average of the running ones in Mbps, fastest in bytes/s", Math.Abs(s.AverageMbps - 16) < 0.001 && s.FastestBytesPerSec == 3_000_000, $"{s.AverageMbps}");
        T.Check("no items: zeros, no crash", V15StatisticsService.Build(new List<DownloadItem>()) is { Total: 0, AverageMbps: 0, FastestBytesPerSec: 0 });

        var data = Sub(); var app = Sub(); var downloads = Path.Combine(Sub(), "dl");
        File.WriteAllText(Path.Combine(data, "downloads.db"), "x"); File.WriteAllText(Path.Combine(app, "MakanNativeHost.exe"), "x");
        var inputs = new V15HealthInputs(data, app, downloads, true, null, null, new[] { ("Chrome", true), ("Firefox", false) }, () => true);
        var checks = new V15HealthService(() => inputs).Run().ToDictionary(c => c.Name);
        T.Check("database present and healthy", checks["Database"].Healthy);
        T.Check("the download folder is created and writable", checks["Download folder"].Healthy && Directory.Exists(downloads));
        T.Check("browser integration: connected to Chrome, Firefox named as missing", checks["Browser integration"] is { Healthy: true } b && b.Detail.Contains("Chrome") && b.Detail.Contains("Firefox"), checks["Browser integration"].Detail);
        T.Check("missing optional tools are hints, not problems", checks["yt-dlp (YouTube and other sites)"] is { Healthy: false, Optional: true } && checks["FFmpeg (merging video and sound)"].Optional);
        var bad = new V15HealthService(() => inputs with { DatabaseIntegrity = () => false, YtDlpPath = "C:\\t\\yt-dlp.exe", Browsers = new[] { ("Chrome", false) } }).Run().ToDictionary(c => c.Name);
        T.Check("a failing integrity check is reported", !bad["Database"].Healthy && bad["Database"].Detail.Contains("problem"));
        T.Check("no browser registered: not healthy, with the fix", !bad["Browser integration"].Healthy && bad["Browser integration"].Detail.Contains("install-browser-integration"));
        T.Check("a found tool is reported", bad["yt-dlp (YouTube and other sites)"].Healthy);
        var missingDb = new V15HealthService(() => inputs with { DataDirectory = Sub() }).Run().First(c => c.Name == "Database");
        T.Check("missing database file", !missingDb.Healthy);
        return Task.CompletedTask;
    }

    static Task Recovery()
    {
        var dir = Sub();
        DownloadItem Make(string name, string status, long done, long? total)
        {
            var item = new DownloadItem { Url = "https://e.com/" + name, FilePath = Path.Combine(dir, name), Status = status, DoneBytes = done, TotalBytes = total, Connections = 4 };
            DownloadStateManifest.Write(item); return item;
        }
        var paused = Make("paused.bin", "Paused", 500, 1000);
        var complete = Make("done.bin", "Complete", 1000, 1000);
        var broken = new DownloadItem { Url = "https://e.com/b", FilePath = Path.Combine(dir, "broken.bin"), Status = "Failed" };
        File.WriteAllText(DownloadStateManifest.PathFor(broken), "{ nope");
        var none = new DownloadItem { Url = "https://e.com/n", FilePath = Path.Combine(dir, "none.bin"), Status = "Paused" };
        var found = V15RecoveryService.Scan(new[] { paused, complete, broken, none });
        T.Check("finds the interrupted download with its position", found.Count == 1 && found[0].DoneBytes == 500 && found[0].TotalBytes == 1000 && found[0].Path == paused.FilePath, $"{found.Count}");
        T.Check("a finished download, a damaged state file and a missing one are not listed", found.All(f => !f.Path.EndsWith("done.bin") && !f.Path.EndsWith("broken.bin")));
        DownloadStateManifest.Delete(paused);
        T.Check("deleting the state file removes it and its temp file", !File.Exists(DownloadStateManifest.PathFor(paused)) && V15RecoveryService.Scan(new[] { paused }).Count == 0);
        return Task.CompletedTask;
    }

    static Task Secrets()
    {
        T.Check("a plain value stays plain (and is flagged, so it gets protected)", SecretProtector.DecryptOrPlain("sid=abc", out var plainFlag) == "sid=abc" && plainFlag);
        T.Check("null and empty pass through", SecretProtector.DecryptOrPlain(null, out _) == null && SecretProtector.DecryptOrPlain("", out _) == "");
        var value = SecretProtector.DecryptOrPlain("dpapi:v1:AAAAAAAA", out var flagged);
        T.Check("a protected value that cannot be opened is dropped, never used as a cookie", value == null && !flagged, value);
        T.Check("garbage after the prefix does not throw", SecretProtector.DecryptOrPlain("dpapi:v1:%%%%", out _) == null);

        T.Check("cookie headers are hidden completely (several pairs)", DiagnosticsService.Redact("GET failed Cookie: sid=SUPERSECRET; other=SECOND2; x=3").Contains("REDACTED") && !DiagnosticsService.Redact("Cookie: sid=SUPERSECRET; other=SECOND2").Contains("SECOND2") && !DiagnosticsService.Redact("Set-Cookie: sid=SUPERSECRET; Path=/").Contains("SUPERSECRET"));
        T.Check("bearer / basic tokens are hidden, not just the word Bearer", !DiagnosticsService.Redact("Authorization: Bearer TOPSECRET123").Contains("TOPSECRET123") && !DiagnosticsService.Redact("proxy-authorization: Basic dXNlcjpwYXNz").Contains("dXNlcjpwYXNz"));
        T.Check("token / api key / password values are hidden", !DiagnosticsService.Redact("retry with access_token=AAA111&x=1").Contains("AAA111") && !DiagnosticsService.Redact("apikey: KEY222").Contains("KEY222") && !DiagnosticsService.Redact("password=hunter2").Contains("hunter2"));
        T.Check("signed URLs lose their signature but keep the rest", DiagnosticsService.Redact("https://b.s3.amazonaws.com/f?X-Amz-Signature=ABCDEF123&a=1") == "https://b.s3.amazonaws.com/f?X-Amz-Signature=[REDACTED]&a=1");
        T.Check("normal messages are untouched", DiagnosticsService.Redact("Download failed: The server returned 503 for https://example.com/a.zip") == "Download failed: The server returned 503 for https://example.com/a.zip");

        var log = new DiagnosticsService();
        log.Error("request failed Cookie: sessionid=LOGSECRET; other=1");
        log.Error("and Authorization: Bearer LOGTOKEN plus https://x/y?sig=LOGSIG&a=1");
        var text = string.Join("\n", Directory.GetFiles(log.DirectoryPath, "makan-*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(1).Select(File.ReadAllText));
        T.Check("what reaches the log file is redacted too", !text.Contains("LOGSECRET") && !text.Contains("LOGTOKEN") && !text.Contains("LOGSIG") && text.Contains("[REDACTED]"));
        return Task.CompletedTask;
    }

    static async Task StartedByHand()
    {
        var dir = Sub();
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
        using var q = new QueueService(m, new MemQueueStoreForV15(), new PowerStub(), autoTimers: false);
        var item = new DownloadItem { Url = Base + "/small.bin?byhand", FilePath = Path.Combine(dir, "byhand.bin"), Connections = 2 };
        q.AddLater(item);
        T.Check("parked in the main queue, stopped", item.QueueName != null && item.Status == "Paused" && !q.IsRunning(q.Main.Id));
        m.Enqueue(item);                                    // e.g. the Start button of the progress window
        T.Check("starts and completes although its queue is not running", await Program.WaitStatusPublic(item, DownloadStatus.Complete, 30), item.Status + " " + item.LastError);

        // while the queue IS running, the queue (not the global scheduler) decides: MaxParallel 1 means one at a time
        var a = new DownloadItem { Url = Base + "/slow.bin?a", FilePath = Path.Combine(dir, "a.bin"), Connections = 2 };
        var b = new DownloadItem { Url = Base + "/slow.bin?b", FilePath = Path.Combine(dir, "b.bin"), Connections = 2 };
        q.AddLater(a); q.AddLater(b);
        q.SetMaxParallel(q.Main.Id, 1);
        q.Start(q.Main.Id);
        await Program.WaitStatusPublic(a, DownloadStatus.Downloading, 20);
        await Task.Delay(2500);                             // several scheduler ticks
        T.Check("a running queue with MaxParallel 1 never runs two files (the global scheduler does not bypass it)", a.Status == "Downloading" && b.Status is "Queued" or "Paused", $"{a.Status} / {b.Status}");
        q.Stop(q.Main.Id);
        m.Cancel(a); m.Cancel(b);
    }

    static async Task EngineLearning()
    {
        var dir = Sub();
        var learned = new SmartDownloadController(new DictSettings());
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { SmartController = learned };
        learned.ReportThrottle(new Uri(Base), 8);           // this test server "complained" once: 8 -> 4
        var item = new DownloadItem { Url = Base + "/slow.bin?learn", FilePath = Path.Combine(dir, "learn.bin"), Connections = 8 };
        m.Enqueue(item);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (item.DoneBytes < 3_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(30);
        T.Check("the learned limit is used (4 connections instead of the 8 asked for)", m.GetConnections(item).Count == 4, m.GetConnections(item).Count.ToString());
        T.Check("a state file exists while the download is unfinished", File.Exists(DownloadStateManifest.PathFor(item)));
        m.Pause(item);
        await Program.WaitStatusPublic(item, DownloadStatus.Paused, 15);
        T.Check("and stays for a paused one, so recovery can list it", File.Exists(DownloadStateManifest.PathFor(item)) && V15RecoveryService.Scan(new[] { item }).Count == 1);
        m.Cancel(item);
        await Program.WaitStatusPublic(item, DownloadStatus.Cancelled, 15);
        T.Check("cancel removes it", !File.Exists(DownloadStateManifest.PathFor(item)));

        var quick = new DownloadItem { Url = Base + "/mid.bin?state", FilePath = Path.Combine(dir, "quick.bin"), Connections = 2 };
        m.Enqueue(quick);
        T.Check("finished download completes", await Program.WaitStatusPublic(quick, DownloadStatus.Complete, 30));
        T.Check("and leaves no state file behind", !File.Exists(DownloadStateManifest.PathFor(quick)));
        T.Check("the finished download taught the controller about this server", learned.Snapshot().Values.Any(v => v.Samples >= 1));
    }

    static Task Updater()
    {
        var install = Sub(); var stage = Sub();
        File.WriteAllText(Path.Combine(install, "MakanDownloadManager.exe"), "old app");
        File.WriteAllText(Path.Combine(install, "keep.txt"), "mine");
        Directory.CreateDirectory(Path.Combine(install, "data")); File.WriteAllText(Path.Combine(install, "data", "downloads.db"), "portable data");
        File.WriteAllText(Path.Combine(stage, "MakanDownloadManager.exe"), "new app");
        Directory.CreateDirectory(Path.Combine(stage, "browser-extension")); File.WriteAllText(Path.Combine(stage, "browser-extension", "background.js"), "new js");

        MakanUpdater.FileSwap.Apply(stage, install);
        T.Check("files are replaced and new ones added (also in sub-folders)", File.ReadAllText(Path.Combine(install, "MakanDownloadManager.exe")) == "new app" && File.ReadAllText(Path.Combine(install, "browser-extension", "background.js")) == "new js");
        T.Check("files and folders the update does not contain (portable data!) are untouched", File.ReadAllText(Path.Combine(install, "keep.txt")) == "mine" && File.ReadAllText(Path.Combine(install, "data", "downloads.db")) == "portable data");
        T.Check("no backup files are left behind", !Directory.GetFiles(install, "*.old-*", SearchOption.AllDirectories).Any());

        // a failure half-way undoes everything
        var install2 = Sub(); var stage2 = Sub();
        File.WriteAllText(Path.Combine(install2, "a.exe"), "old a"); File.WriteAllText(Path.Combine(install2, "b.dll"), "old b");
        File.WriteAllText(Path.Combine(stage2, "a.exe"), "new a"); File.WriteAllText(Path.Combine(stage2, "b.dll"), "new b"); File.WriteAllText(Path.Combine(stage2, "c.dll"), "brand new");
        var copied = 0; var failed = false;
        try { MakanUpdater.FileSwap.Apply(stage2, install2, _ => { if (++copied == 2) throw new IOException("disk full"); }); }
        catch (IOException) { failed = true; }
        T.Check("the failure is reported", failed);
        T.Check("every file is back as it was, nothing new is left, no backups remain",
            File.ReadAllText(Path.Combine(install2, "a.exe")) == "old a" && File.ReadAllText(Path.Combine(install2, "b.dll")) == "old b" && !File.Exists(Path.Combine(install2, "c.dll")) && !Directory.GetFiles(install2, "*.old-*").Any(),
            string.Join(",", Directory.GetFiles(install2).Select(Path.GetFileName)));

        File.WriteAllText(Path.Combine(install2, "MakanUpdater.exe.old-20200101000000000"), "x");
        MakanUpdater.FileSwap.CleanOld(install2);
        T.Check("leftovers of earlier updates (a program that was running) are cleaned", !Directory.GetFiles(install2, "*.old-*").Any());

        // the real updater program refuses anything that is not HTTPS or not valid, without touching the installation
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "updater", "bin", "Release", "net8.0", "MakanUpdater.dll"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) { Console.WriteLine("  (updater program not built: skipped)"); return Task.CompletedTask; }
        var dll = Path.Combine(dir, "updater", "bin", "Release", "net8.0", "MakanUpdater.dll");
        (int Code, string Error) RunUpdater(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add(dll); foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!; var err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit(30000);
            return (p.ExitCode, err);
        }
        var http = RunUpdater("--manifest", Base + "/manifest.json", "--install-dir", install);
        T.Check("a plain http:// manifest is refused (exit 1, says HTTPS)", http.Code == 1 && http.Error.Contains("HTTPS"), http.Error);
        var noDir = RunUpdater("--manifest", "https://example.invalid/m.json", "--install-dir", Path.Combine(install, "nope"));
        T.Check("a missing install folder is refused", noDir.Code == 1 && noDir.Error.Contains("Install folder"), noDir.Error);
        var otherProcess = RunUpdater("--manifest", "https://example.invalid/m.json", "--install-dir", install, "--process", "notepad");
        T.Check("only Makan itself may be stopped", otherProcess.Code == 1 && otherProcess.Error.Contains("Only the MakanDownloadManager"), otherProcess.Error);
        T.Check("the installation was not touched by any of them", File.ReadAllText(Path.Combine(install, "MakanDownloadManager.exe")) == "new app");
        return Task.CompletedTask;
    }

    sealed class PowerStub : IPowerActions
    {
        public Task<bool> PowerOffAsync(PowerAction action, bool force) => Task.FromResult(false);
        public void ExitApplication() { }
        public void OpenFile(string path) { }
    }

    sealed class MemQueueStoreForV15 : IQueueStore
    {
        string? _json;
        public string? Load() => _json;
        public void Save(string json) => _json = json;
    }
}
