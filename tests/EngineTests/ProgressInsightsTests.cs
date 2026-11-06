using System.Diagnostics;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>V2 progress window: speed graph data (1 / 5 / 30 min), network breakdown, segment map states and legend counts,
/// log lines, SHA-256 line, and the safe "Change" of the save folder. The pure part needs no web server
/// (quick run: set MAKAN_TESTS=progress); the live part runs with the full suite.</summary>
static class ProgressInsightsTests
{
    static readonly DateTime T0 = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    const long MB = 1024 * 1024;

    public static void Run()
    {
        SpeedGraph();
        Networks();
        Segments();
        Logs();
        Verification();
        ChangeFolder();
    }

    // ---------------------------------------------------------------- speed graph

    static void SpeedGraph()
    {
        T.Check("the graph offers 1 min, 5 min and 30 min", SpeedHistory.RangeSeconds.SequenceEqual(new[] { 60, 300, 1800 }));

        var h = new SpeedHistory();
        for (var s = 0; s < 10; s++) h.Add(T0.AddSeconds(s), 1 * MB, 256 * 1024);
        var now = T0.AddSeconds(9);
        var oneMinute = h.Series(60, 60, now);
        T.Check("1 min: 60 points, the last ten seconds carry the speed, the older fifty are zero",
            oneMinute.Length == 60 && oneMinute.Skip(50).All(v => Math.Abs(v - MB) < 1) && oneMinute.Take(50).All(v => v == 0));
        T.Check("1 min reduced to 6 points = averages of ten seconds", h.Series(60, 6, now) is { Length: 6 } six && Math.Abs(six[5] - MB) < 1 && six[0] == 0);
        var fiveMinutes = h.Series(300, 60, now);
        T.Check("5 min: the same ten seconds are squeezed into the newest 5-second slots (two points)",
            fiveMinutes.Length == 60 && fiveMinutes.Take(58).All(v => v == 0) && fiveMinutes.Skip(58).All(v => Math.Abs(v - MB) < 1));
        T.Check("30 min works with the same number of points", h.Series(1800, 60, now).Length == 60 && h.Series(1800, 60, now)[59] > 0);
        T.Check("upload is a separate line", Math.Abs(h.Series(60, 60, now, upload: true)[59] - 256 * 1024) < 1 && Math.Abs(h.Series(60, 60, now)[59] - MB) < 1);
        T.Check("the peak of the range is found", Math.Abs(h.Peak(60, now) - MB) < 1 && h.Peak(60, T0.AddHours(3)) == 0);

        var avg = new SpeedHistory();
        avg.Add(T0, 2 * MB); avg.Add(T0.AddMilliseconds(400), 4 * MB);
        T.Check("several samples inside one second are averaged", avg.SampleCount == 1 && Math.Abs(avg.Series(1, 1, T0)[0] - 3 * MB) < 1);
        avg.Add(T0.AddSeconds(-5), 99 * MB);
        T.Check("a sample from the past (clock change) is ignored", avg.SampleCount == 1);
        var negative = new SpeedHistory(); negative.Add(T0, -5 * MB, -1);
        T.Check("a negative speed is stored as zero", negative.Peak(60, T0) == 0 && negative.Peak(60, T0, upload: true) == 0);

        var cap = new SpeedHistory(1800);
        cap.Add(T0, MB); cap.Add(T0.AddSeconds(2000), MB);
        T.Check("samples older than 30 minutes are dropped", cap.SampleCount == 1);

        T.Check("axis: never below 1 MB/s", SpeedHistory.AxisMaximum(0) == MB && SpeedHistory.AxisMaximum(500_000) == MB);
        T.Check("axis: rounded up to 1, 2 or 5 x 10^n", SpeedHistory.AxisMaximum(3 * MB) == 5_000_000 && SpeedHistory.AxisMaximum(120 * MB) == 200_000_000 && SpeedHistory.AxisMaximum(60 * MB) == 100_000_000);
    }

    // ---------------------------------------------------------------- network breakdown

