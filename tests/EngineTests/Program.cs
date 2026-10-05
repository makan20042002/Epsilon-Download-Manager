using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

static class T
{
    public static int Pass, Fail;
    public static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { Pass++; Console.WriteLine($"  PASS  {name}"); }
        else
        {
            Fail++;
            Console.WriteLine($"  FAIL  {name}  {detail}");
            // Public GitHub Actions pages hide raw logs from signed-out visitors. Emit the exact
            // failed assertion as an annotation so intermittent Windows failures remain diagnosable.
            if (string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            {
                static string Escape(string value) => value.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
                Console.WriteLine($"::error title=Engine test failed::{Escape(name + (string.IsNullOrWhiteSpace(detail) ? "" : " — " + detail))}");
            }
        }
    }
    public static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
}

public static class Program
{
    static string Base = "http://127.0.0.1:18080";
    static readonly HttpClient Http = new();
    static string Dir = "";
    static Dictionary<string, string> Sha = new();

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0) Base = args[0];
        Dir = Path.Combine(Path.GetTempPath(), "makan-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        if (Environment.GetEnvironmentVariable("MAKAN_TESTS") == "resume")
        {
            var resumeStats = await Stats(); Sha = resumeStats.sha;
            await Run("Pause then resume continues from disk and reports the recovered bytes", PauseResume);
            Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
            try { Directory.Delete(Dir, true); } catch { }
            return T.Fail == 0 ? 0 : 1;
        }
        if (Environment.GetEnvironmentVariable("MAKAN_TESTS") == "torrent")      // only the BitTorrent suite (needs no web server)
        {
            await TorrentTests.RunAll(Dir);
            Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
            return T.Fail == 0 ? 0 : 1;
        }
        if (Environment.GetEnvironmentVariable("MAKAN_TESTS") == "hls")
        {
            await HlsTests.RunAll(Base, Dir, Http);
            Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
            try { Directory.Delete(Dir, true); } catch { }
            return T.Fail == 0 ? 0 : 1;
        }
        if (Environment.GetEnvironmentVariable("MAKAN_TESTS") == "perf" || (args.Length > 0 && args[0] == "-perf"))
        {
            await TorrentPerformanceTests.RunAllBenchmarks();
            return 0;
        }
        var stats = await Stats();
        Sha = stats.sha;

        await Run("Segmented download (40 MB, 8 connections) is byte-exact", SegmentedExact);
        await Run("Segments leave no merge step / temp files behind", NoLeftovers);
        await Run("Server without Range support falls back to single stream", NoRange);
        await Run("Content-Disposition names the file (incl. UTF-8 and redirects)", ContentDisposition);
        await Run("Cookies are sent; 403 fails fast with a clear error and no retries", CookiesAndErrors);
        await Run("Pause then resume continues from the saved offset", PauseResume);
        await Run("Cancel really ends as Cancelled and deletes partial files", CancelWorks);
        await Run("Dropped connections mid-body are retried and finish byte-exact", Flaky);
        await Run("HTTP 429 with Retry-After is honoured", Throttle);
        await Run("App restart mid-download resumes from disk instead of starting over", RestartResume);
        await Run("Two downloads with the same name never share a file", SameName);
        await Run("Probe: HTML page detected, sizes and range support reported", ProbeInfo);
        await Run("Completed item is Complete in the shared item (UI sees it)", SharedState);

        await Extra.RunAll(Base, Dir, Http);
        Console.WriteLine("\n== V12 feature tests");
        try { V12FeatureTests.Run(); T.Check("V12 feature suite", true); } catch (Exception ex) { T.Check("V12 feature suite", false, ex.ToString()); }

        Console.WriteLine($"\n{T.Pass} passed, {T.Fail} failed");
        try { Directory.Delete(Dir, true); } catch { }
        return T.Fail == 0 ? 0 : 1;
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); }
        catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static async Task<(Dictionary<string, int> hits, long bytes, Dictionary<string, string> sha)> Stats()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        var r = doc.RootElement;
        return (r.GetProperty("hits").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
                r.GetProperty("bytes").GetInt64(),
                r.GetProperty("sha").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
    }

    static string Sub() { var d = Path.Combine(Dir, Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    static DownloadItem New(string path, string dir, int conns = 8, bool auto = false) =>
        new() { Url = Base + path, FilePath = Path.Combine(dir, Path.GetFileName(path.Split('?')[0]) is { Length: > 0 } n ? n : "file.bin"), Connections = conns, AutoName = auto };

    public static Task<bool> WaitStatusPublic(DownloadItem item, DownloadStatus s, int seconds = 60) => WaitStatus(item, s, seconds);
    public static async Task<bool> WaitStatus(DownloadItem item, DownloadStatus s, int seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (item.Status == s.ToString()) return true;
            if (s != DownloadStatus.Failed && item.Status == DownloadStatus.Failed.ToString()) { await Task.Delay(200); return item.Status == s.ToString(); }
            await Task.Delay(50);
        }
        return false;
    }

    // ------------------------------------------------------------------ tests

    static async Task SegmentedExact()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = New("/big.bin", dir); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        T.Check("completes", await WaitStatus(item, DownloadStatus.Complete), item.LastError);
        T.Check("sha256 matches source", File.Exists(item.FilePath) && T.Sha(item.FilePath) == Sha["/big.bin"]);
        T.Check("used multiple connections", item.ActiveConnections == 0 && item.StatusCode == 206, $"status={item.StatusCode}");
        Console.WriteLine($"        ({sw.Elapsed.TotalSeconds:0.0}s for 40 MB)");
    }

    static async Task NoLeftovers()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = New("/big.bin", dir); m.Enqueue(item);
        await WaitStatus(item, DownloadStatus.Complete);
        var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
        T.Check("only the final file remains", files.Length == 1 && files[0] == "big.bin", string.Join(",", files));
    }

    static async Task NoRange()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = New("/norange.bin", dir); m.Enqueue(item);
        T.Check("completes", await WaitStatus(item, DownloadStatus.Complete), item.LastError);
        T.Check("sha256 matches", T.Sha(item.FilePath) == Sha["/mid.bin"]);
    }

    static async Task ContentDisposition()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var a = new DownloadItem { Url = Base + "/cd", FilePath = Path.Combine(dir, "cd"), AutoName = true };
        var b = new DownloadItem { Url = Base + "/cd-utf8", FilePath = Path.Combine(dir, "cd-utf8"), AutoName = true };
        var c = new DownloadItem { Url = Base + "/redir", FilePath = Path.Combine(dir, "redir"), AutoName = true };
        var d = new DownloadItem { Url = Base + "/cd", FilePath = Path.Combine(dir, "keep-my-name.pdf"), AutoName = false };
        foreach (var i in new[] { a, b, c, d }) m.Enqueue(i);
        foreach (var i in new[] { a, b, c, d }) await WaitStatus(i, DownloadStatus.Complete);
        T.Check("Content-Disposition filename", a.FileName == "Quarterly Report (final).pdf", a.FileName);
        T.Check("RFC 5987 UTF-8 filename", b.FileName == "résumé.pdf", b.FileName);
        T.Check("name taken from redirect target", c.FileName == "moved-name.zip", c.FileName);
        T.Check("user-chosen name is never overridden", d.FileName == "keep-my-name.pdf", d.FileName);
        T.Check("category follows the new name", a.Category == "Documents" && c.Category == "Compressed", $"{a.Category}/{c.Category}");
        T.Check("files exist under their new names", File.Exists(a.FilePath) && File.Exists(c.FilePath));
    }

    static async Task CookiesAndErrors()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var noCookie = new DownloadItem { Url = Base + "/needs-cookie", FilePath = Path.Combine(dir, "a.bin") };
        var cookie = new DownloadItem { Url = Base + "/needs-cookie", FilePath = Path.Combine(dir, "b.bin"), Cookie = "session=abc" };
        var gone = new DownloadItem { Url = Base + "/notfound", FilePath = Path.Combine(dir, "c.bin") };
        foreach (var i in new[] { noCookie, cookie, gone }) m.Enqueue(i);
        T.Check("cookie download completes", await WaitStatus(cookie, DownloadStatus.Complete), cookie.LastError);
        T.Check("missing cookie => Failed", await WaitStatus(noCookie, DownloadStatus.Failed, 15), noCookie.Status);
        T.Check("error message names the HTTP status", (noCookie.LastError ?? "").Contains("403"), noCookie.LastError);
        T.Check("404 => Failed", await WaitStatus(gone, DownloadStatus.Failed, 15), gone.Status);
        var hits = (await Stats()).hits;
        T.Check("404 not retried (probe only)", hits.GetValueOrDefault("/notfound") <= 2, $"hits={hits.GetValueOrDefault("/notfound")}");
    }

    static async Task PauseResume()
    {
        var dir = Sub(); var store = new MemoryStore(); using var m = new DownloadManager(store);
        var item = New("/slow.bin", dir, 4); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 6_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m.Pause(item);
        T.Check("becomes Paused", await WaitStatus(item, DownloadStatus.Paused, 10), item.Status);
        var doneAtPause = item.DoneBytes;
        T.Check("progress preserved while paused", doneAtPause > 0, $"{doneAtPause}");
        await Http.GetAsync(Base + "/__reset");
        m.Enqueue(item);
        sw.Restart(); while (item.DiskLoadedBytes == 0 && item.Status != "Complete" && sw.Elapsed.TotalSeconds < 10) await Task.Delay(20);
        T.Check("resume reports the bytes loaded from disk", item.DiskLoadedBytes >= doneAtPause * 0.8, $"disk={item.DiskLoadedBytes}, paused={doneAtPause}");
        T.Check("resumes to Complete", await WaitStatus(item, DownloadStatus.Complete, 90), item.LastError);
        T.Check("sha256 matches after pause/resume", T.Sha(item.FilePath) == Sha["/big.bin"]);
        var s = await Stats();
        var savedFraction = 1.0 - (double)s.bytes / (40 * 1024 * 1024);
        T.Check("resume did not re-download everything", s.bytes < 40L * 1024 * 1024 * 0.95, $"fetched {s.bytes / 1e6:0.0} MB after resume; {savedFraction:P0} saved");
    }

    static async Task CancelWorks()
    {
        var dir = Sub(); var store = new MemoryStore(); using var m = new DownloadManager(store);
        var item = New("/slow.bin", dir, 4); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 3_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m.Cancel(item);
        T.Check("status ends Cancelled (not Paused)", await WaitStatus(item, DownloadStatus.Cancelled, 10), item.Status);
        await Task.Delay(300);
        T.Check("status stays Cancelled", item.Status == "Cancelled", item.Status);
        T.Check("partial files deleted", Directory.GetFiles(dir).Length == 0, string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));
        T.Check("database row says Cancelled", store.Row(item.Id)?.Status == "Cancelled", store.Row(item.Id)?.Status);
    }

    static async Task Flaky()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = New("/flaky.bin", dir, 4); m.Enqueue(item);
        T.Check("completes despite dropped connections", await WaitStatus(item, DownloadStatus.Complete, 90), item.LastError);
        T.Check("sha256 matches", File.Exists(item.FilePath) && T.Sha(item.FilePath) == Sha["/mid.bin"]);
    }

    static async Task Throttle()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = New("/throttle.bin", dir, 4); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        T.Check("completes after 429s", await WaitStatus(item, DownloadStatus.Complete, 60), item.LastError);
        T.Check("waited for Retry-After (>= ~1s)", sw.Elapsed.TotalSeconds >= 0.9, $"{sw.Elapsed.TotalSeconds:0.0}s");
        T.Check("sha256 matches", File.Exists(item.FilePath) && T.Sha(item.FilePath) == Sha["/mid.bin"]);
    }

    static async Task RestartResume()
    {
        var dir = Sub(); var store = new MemoryStore();
        var m1 = new DownloadManager(store);
        var item = New("/slow.bin", dir, 4); m1.Enqueue(item);
        var id = item.Id;
        var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 8_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m1.Dispose();   // "app closed"
        var row = store.Row(id)!;
        T.Check("shutdown stored it as Queued (auto-resume)", row.Status == "Queued", row.Status);
        T.Check("progress persisted", row.DoneBytes > 0, $"{row.DoneBytes}");
        await Http.GetAsync(Base + "/__reset");
        using var m2 = new DownloadManager(store);   // "app reopened"
        var again = m2.Items.Single(x => x.Id == id);
        T.Check("completes after restart", await WaitStatus(again, DownloadStatus.Complete, 90), again.LastError);
        T.Check("sha256 matches", T.Sha(again.FilePath) == Sha["/big.bin"]);
        var s = await Stats();
        T.Check("only the remainder was fetched", s.bytes < 40L * 1024 * 1024 * 0.9, $"fetched {s.bytes / 1e6:0.0} MB after restart");
    }

    static async Task SameName()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var a = new DownloadItem { Url = Base + "/small.bin", FilePath = Path.Combine(dir, "same.bin") };
        var b = new DownloadItem { Url = Base + "/mid.bin", FilePath = Path.Combine(dir, "same.bin") };
        m.Enqueue(a); m.Enqueue(b);
        T.Check("different target paths", a.FilePath != b.FilePath, $"{a.FileName} / {b.FileName}");
        await WaitStatus(a, DownloadStatus.Complete); await WaitStatus(b, DownloadStatus.Complete);
        T.Check("both intact", T.Sha(a.FilePath) == Sha["/small.bin"] && T.Sha(b.FilePath) == Sha["/mid.bin"]);
    }

    static async Task ProbeInfo()
    {
        using var m = new DownloadManager(new MemoryStore());
        var html = await m.ProbeAsync(new DownloadItem { Url = Base + "/html" }, CancellationToken.None);
        T.Check("HTML content type visible to callers", html.ContentType == "text/html", html.ContentType);
        var big = await m.ProbeAsync(new DownloadItem { Url = Base + "/big.bin" }, CancellationToken.None);
        T.Check("length + range support", big.Length == 40 * 1024 * 1024 && big.AcceptRanges, $"{big.Length} {big.AcceptRanges}");
        var nr = await m.ProbeAsync(new DownloadItem { Url = Base + "/norange.bin" }, CancellationToken.None);
        T.Check("no-range server reported honestly", !nr.AcceptRanges && nr.Length == 6 * 1024 * 1024, $"{nr.Length} {nr.AcceptRanges}");
        try { await m.ProbeAsync(new DownloadItem { Url = Base + "/forbidden" }, CancellationToken.None); T.Check("403 throws", false); }
        catch (HttpRequestException ex) { T.Check("403 throws with status", ex.StatusCode == System.Net.HttpStatusCode.Forbidden); }
    }

    static async Task SharedState()
    {
        var dir = Sub(); var store = new MemoryStore(); using var m = new DownloadManager(store);
        var seen = new List<string>();
        var item = New("/mid.bin", dir); item.PropertyChanged += (_, e) => { if (e.PropertyName == "Status") seen.Add(item.Status); };
        m.Enqueue(item);
        await WaitStatus(item, DownloadStatus.Complete);
        T.Check("UI-bound item reaches Complete with 100%", item.Status == "Complete" && item.Progress == 100, $"{item.Status} {item.Progress}");
        T.Check("item raised change notifications for Status", seen.Contains("Downloading") && seen.Contains("Complete"), string.Join(",", seen));
        T.Check("history recorded", store.History.Contains($"{item.Id}:Complete"));
    }
}
