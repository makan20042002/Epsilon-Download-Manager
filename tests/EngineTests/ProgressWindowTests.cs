using System.Diagnostics;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>What the IDM-style progress window needs from the engine: per-connection rows, the position map, resume support, live speed limit.</summary>
static class ProgressWindowTests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("Progress window data: connections, position map, resume support, live speed limit, temporary limit", Data);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static async Task Data()
    {
        var dir = Path.Combine(Dir, "pw" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);

        // ---- several connections
        var item = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "multi.bin"), Connections = 4 };
        m.Enqueue(item);
        var sw = Stopwatch.StartNew(); while (item.DoneBytes < 6_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(40);
        var rows = m.GetConnections(item);
        T.Check("one row per connection, numbered from 1", rows.Count == 4 && rows.Select(r => r.Index).SequenceEqual(new[] { 1, 2, 3, 4 }), string.Join(",", rows.Select(r => r.Index)));
        T.Check("each connection reports what it fetched", rows.All(r => r.Downloaded > 0) && Math.Abs(rows.Sum(r => r.Downloaded) - item.DoneBytes) < 8_000_000, string.Join(",", rows.Select(r => r.Downloaded)));
        T.Check("and what it is doing (IDM's wording)", rows.All(r => new[] { "Send GET...", "Receiving data...", "Disconnect.", "Download complete." }.Contains(r.Info)) && rows.Any(r => r.Info == "Receiving data..."), string.Join(",", rows.Select(r => r.Info)));
        var map = m.GetPositionMap(item, 50);
        T.Check("position map: 50 buckets in 0..1, more done than not started, not everything done", map.Length == 50 && map.All(x => x is >= 0 and <= 1) && map.Sum() > 1 && map.Sum() < 50 && map.Any(x => x > 0.99), $"sum={map.Sum():0.0}");
        T.Check("resume is supported by this server", m.SupportsResume(item) == true);
        m.Cancel(item); await Program.WaitStatusPublic(item, DownloadStatus.Cancelled, 15);
        T.Check("after the download ended there is nothing to show", m.GetConnections(item).Count == 0 && m.SupportsResume(item) == null);

        // ---- a server without ranges: one connection, no resume
        var single = new DownloadItem { Url = Base + "/norange-slow.bin", FilePath = Path.Combine(dir, "single.bin"), Connections = 4 };
        m.Enqueue(single);
        var s1 = Stopwatch.StartNew(); bool? resume = null; IReadOnlyList<ConnectionInfo> one = Array.Empty<ConnectionInfo>();
        while (s1.Elapsed.TotalSeconds < 15 && (one.Count == 0 || resume == null || single.DoneBytes < 500_000)) { resume ??= m.SupportsResume(single); if (m.GetConnections(single).Count > 0) one = m.GetConnections(single); await Task.Delay(20); }
        T.Check("single connection: one row that is receiving, resume is not supported", one.Count == 1 && resume == false && one[0].Info == "Receiving data...", $"{one.Count} rows, resume={resume}, {(one.Count > 0 ? one[0].Info : "")}");
        var idle = m.GetPositionMap(single, 10);
        T.Check("without a chunk map the position map is a simple bar", idle.Length == 10 && idle.All(x => x is >= 0 and <= 1) && idle.Sum() > 0 && idle.Sum() < 10, $"sum={idle.Sum():0.00}");
        m.Cancel(single); await Program.WaitStatusPublic(single, DownloadStatus.Cancelled, 15);

        // ---- speed limit changed while it runs
        var fast = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "limited.bin"), Connections = 2 };
        m.Enqueue(fast);
        var s2 = Stopwatch.StartNew(); while (fast.DoneBytes < 3_000_000 && s2.Elapsed.TotalSeconds < 30) await Task.Delay(20);
        var at = fast.DoneBytes;
        fast.SpeedLimitBytesPerSec = 400 * 1024; fast.LimitIsTemporary = true;          // like ticking "Use Speed Limiter" in the window
        await Task.Delay(300);
        var from = fast.DoneBytes; await Task.Delay(2000);
        var rate = (fast.DoneBytes - from) / 2.0;
        T.Check("the new limit is obeyed at once (about 400 KB/s, not the megabytes per second before)", rate < 1_000_000 && rate > 100_000, $"{rate / 1024:0} KB/s (was at {at / 1024} KB)");
        m.Pause(fast); T.Check("stopped", await Program.WaitStatusPublic(fast, DownloadStatus.Paused, 15));
        T.Check("a limit set for this run only is dropped on stop (Remember was not ticked)", fast.SpeedLimitBytesPerSec == 0 && !fast.LimitIsTemporary);
        var kept = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "kept.bin"), Connections = 2, SpeedLimitBytesPerSec = 500 * 1024, LimitIsTemporary = false };
        m.Enqueue(kept); await Task.Delay(800); m.Pause(kept); await Program.WaitStatusPublic(kept, DownloadStatus.Paused, 15);
        T.Check("a remembered limit survives stop / resume", kept.SpeedLimitBytesPerSec == 500 * 1024);
        m.Cancel(fast); m.Cancel(kept);
    }
}