    static void Networks()
    {
        var rows = new[]
        {
            new ConnectionInfo(1, 300, "Receiving data...", "Ethernet"), new ConnectionInfo(2, 300, "Receiving data...", "Ethernet"), new ConnectionInfo(3, 0, "Send GET...", "Ethernet"),
            new ConnectionInfo(4, 200, "Receiving data...", "Wi-Fi"), new ConnectionInfo(5, 100, "Receiving data...", "Wi-Fi"),
            new ConnectionInfo(6, 100, "Receiving data...", ""),
        };
        var shares = NetworkBreakdown.FromConnections(rows);
        T.Check("three networks, largest first (Ethernet, Wi-Fi, Windows route)", shares.Select(s => s.Network).SequenceEqual(new[] { "Ethernet", "Wi-Fi", "Windows route" }), string.Join(",", shares.Select(s => s.Network)));
        T.Check("shares follow the bytes each network carried (60 / 30 / 10 %) and add up to 100",
            Math.Abs(shares[0].Percent - 60) < 0.001 && Math.Abs(shares[1].Percent - 30) < 0.001 && Math.Abs(shares[2].Percent - 10) < 0.001 && Math.Abs(shares.Sum(s => s.Percent) - 100) < 0.001,
            string.Join(" ", shares.Select(s => $"{s.Network}={s.Percent:0.0}")));
        T.Check("connections are counted per network", shares[0].Connections == 3 && shares[1].Connections == 2 && shares[2].Connections == 1);
        T.Check("more than one network = Multi-Network is in use", NetworkBreakdown.IsMultiNetwork(shares));

        var idle = NetworkBreakdown.FromConnections(new[] { new ConnectionInfo(1, 0, "Send GET...", "Ethernet"), new ConnectionInfo(2, 0, "Send GET...", "Ethernet"), new ConnectionInfo(3, 0, "Send GET...", "Wi-Fi") });
        T.Check("before any data arrives the split follows the connection count", Math.Abs(idle.Single(s => s.Network == "Ethernet").Percent - 66.667) < 0.01 && Math.Abs(idle.Sum(s => s.Percent) - 100) < 0.001);
        T.Check("a normal single-route download is one row and not Multi-Network",
            NetworkBreakdown.FromConnections(new[] { new ConnectionInfo(1, 10, "Receiving data...", "Windows route") }) is { Count: 1 } one && !NetworkBreakdown.IsMultiNetwork(one));
        T.Check("no connections = no rows", NetworkBreakdown.FromConnections(Array.Empty<ConnectionInfo>()).Count == 0);
        T.Check("network names are matched without case", NetworkBreakdown.FromConnections(new[] { new ConnectionInfo(1, 1, "", "wi-fi"), new ConnectionInfo(2, 1, "", "Wi-Fi") }).Count == 1);
    }

    // ---------------------------------------------------------------- segment map

    static void Segments()
    {
        var map = new[] { 1.0, 1.0, 0.6, 0.2, 0.0, 0.0 };
        var running = SegmentMap.Classify(map, nameof(DownloadStatus.Downloading), 4);
        T.Check("running: finished parts are Complete, partly fetched parts Downloading, the rest Waiting",
            running.SequenceEqual(new[] { SegmentState.Complete, SegmentState.Complete, SegmentState.Downloading, SegmentState.Downloading, SegmentState.Waiting, SegmentState.Waiting }), string.Join(",", running));
        var paused = SegmentMap.Classify(map, nameof(DownloadStatus.Paused), 0);
        T.Check("paused: nothing is Downloading, untouched parts are Unassigned", paused[2] == SegmentState.Waiting && paused[3] == SegmentState.Waiting && paused[4] == SegmentState.Unassigned && paused[0] == SegmentState.Complete);
        var failed = SegmentMap.Classify(map, nameof(DownloadStatus.Failed), 0);
        T.Check("failed: everything that is not finished is shown as Error", failed.Count(s => s == SegmentState.Error) == 4 && failed[0] == SegmentState.Complete);
        T.Check("queued: parts are Waiting for a slot", SegmentMap.Classify(new[] { 0.0, 0.0 }, nameof(DownloadStatus.Queued), 0).All(s => s == SegmentState.Waiting));
        T.Check("a download without connections yet has Unassigned parts", SegmentMap.Classify(new[] { 0.0 }, nameof(DownloadStatus.Downloading), 0)[0] == SegmentState.Unassigned);
        T.Check("complete: everything is Complete", SegmentMap.Classify(new[] { 0.3, 0.0 }, nameof(DownloadStatus.Complete), 0).All(s => s == SegmentState.Complete));
        T.Check("bad numbers (NaN, below 0, above 1) never crash", SegmentMap.Classify(new[] { double.NaN, -1, 7 }, nameof(DownloadStatus.Downloading), 2) is { Length: 3 } odd && odd[2] == SegmentState.Complete);
        var counts = SegmentMap.Count(running);
        T.Check("legend counts add up to the number of parts", counts.Complete == 2 && counts.Downloading == 2 && counts.Waiting == 2 && counts.Error == 0 && counts.Unassigned == 0 && counts.Total == 6);
    }

