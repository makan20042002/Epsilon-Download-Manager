using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>IDM-style behaviour: 8 connections by default, a smart increase that stops when it no longer helps, failed files that
/// come back by themselves, and queues that keep retrying (then still finish and shut down).</summary>
static class RetryLaterTests
{
    sealed class PowerFake : IPowerActions
    {
        public readonly List<string> Calls = new();
        public void OpenFile(string path) { }
        public void ExitApplication() => Calls.Add("exit");
        public Task<bool> PowerOffAsync(PowerAction action, bool force) { Calls.Add("power"); return Task.FromResult(true); }
    }
    sealed class QStore : IQueueStore { public string? Json; public string? Load() => Json; public void Save(string json) => Json = json; }

    static ProbeResult Probe(long length) => new(true, length, HttpStatusCode.PartialContent, null, null, null, null, new Uri("https://files.example.com/big.bin"));

    public static void Run()
    {
        Console.WriteLine("\n== IDM-style retries, 8 default connections, smart increase");
        // ---- default 8 on ordinary files
        using (var m = new DownloadManager(new MemoryStore()))
        {
            var mid = new DownloadItem { Url = "https://files.example.com/big.bin", FilePath = "big.bin", Connections = 8 };
            T.Check("a 100 MB file starts with 8 connections by default", m.ChooseConnections(mid, Probe(100L << 20)) == 8);
            T.Check("a 20 MB file also starts with 8", m.ChooseConnections(mid, Probe(20L << 20)) == 8);
            T.Check("a tiny 3 MB file uses 2", m.ChooseConnections(mid, Probe(3L << 20)) == 2);
        }
        // ---- smart increase
        var svc = new AdaptiveConnectionService(); var uri = new Uri("https://files.example.com/big.bin");
        T.Check("a new server starts with the 8 asked for", svc.Suggest(uri, 8) == 8);
        svc.ReportSuccess(uri, 10L << 20, TimeSpan.FromSeconds(30), 8);
        T.Check("a small download teaches nothing", svc.Suggest(uri, 8) == 8);
        svc.ReportSuccess(uri, 400L << 20, TimeSpan.FromSeconds(40), 8);
        T.Check("a big download that ran fine lets the next one use 12", svc.Suggest(uri, 8) == 12);
        svc.ReportSuccess(uri, 400L << 20, TimeSpan.FromSeconds(25), 12);   // clearly faster with 12
        T.Check("more connections gave more speed, so it goes on to 16", svc.Suggest(uri, 8) == 16);
        svc.ReportSuccess(uri, 400L << 20, TimeSpan.FromSeconds(25), 16);   // no faster than with 12
        T.Check("16 was no faster, so it settles at the best count (12) and stops growing", svc.Suggest(uri, 8) == 12);
        svc.ReportSuccess(uri, 400L << 20, TimeSpan.FromSeconds(20), 12);
        T.Check("it stays at 12 afterwards", svc.Suggest(uri, 8) == 12);
        svc.ReportThrottle(uri);
        T.Check("a server that throttles takes the increase away at once and gets fewer than 8", svc.Suggest(uri, 8) < 8);
        var never = new AdaptiveConnectionService(); var other = new Uri("https://fast.example.com/x.bin");
        never.Suggest(other, 8);
        for (var i = 0; i < 10; i++) never.ReportSuccess(other, 800L << 20, TimeSpan.FromSeconds(10 - Math.Min(i, 8)), Math.Max(8, never.Suggest(other, 8)));
        T.Check("the smart increase never goes past 32", never.Suggest(other, 8) <= AdaptiveConnectionService.SmartMaxConnections);
        // ---- queue defaults
        T.Check("a new queue has retries switched on (3 extra tries)", new QueueSchedule().RetriesEnabled && new QueueSchedule().Retries == 3);
        var store = new QStore { Json = "{\"NextId\":2,\"Queues\":[{\"Id\":1,\"Name\":\"Main download queue\",\"IsMain\":true,\"Schedule\":{\"RetriesEnabled\":false,\"Retries\":0},\"ItemIds\":[]}]}" };
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false))
        using (var q = new QueueService(m, store, new PowerFake(), autoTimers: false))
            T.Check("a queue saved by an older version (retries off) now retries 3 times", q.Main.Schedule.RetriesEnabled && q.Main.Schedule.Retries == 3 && q.Main.Schedule.RetryRulesVersion == 1);
        store.Json = "{\"NextId\":2,\"Queues\":[{\"Id\":1,\"Name\":\"Main download queue\",\"IsMain\":true,\"Schedule\":{\"RetriesEnabled\":false,\"Retries\":5,\"RetryRulesVersion\":1},\"ItemIds\":[]}]}";
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false))
        using (var q = new QueueService(m, store, new PowerFake(), autoTimers: false))
            T.Check("a user who switched retries off after that keeps it off", !q.Main.Schedule.RetriesEnabled && q.Main.Schedule.Retries == 5);
        using (var m = new DownloadManager(new MemoryStore()))
            T.Check("the pauses between automatic tries are 30 s, 1, 2, 5 and 10 minutes", m.AutoRetryDelay(0).TotalSeconds == 30 && m.AutoRetryDelay(1).TotalSeconds == 60 && m.AutoRetryDelay(2).TotalSeconds == 120 && m.AutoRetryDelay(3).TotalSeconds == 300 && m.AutoRetryDelay(9).TotalSeconds == 600);
    }

    /// <summary>Acts like a server that does not answer in time: the first requests end with a TaskCanceledException (what HttpClient throws
    /// on a connect timeout) although nobody pressed Stop.</summary>
    sealed class TimeoutFirstHandler : DelegatingHandler
    {
        int _n; readonly int _fail;
        public TimeoutFirstHandler(int fail) : base(new SocketsHttpHandler { AllowAutoRedirect = false }) { _fail = fail; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Interlocked.Increment(ref _n) <= _fail ? throw new TaskCanceledException("The request timed out.", new TimeoutException()) : base.SendAsync(request, cancellationToken);
    }

    public static async Task RunLive(string baseUrl, string dir, HttpClient http)
    {
        Console.WriteLine("\n== A download that stopped on a temporary problem comes back by itself");
        await http.GetAsync(baseUrl + "/__reset");
        var sub = Path.Combine(dir, "rl" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(sub);
        var notices = new List<string>();
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { RetryAttempts = 0, AutoRetryLater = true, AutoRetryRounds = 2, AutoRetryDelays = new[] { TimeSpan.FromMilliseconds(400) } };
        m.Notification += (_, text) => notices.Add(text);
        var name = "later" + Guid.NewGuid().ToString("N")[..5] + ".bin";
        var item = new DownloadItem { Url = baseUrl + "/down503.bin?" + name, FilePath = Path.Combine(sub, name), Connections = 1 };
        m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        var done = false;
        while (sw.Elapsed.TotalSeconds < 25) { if (item.Status == nameof(DownloadStatus.Failed) && item.AutoRetryRound >= 2 && item.AutoRetryAt == null && !string.IsNullOrEmpty(item.LastError) && !item.LastError.StartsWith("Will try again", StringComparison.Ordinal)) { done = true; break; } await Task.Delay(100); }
        T.Check("it came back twice by itself, then stayed failed", done && item.Status == nameof(DownloadStatus.Failed) && item.AutoRetryRound == 2, $"{item.Status} round {item.AutoRetryRound} {item.LastError}");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(baseUrl + "/__stats"));
        var hits = doc.RootElement.GetProperty("hits").TryGetProperty("/down503.bin?" + name, out var v) ? v.GetInt32() : 0;
        T.Check("the server saw 1 first try + 2 comebacks", hits == 3, $"hits={hits}");
        T.Check("only the final failure raised a notification (not every pause)", notices.Count(x => x.Contains("failed", StringComparison.OrdinalIgnoreCase)) == 1, string.Join("|", notices));

        // a permanent error is not retried
        var missing = new DownloadItem { Url = baseUrl + "/notfound?" + name, FilePath = Path.Combine(sub, "nf-" + name), Connections = 1 };
        m.Enqueue(missing);
        await Program.WaitStatusPublic(missing, DownloadStatus.Failed, 15);
        await Task.Delay(1500);
        T.Check("a 404 is permanent: it does not come back", missing.Status == nameof(DownloadStatus.Failed) && missing.AutoRetryRound == 0 && missing.AutoRetryAt == null);

        // switched off: no comeback
        using var off = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { RetryAttempts = 0, AutoRetryLater = false, AutoRetryDelays = new[] { TimeSpan.FromMilliseconds(300) } };
        var name2 = "off" + Guid.NewGuid().ToString("N")[..5] + ".bin";
        var item2 = new DownloadItem { Url = baseUrl + "/down503.bin?" + name2, FilePath = Path.Combine(sub, name2), Connections = 1 };
        off.Enqueue(item2);
        await Program.WaitStatusPublic(item2, DownloadStatus.Failed, 15);
        await Task.Delay(1500);
        T.Check("with the option off it stays failed after one try", item2.Status == nameof(DownloadStatus.Failed) && item2.AutoRetryRound == 0);

        // A timeout nobody asked for must never look like "Stopped" (the user did not stop it): it is retried and then finishes.
        using var flaky = new DownloadManager(new MemoryStore(), new TimeoutFirstHandler(2), autoResumeUnfinished: false) { RetryAttempts = 5, AutoRetryLater = true, AutoRetryRounds = 2, AutoRetryDelays = new[] { TimeSpan.FromMilliseconds(300) } };
        var item3 = new DownloadItem { Url = baseUrl + "/mid.bin", FilePath = Path.Combine(sub, "timeout-" + name), Connections = 1 };
        flaky.Enqueue(item3);
        var sw3 = Stopwatch.StartNew(); var sawStopped = false;
        while (sw3.Elapsed.TotalSeconds < 40 && item3.Status != nameof(DownloadStatus.Complete)) { if (item3.Status == nameof(DownloadStatus.Paused)) sawStopped = true; await Task.Delay(50); }
        T.Check("a timeout that nobody asked for is retried and the download finishes", item3.Status == nameof(DownloadStatus.Complete), $"{item3.Status} {item3.LastError}");
        T.Check("and it never showed as 'Stopped' on the way", !sawStopped);

        // a server that times out every time ends as a clear failure that comes back later - not as a silent "Stopped"
        using var dead = new DownloadManager(new MemoryStore(), new TimeoutFirstHandler(1000), autoResumeUnfinished: false) { RetryAttempts = 1, AutoRetryLater = false };
        var item4 = new DownloadItem { Url = baseUrl + "/mid.bin", FilePath = Path.Combine(sub, "dead-" + name), Connections = 1 };
        dead.Enqueue(item4);
        var sw4 = Stopwatch.StartNew();
        while (sw4.Elapsed.TotalSeconds < 40 && item4.Status is nameof(DownloadStatus.Queued) or nameof(DownloadStatus.Downloading)) await Task.Delay(100);
        T.Check("a server that never answers ends as Failed with a message, not as 'Stopped'", item4.Status == nameof(DownloadStatus.Failed) && !string.IsNullOrEmpty(item4.LastError), $"{item4.Status} {item4.LastError}");
    }
}