    // ---------------------------------------------------------------- logs and SHA-256

    static void Logs()
    {
        var log = new ProgressLog();
        var t = new DateTime(2026, 10, 24, 9, 5, 7);
        var first = log.Observe(t, "Downloading", null, 8, 0);
        T.Check("first look: status and connections are logged", first.Count == 2 && first[0] == "09:05:07  Status: Downloading" && first[1] == "09:05:07  Connections: 8", string.Join("|", first));
        T.Check("nothing changed = nothing logged", log.Observe(t.AddSeconds(1), "Downloading", null, 8, 0).Count == 0);
        T.Check("connection changes are logged with both numbers", log.Observe(t.AddSeconds(2), "Downloading", null, 4, 0) is { Count: 1 } c && c[0].EndsWith("Connections: 8 → 4"));
        var retry = log.Observe(t.AddSeconds(3), "Downloading", "Retrying in 4 s (1/6): 503", 4, 1);
        T.Check("a retry and its message are logged once", retry.Count == 2 && retry[0].EndsWith("Retry 1") && retry[1].EndsWith("Retrying in 4 s (1/6): 503") && log.Observe(t.AddSeconds(4), "Downloading", "Retrying in 4 s (1/6): 503", 4, 1).Count == 0);
        T.Check("a status change is logged", log.Observe(t.AddSeconds(5), "Paused", null, 0, 1).Any(l => l.EndsWith("Status: Paused")));
        var small = new ProgressLog(10);
        for (var i = 0; i < 40; i++) small.Add(t, "line " + i);
        T.Check("the log keeps only the newest lines", small.Lines.Count == 10 && small.Lines[^1].EndsWith("line 39") && small.Lines[0].EndsWith("line 30"));
    }

    static void Verification()
    {
        T.Check("SHA-256 line is a dash when no checksum was given", VerificationText.For(new DownloadItem { Url = "https://e/x" }) == "—");
        var hash = new string('a', 32) + new string('b', 32);
        var pending = VerificationText.For(new DownloadItem { Url = "https://e/x", ExpectedSha256 = hash });
        T.Check("with a checksum the line says it is checked when the download finishes (shortened)", pending.Contains("when finished") && pending.Contains("aaaaaaaa…bbbbbb") && !pending.Contains(hash));
        T.Check("and afterwards that it was checked", VerificationText.For(new DownloadItem { Url = "https://e/x", ExpectedSha256 = hash, Status = nameof(DownloadStatus.Complete) }).StartsWith("checked against"));
    }

    // ---------------------------------------------------------------- Change save folder

    static void ChangeFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "makan-changefolder-" + Guid.NewGuid().ToString("N")[..8]);
        var from = Path.Combine(root, "from"); var to = Path.Combine(root, "to");
        Directory.CreateDirectory(from);
        try
        {
            using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
            DownloadItem Make(string name, string status = nameof(DownloadStatus.Paused)) => new() { Url = "https://example.com/" + name, FilePath = Path.Combine(from, name), Status = status };

            var ok = Make("movie.mkv");
            T.Check("a stopped download with no data moves to the new folder", m.ChangeSaveFolder(ok, to) == null && ok.FilePath == Path.Combine(to, "movie.mkv") && Directory.Exists(to), ok.FilePath);
            T.Check("the category follows the new name", ok.Category == CategoryService.For(ok.FilePath));

            File.WriteAllText(Path.Combine(to, "clash.zip"), "x");
            var clash = Make("clash.zip");
            T.Check("an existing file with the same name is never overwritten (a numbered name is used)", m.ChangeSaveFolder(clash, to) == null && Path.GetFileName(clash.FilePath) == "clash (1).zip", clash.FilePath);

            var running = Make("run.bin", nameof(DownloadStatus.Downloading));
            T.Check("refused while the download runs", m.ChangeSaveFolder(running, to) is { Length: > 0 } && running.FilePath == Path.Combine(from, "run.bin"));
            var queued = Make("q.bin", nameof(DownloadStatus.Queued));
            T.Check("refused while it waits in the queue", m.ChangeSaveFolder(queued, to) is { Length: > 0 });
            var finished = Make("done.bin", nameof(DownloadStatus.Complete));
            T.Check("refused when it is already finished", m.ChangeSaveFolder(finished, to) is { Length: > 0 });

            var partial = Make("part.bin"); partial.DoneBytes = 5_000_000;
            T.Check("refused once data has been fetched (the partial data lives beside the file name)", m.ChangeSaveFolder(partial, to) is { Length: > 0 } && partial.FilePath == Path.Combine(from, "part.bin"));
            var onDisk = Make("disk.bin"); File.WriteAllText(onDisk.FilePath + ".part", "x");
            T.Check("refused when partial files exist on disk", m.ChangeSaveFolder(onDisk, to) is { Length: > 0 } && onDisk.FilePath == Path.Combine(from, "disk.bin"));

            T.Check("an empty folder name is refused", m.ChangeSaveFolder(Make("e.bin"), "   ") is { Length: > 0 });
            var bad = Make("bad.bin");
            T.Check("an impossible folder name gives a message instead of an exception", m.ChangeSaveFolder(bad, "bad\0folder") is { Length: > 0 } && bad.FilePath == Path.Combine(from, "bad.bin"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---------------------------------------------------------------- live (needs the test server)

    public static async Task RunLive(string baseUrl, string dir, HttpClient http)
    {
        Console.WriteLine("\n== Progress window: segment counts and map states of a live segmented download");
        await http.GetAsync(baseUrl + "/__reset");
        try
        {
            var sub = Path.Combine(dir, "pi" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(sub);
            using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
            var item = new DownloadItem { Url = baseUrl + "/slow.bin", FilePath = Path.Combine(sub, "seg.bin"), Connections = 4 };
            T.Check("no segment map before the download starts", m.GetSegmentCounts(item) == (0, 0));
            m.Enqueue(item);
            var sw = Stopwatch.StartNew(); while (item.DoneBytes < 6_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(40);
            var (total, complete) = m.GetSegmentCounts(item);
            T.Check("a running segmented download reports its parts (some may be finished, not all)", total > 0 && complete >= 0 && complete <= total, $"{complete}/{total}");
            var states = SegmentMap.Classify(m.GetPositionMap(item, 100), item.Status, item.ActiveConnections);
            var counts = SegmentMap.Count(states);
            T.Check("the live map has 100 cells, some finished and some still to do", states.Length == 100 && counts.Total == 100 && counts.Complete > 0 && counts.Complete < 100, $"{counts}");
            var shares = NetworkBreakdown.FromConnections(m.GetConnections(item));
            T.Check("the live connections form one network row that carries 100 %", shares.Count == 1 && Math.Abs(shares[0].Percent - 100) < 0.001 && shares[0].Connections == 4, string.Join(",", shares.Select(s => $"{s.Network}:{s.Connections}")));
            var history = new SpeedHistory(); history.Add(DateTime.UtcNow, item.SpeedBytesPerSec);
            T.Check("the live speed can be recorded for the graph", history.SampleCount == 1);
            m.Cancel(item); await Program.WaitStatusPublic(item, DownloadStatus.Cancelled, 15);
            T.Check("after the download ended there are no segments to show", m.GetSegmentCounts(item) == (0, 0));
        }
        catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }
}
