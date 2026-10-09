using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;

/// <summary>Browser bridge, native-host relay and media (HLS / ffmpeg) tests.</summary>
static class Extra
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("Redirects: cookies never follow a redirect to another site", RedirectCookies);
        await Run("Browser bridge: ping / download / hint names / auto names", Bridge);
        await Run("Browser bridge: login pages, 403, duplicates, batch, bad input", BridgeEdges);
        await Run("Ask before start: nothing begins by itself (Later, restart, prompts, batches)", AskFirst);
        await Run("Browser bridge: capture rules (file types, sites, browsers) and language", BridgeCapture);
        await Run("Browser bridge: 'download all links' picker request", BridgeLinks);
        await Run("Browser bridge: stream list for the video menu, stream download, save-as prompt hook", BridgeStreams);
        await Run("Native host relay (real MakanNativeHost process) -> bridge -> engine", HostRelay);
        await Run("Browser bridge: magnet links are accepted (a plain HTTP-only URL is not), .torrent addresses too", BridgeMagnet);
        await Run("HLS: quoted CODECS commas, separate audio rendition, cookie+referer sent", Hls);
        await Run("ffmpeg runner: no pipe deadlock, one -headers, audio mapped, PATH lookup", Ffmpeg);
        await Run("Batch wildcards and text lists (IDM batch download, export/import as .txt)", UrlTextTests);
        await Run("Play while downloading: the safe (gap-free) prefix, and the temporary preview copy", Streaming);
        await Run("Remote status server: token required, status JSON, add-by-URL and add-by-magnet, duplicates", RemoteServer);
        await Run("NetworkBoost: pause/resume bookkeeping (needs no real Windows service or admin rights to test)", NetworkBoostTests);
        await Run("Proxy: mode switching, per-protocol filtering, and credentials for regular HTTP(S) downloads", ProxyTests);
        await Run("Multi-Network: connections spread over several networks, a dead network is dropped, off by default", MultiNetwork);
        await Run("No network at all: the download waits instead of using up its retries", WaitsForNetwork);
        await HlsTests.RunAll(Base, Dir, Http);
        await QueueTests.RunAll(Base, Dir, Http);
        await SettingsTests.RunAll(Base, Dir, Http);
        await YtDlpTests.RunAll(Base, Dir, Http);
        await ProgressWindowTests.RunAll(Base, Dir, Http);
        await V15Tests.RunAll(Base, Dir, Http);
        await TorrentTests.RunAll(Dir);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); }
        catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static string Sub() { var d = Path.Combine(Dir, "x" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    static async Task RedirectCookies()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var cross = new DownloadItem { Url = Base + "/xhost", FilePath = Path.Combine(dir, "cross.bin"), Cookie = "session=abc", Connections = 4, AutoName = true };
        var same = new DownloadItem { Url = Base + "/samehost", FilePath = Path.Combine(dir, "same.bin"), Cookie = "session=abc", Connections = 4, AutoName = true };
        m.Enqueue(cross); m.Enqueue(same);
        T.Check("cross-host redirect still downloads", await Program_WaitStatus(cross, DownloadStatus.Complete), cross.LastError);
        T.Check("same-host redirect downloads", await Program_WaitStatus(same, DownloadStatus.Complete), same.LastError);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        var seen = doc.RootElement.GetProperty("cookie_at");
        T.Check("cookie NOT sent to the other host after redirect", seen.TryGetProperty("/landing-x", out var x) && x.GetString() == "", seen.ToString());
        T.Check("cookie kept when the redirect stays on the same site", seen.TryGetProperty("/landing-s", out var y) && (y.GetString() ?? "").Contains("session=abc"), seen.ToString());
    }

    // ---- bridge plumbing -----------------------------------------------------------------------------------------

    static async Task<JsonElement> Ask(string json)
    {
        using var pipe = new NamedPipeClientStream(".", NativeBridge.EffectivePipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        var bytes = new UTF8Encoding(false).GetBytes(json + "\n");
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
        var line = await new StreamReader(pipe).ReadLineAsync();
        using var doc = JsonDocument.Parse(line ?? "{}");
        return doc.RootElement.Clone();
    }

    static bool Ok(JsonElement e) => e.TryGetProperty("ok", out var v) && v.GetBoolean();
    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    static async Task Bridge()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        using var bridge = new NativeBridge(manager, () => dir);

        var ping = await Ask("{\"kind\":\"ping\"}");
        T.Check("ping answers ok and reports version", Ok(ping) && Str(ping, "version").Length > 0);

        // The browser's suggested path is reduced to a file name inside OUR download folder (no path traversal).
        var hinted = await Ask($"{{\"url\":\"{Base}/small.bin\",\"filePath\":\"C:\\\\Users\\\\someone\\\\Downloads\\\\..\\\\hint name.bin\"}}");
        T.Check("download accepted", Ok(hinted), Str(hinted, "error"));
        var path = Str(hinted, "filePath");
        T.Check("browser name hint used, stays inside the download folder", Path.GetDirectoryName(path) == dir && Path.GetFileName(path) == "hint name.bin", path);
        var item = manager.Items.First(x => x.Id == hinted.GetProperty("id").GetInt64());
        T.Check("hinted download completes byte-exact", await Program_WaitStatus(item, DownloadStatus.Complete) && File.Exists(item.FilePath), item.LastError);

        // No hint => the real server name (Content-Disposition) is adopted once the transfer starts.
        var auto = await Ask($"{{\"url\":\"{Base}/cd\"}}");
        T.Check("download without hint accepted", Ok(auto), Str(auto, "error"));
        var autoItem = manager.Items.First(x => x.Id == auto.GetProperty("id").GetInt64());
        T.Check("completes", await Program_WaitStatus(autoItem, DownloadStatus.Complete), autoItem.LastError);
        T.Check("file renamed to the server-suggested name", autoItem.FileName == "Quarterly Report (final).pdf", autoItem.FileName);

        // Cookie + referrer travel with the job so logged-in downloads work.
        var cookie = await Ask($"{{\"url\":\"{Base}/needs-cookie\",\"cookie\":\"session=abc\",\"referrer\":\"https://site.example/\"}}");
        T.Check("cookie-protected link accepted when cookie supplied", Ok(cookie), Str(cookie, "error"));
    }

    static async Task BridgeEdges()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        using var bridge = new NativeBridge(manager, () => dir);

        var html = await Ask($"{{\"url\":\"{Base}/html\"}}");
        T.Check("login/HTML page is refused so the browser keeps its own download", !Ok(html) && Str(html, "error").Contains("web page"), Str(html, "error"));
        T.Check("nothing was queued for it", manager.Items.Count == 0);

        var forbidden = await Ask($"{{\"url\":\"{Base}/forbidden\"}}");
        T.Check("403 refused with a message", !Ok(forbidden) && Str(forbidden, "error").Length > 0, Str(forbidden, "error"));

        var first = await Ask($"{{\"url\":\"{Base}/slow.bin\"}}");
        var second = await Ask($"{{\"url\":\"{Base}/slow.bin\"}}");
        T.Check("same link twice is reported as duplicate", Ok(first) && Ok(second) && second.TryGetProperty("duplicate", out var d) && d.GetBoolean()
                && first.GetProperty("id").GetInt64() == second.GetProperty("id").GetInt64());
        T.Check("only one item exists", manager.Items.Count == 1);
        DownloadPrompt? duplicatePrompt = null;
        bridge.DuplicatePrompt = p => { duplicatePrompt = p; return true; };
        var third = await Ask($"{{\"url\":\"{Base}/slow.bin\",\"filePath\":\"copy.bin\"}}");
        T.Check("a browser duplicate is handed to the desktop so the user can choose another copy", Ok(third) && third.TryGetProperty("duplicate", out var dup) && dup.GetBoolean()
                && third.TryGetProperty("pending", out var pending) && pending.GetBoolean() && duplicatePrompt is { FileName: "copy.bin" } && manager.Items.Count == 1, third.ToString());
        foreach (var i in manager.Items) manager.Cancel(i);

        var batch = await Ask($"{{\"kind\":\"batch\",\"urls\":[\"{Base}/small.bin?a=1\",\"{Base}/small.bin?a=2\",\"{Base}/small.bin?a=2\",\"ftp://x/y\",\"javascript:alert(1)\"]}}");
        T.Check("batch adds only distinct http(s) links", Ok(batch) && batch.GetProperty("count").GetInt32() == 2, batch.ToString());

        var bad = await Ask("{\"url\":\"file:///c:/windows/system32/config/sam\"}");
        T.Check("non-http URL rejected", !Ok(bad));
        var junk = await Ask("this is not json");
        T.Check("garbage input answered with an error, bridge keeps running", !Ok(junk) && Ok(await Ask("{\"kind\":\"ping\"}")));

        var shown = new TaskCompletionSource<BridgeCommand>();
        bridge.UiCommand += c => shown.TrySetResult(c);
        await Ask($"{{\"kind\":\"media\",\"url\":\"{Base}/hls/plain.m3u8\",\"cookie\":\"a=b\"}}");
        var cmd = await Task.WhenAny(shown.Task, Task.Delay(3000)) == shown.Task ? shown.Task.Result : null;
        T.Check("media request reaches the UI with cookie", cmd is { Kind: "media", Cookie: "a=b" }, cmd?.ToString());
    }

    // ---- real native host process ----------------------------------------------------------------------------

    static async Task AskFirst()
    {
        var dir = Sub();
        // "Download later": added, but it must sit there until the user starts it.
        using (var m = new DownloadManager(new MemoryStore()))
        {
            var item = new DownloadItem { Url = Base + "/small.bin", FilePath = Path.Combine(dir, "later.bin"), Connections = 4 };
            m.Enqueue(item, start: false);
            await Task.Delay(2500);
            T.Check("added as Stopped and NOT downloading", item.Status == "Paused" && item.DoneBytes == 0 && !File.Exists(item.FilePath), item.Status);
            T.Check("the server saw no request", await HitsOf("/small.bin") == 0);
            m.Enqueue(item);   // the user presses Resume
            T.Check("Resume starts it and it completes", await Program_WaitStatus(item, DownloadStatus.Complete), item.LastError);
        }
        // Restart: with auto-resume off, unfinished downloads come back Stopped and stay that way.
        var store = new MemoryStore();
        var first = new DownloadManager(store, autoResumeUnfinished: false);
        var slow = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "slow.bin"), Connections = 4 };
        first.Enqueue(slow);
        var sw = Stopwatch.StartNew();
        while (slow.DoneBytes < 4_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        first.Dispose();
        T.Check("closing the app leaves it Stopped, not Queued", store.Row(slow.Id)!.Status == "Paused", store.Row(slow.Id)!.Status);
        await Http.GetAsync(Base + "/__reset");
        using var second = new DownloadManager(store, autoResumeUnfinished: false);
        var again = second.Items.Single();
        await Task.Delay(2500);
        T.Check("after restart it is Stopped and nothing was requested", again.Status == "Paused" && await HitsOf("/slow.bin") == 0, again.Status);
        second.Enqueue(again);
        T.Check("only Resume brings it back, and it finishes", await Program_WaitStatus(again, DownloadStatus.Complete), again.LastError);

        // Bridge prompts: the browser is told OK, the UI decides what happens.
        using var manager = new DownloadManager(new MemoryStore());
        using var bridge = new NativeBridge(manager, () => dir);
        DownloadPrompt? seen = null; BatchPrompt? batch = null;
        bridge.DownloadPrompt = p => { seen = p; return true; };
        bridge.BatchPrompt = b => { batch = b; return true; };
        var r = await Ask($"{{\"url\":\"{Base}/cd\",\"cookie\":\"a=b\",\"referrer\":\"https://x.example/\"}}");
        T.Check("browser gets ok/pending and nothing is queued", Ok(r) && r.TryGetProperty("pending", out var pend) && pend.GetBoolean() && manager.Items.Count == 0, r.ToString());
        T.Check("prompt carries size, the server's real name and type", seen is { Size: 307200, ProbedName: "Quarterly Report (final).pdf", Cookie: "a=b" }, seen?.ToString());
        var rb = await Ask($"{{\"kind\":\"batch\",\"urls\":[\"{Base}/small.bin?1\",\"{Base}/small.bin?2\",\"{Base}/small.bin?2\"],\"cookie\":\"c=d\"}}");
        T.Check("batch is handed over (deduplicated), nothing queued", Ok(rb) && batch is { Cookie: "c=d" } && batch.Urls.Count == 2 && manager.Items.Count == 0, batch?.ToString());

        bridge.DownloadPrompt = null; bridge.BatchPrompt = null; bridge.AskBeforeStart = () => true;
        var all = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/small.m3u8\",\"title\":\"NoDialog\",\"noPrompt\":true}}");
        var added = manager.Items.Single();
        await Task.Delay(1500);
        T.Check("'Download all' style items are added Stopped while asking is on", Ok(all) && added.Status == "Paused" && added.DoneBytes == 0, added.Status);
        bridge.AskBeforeStart = () => false;
        var direct = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/vod.m3u8\",\"title\":\"Go\",\"noPrompt\":true}}");
        var started = manager.Items.First(x => x.Url.EndsWith("vod.m3u8"));
        T.Check("with asking off they start", Ok(direct) && await Program_WaitStatus(started, DownloadStatus.Complete), started.LastError);
    }

    static async Task<int> HitsOf(string path)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        return doc.RootElement.GetProperty("hits").TryGetProperty(path, out var v) ? v.GetInt32() : 0;
    }

    static Task UrlTextTests()
    {
        var nums = UrlText.ExpandWildcard("http://x.example/pic*.jpg", WildcardKind.Numbers, 1, 3, 1, false);
        T.Check("numbers 1..3", nums.SequenceEqual(new[] { "http://x.example/pic1.jpg", "http://x.example/pic2.jpg", "http://x.example/pic3.jpg" }), string.Join(",", nums));
        var padded = UrlText.ExpandWildcard("http://x/a*.zip", WildcardKind.Numbers, 8, 12, 2, true);
        T.Check("zero fill uses the width of the largest number, step honoured", padded.SequenceEqual(new[] { "http://x/a08.zip", "http://x/a10.zip", "http://x/a12.zip" }), string.Join(",", padded));
        T.Check("every * gets the same value", UrlText.ExpandWildcard("http://x/*/part*.rar", WildcardKind.Numbers, 5, 5, 1, false).Single() == "http://x/5/part5.rar");
        var letters = UrlText.ExpandWildcard("http://x/*.txt", WildcardKind.Letters, 0, 0, 1, false, 'a', 'd');
        T.Check("letters a..d", letters.Count == 4 && letters[3] == "http://x/d.txt");
        T.Check("upper-case letters", UrlText.ExpandWildcard("http://x/*", WildcardKind.Letters, 0, 0, 1, false, 'A', 'C')[2] == "http://x/C");
        foreach (var bad in new Action[] {
            () => UrlText.ExpandWildcard("http://x/nothing", WildcardKind.Numbers, 1, 2, 1, false),
            () => UrlText.ExpandWildcard("http://x/*", WildcardKind.Numbers, 5, 1, 1, false),
            () => UrlText.ExpandWildcard("http://x/*", WildcardKind.Numbers, 1, 2, 0, false),
            () => UrlText.ExpandWildcard("http://x/*", WildcardKind.Numbers, 1, 100000, 1, false) })
        {
            var rejected = false; try { bad(); } catch (ArgumentException) { rejected = true; }
            T.Check("bad input is rejected with a message", rejected);
        }
        var found = UrlText.ExtractUrls("Links:\r\nhttp://a.example/x.zip, and (https://b.example/y?z=1).\nftp://no.example/f\nhttp://a.example/x.zip\r\n<https://c.example/p>");
        T.Check("addresses are found in free text, trimmed, deduplicated, http(s) only", found.SequenceEqual(new[] { "http://a.example/x.zip", "https://b.example/y?z=1", "https://c.example/p" }), string.Join(" | ", found));
        var text = UrlText.ToTextFile(new[] { "https://a.example/v.m3u8#makan-audio=zzz", "https://b.example/f.zip" });
        T.Check("text export: one address per line, internal details dropped", text == "https://a.example/v.m3u8\r\nhttps://b.example/f.zip\r\n", text.Replace("\r\n", "|"));
        T.Check("export then import round-trips", UrlText.ExtractUrls(text).Count == 2);
        return Task.CompletedTask;
    }

    static Task Streaming()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        const int chunkSize = 1024, chunkCount = 5;
        var total = (long)chunkSize * chunkCount;

        DownloadItem NewItem(string name) => new() { FilePath = Path.Combine(dir, name), TotalBytes = total, Connections = 4 };
        void WriteSegment(DownloadItem item, long[] done)
        {
            File.WriteAllBytes(item.FilePath + ".seg", new byte[total]);
            File.WriteAllText(item.FilePath + ".seg.map", JsonSerializer.Serialize(new { Total = total, ChunkSize = chunkSize, Done = done }));
        }

        var complete = NewItem("done.mp4"); WriteSegment(complete, new long[] { chunkSize, chunkSize, chunkSize, chunkSize, chunkSize });
        T.Check("every chunk complete: the whole file is the safe prefix", manager.SafeStreamablePrefixBytes(complete) == total);

        var gapInMiddle = NewItem("gap.mp4"); WriteSegment(gapInMiddle, new long[] { chunkSize, chunkSize, 200, chunkSize, chunkSize });
        T.Check("a gap partway through stops the safe prefix right there, even though later chunks are done", manager.SafeStreamablePrefixBytes(gapInMiddle) == chunkSize * 2);

        var startMissing = NewItem("start-missing.mp4"); WriteSegment(startMissing, new long[] { 0, chunkSize, chunkSize, chunkSize, chunkSize });
        T.Check("the very first chunk missing means nothing is safely playable, no matter what finished later", manager.SafeStreamablePrefixBytes(startMissing) == 0);

        var single = NewItem("single.mp3"); File.WriteAllBytes(single.FilePath + ".part", new byte[777]);
        T.Check("a single-connection (.part) download is safe up to exactly what has been written - it always fills in order", manager.SafeStreamablePrefixBytes(single) == 777);

        var finished = NewItem("finished.mp4"); finished.Status = nameof(DownloadStatus.Complete);
        T.Check("a completed item reports no preview prefix - the real, finished file should be opened instead", manager.SafeStreamablePrefixBytes(finished) == 0);

        T.Check("only recognised media extensions look streamable", DownloadManager.LooksStreamable(complete) && !DownloadManager.LooksStreamable(NewItem("archive.zip")));
        T.Check("a torrent's own file is never treated as a plain streamable download here", !DownloadManager.LooksStreamable(new DownloadItem { FilePath = Path.Combine(dir, "movie.mp4"), Url = "magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038" }));

        var copy = manager.PreparePlayableCopy(gapInMiddle, minimumBytes: 100);
        T.Check("the preview copy exists and is exactly the safe prefix length, not the full pre-allocated size", copy != null && new FileInfo(copy!).Length == chunkSize * 2);
        T.Check("the preview copy keeps the real extension, for the OS to recognise it as a video", copy!.EndsWith(".mp4"));

        T.Check("too little downloaded yet: no copy is made at all", manager.PreparePlayableCopy(startMissing, minimumBytes: 100) == null);
        return Task.CompletedTask;
    }

    static async Task MultiNetwork()
    {
        var dir = Sub();
        var a = new NetworkLink("test-a", "Ethernet", System.Net.IPAddress.Parse("127.0.0.1"));
        var b = new NetworkLink("test-b", "Wi-Fi", System.Net.IPAddress.Parse("127.0.0.2"));
        var dead = new NetworkLink("test-dead", "USB tethering", System.Net.IPAddress.Parse("203.0.113.77"));   // not an address of this machine: binding fails

        using (var m = new DownloadManager(new MemoryStore()) { LinkProbe = () => new[] { a, b } })
        {
            var off = new DownloadItem { Url = Base + "/big.bin", FilePath = Path.Combine(dir, "off.bin"), Connections = 4 };
            m.Enqueue(off);
            T.Check("switched off (the default) a download completes as before", await Program_WaitStatus(off, DownloadStatus.Complete), off.LastError);
            T.Check("...and uses no per-network bookkeeping at all", m.GetLinkUsage(off).Count == 0);
        }

        using (var m = new DownloadManager(new MemoryStore()) { MultiNetworkEnabled = true, LinkProbe = () => new[] { a, b } })
        {
            var item = new DownloadItem { Url = Base + "/big.bin", FilePath = Path.Combine(dir, "two.bin"), Connections = 4 };
            m.Enqueue(item);
            var usage = await WatchLinks(m, item);
            T.Check("with two networks the download completes", await Program_WaitStatus(item, DownloadStatus.Complete), item.LastError);
            T.Check("...byte-exact", File.Exists(item.FilePath) && T.Sha(item.FilePath) == await ShaOf("/big.bin"));
            T.Check("both networks carried part of the file", usage.Count == 2 && usage.All(u => u.Bytes > 0 && u.Active), string.Join(", ", usage.Select(u => u.Link.Name + "=" + u.Bytes)));
            T.Check("together they never carried more than the file", usage.Sum(u => u.Bytes) <= new FileInfo(item.FilePath).Length, usage.Sum(u => u.Bytes).ToString());
        }

        using (var m = new DownloadManager(new MemoryStore()) { MultiNetworkEnabled = true, LinkProbe = () => new[] { a, dead } })
        {
            var item = new DownloadItem { Url = Base + "/big.bin", FilePath = Path.Combine(dir, "dead.bin"), Connections = 4 };
            m.Enqueue(item);
            var usage = await WatchLinks(m, item);
            T.Check("a network that does not work never fails the download", await Program_WaitStatus(item, DownloadStatus.Complete), item.LastError);
            T.Check("...the file is still byte-exact", File.Exists(item.FilePath) && T.Sha(item.FilePath) == await ShaOf("/big.bin"));
            T.Check("the dead network is dropped and carried nothing", usage.Count == 2 && !usage[1].Active && usage[1].Bytes == 0 && usage[0].Active && usage[0].Bytes > 0);
            T.Check("no retry was counted against the download for it", item.RetryCount == 0, item.RetryCount.ToString());
        }

        // options: an unticked network is not used; small files do not use Multi-Network; connections follow adapter speed
        using (var m = new DownloadManager(new MemoryStore()) { MultiNetworkEnabled = true, LinkProbe = () => new[] { a, b }, MultiNetworkExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TEST-B" } })
        {
            T.Check("an unticked network is left out (and with one network left, Multi-Network does nothing)", m.UsableLinks().Count == 1 && m.UsableLinks()[0].Name == "test-a");
        }
        using (var m = new DownloadManager(new MemoryStore()) { MultiNetworkEnabled = true, LinkProbe = () => new[] { a, b }, MultiNetworkMinBytes = 1L << 40 })
        {
            var item = new DownloadItem { Url = Base + "/big.bin", FilePath = Path.Combine(dir, "small-rule.bin"), Connections = 4 };
            m.Enqueue(item);
            var usage = await WatchLinks(m, item);
            T.Check("a file below the minimum size downloads normally, without Multi-Network", await Program_WaitStatus(item, DownloadStatus.Complete) && usage.Count == 0, item.LastError);
        }
        var fast = new NetworkLink("fast", "Ethernet", System.Net.IPAddress.Loopback, 900_000_000); var slow = new NetworkLink("slow", "Wi-Fi", System.Net.IPAddress.Loopback, 100_000_000);
        var bySpeed = DownloadManager.AssignWorkers(new[] { fast, slow }, 8, bySpeed: true);
        T.Check("by speed: the faster network gets most connections, the slower one still gets some", bySpeed.Count(x => x == 0) >= 6 && bySpeed.Count(x => x == 1) >= 1 && bySpeed.Length == 8, string.Join("", bySpeed));
        var equal = DownloadManager.AssignWorkers(new[] { fast, slow }, 8, bySpeed: false);
        T.Check("equal: both networks get the same number", equal.Count(x => x == 0) == 4 && equal.Count(x => x == 1) == 4, string.Join("", equal));
        T.Check("unknown adapter speeds fall back to an equal share", DownloadManager.AssignWorkers(new[] { a, b }, 4, bySpeed: true).Count(x => x == 0) == 2);

        var metered = new NetworkLink("phone", "Mobile", System.Net.IPAddress.Parse("127.0.0.3"), IsMetered: true);
        using (var m = new DownloadManager(new MemoryStore()) { LinkProbe = () => new[] { a, b, metered }, MultiNetworkAvoidMetered = true })
            T.Check("metered networks can be excluded before workers are assigned", m.UsableLinks().Count == 2 && m.UsableLinks().All(x => !x.IsMetered));
        using (var m = new DownloadManager(new MemoryStore()) { LinkProbe = () => new[] { fast, slow }, MultiNetworkKeepOneFree = true })
            T.Check("keep one network free reserves the fastest adapter", m.UsableLinks().Count == 1 && m.UsableLinks()[0].Name == "slow");
        using (var m = new DownloadManager(new MemoryStore()) { LinkProbe = () => new[] { a, b }, MultiNetworkAskNewNetwork = true, MultiNetworkKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "test-a" } })
            T.Check("a newly detected network is not used until approved", m.UsableLinks().Count == 1 && m.UsableLinks()[0].Name == "test-a");
        using (var m = new DownloadManager(new MemoryStore()) { LinkProbe = () => new[] { a, b }, MultiNetworkDailyLimitBytes = 100 })
        {
            m.RestoreMultiNetworkUsage(DateOnly.FromDateTime(DateTime.Now), 100);
            T.Check("reaching the daily Multi-Network budget leaves normal downloading available but assigns no Multi-Net links", m.UsableLinks().Count == 0);
        }

        using (var m = new DownloadManager(new MemoryStore()) { MultiNetworkEnabled = true, LinkProbe = () => new[] { a } })
        {
            var item = new DownloadItem { Url = Base + "/mid.bin", FilePath = Path.Combine(dir, "one.bin"), Connections = 4 };
            m.Enqueue(item);
            T.Check("with only one network connected the switch changes nothing", await Program_WaitStatus(item, DownloadStatus.Complete) && m.GetLinkUsage(item).Count == 0, item.LastError);
        }
    }

    /// <summary>The per-network counters exist only while a download runs: returns the last reading before it ended.</summary>
    static async Task<IReadOnlyList<(NetworkLink Link, long Bytes, bool Active)>> WatchLinks(DownloadManager m, DownloadItem item)
    {
        IReadOnlyList<(NetworkLink Link, long Bytes, bool Active)> last = Array.Empty<(NetworkLink, long, bool)>();
        for (var i = 0; i < 6000 && item.Status is not ("Complete" or "Failed"); i++)
        {
            var now = m.GetLinkUsage(item);
            if (now.Count > 0) last = now;
            await Task.Delay(10);
        }
        return last;
    }

    static async Task WaitsForNetwork()
    {
        var dir = Sub();
        var up = false;
        using var m = new DownloadManager(new MemoryStore()) { NetworkUpProbe = () => Volatile.Read(ref up) };
        var item = new DownloadItem { Url = "http://127.0.0.1:1/never.bin", FilePath = Path.Combine(dir, "never.bin") };   // nothing listens there: a connection error
        m.Enqueue(item);
        var waiting = false;
        for (var i = 0; i < 100 && !waiting; i++) { await Task.Delay(100); waiting = (item.LastError ?? "").StartsWith("Waiting for the network", StringComparison.Ordinal); }
        T.Check("with no network the download says it is waiting", waiting, item.LastError);
        await Task.Delay(4000);
        T.Check("...and keeps waiting, without failing or counting retries", item.Status == DownloadStatus.Downloading.ToString() && item.RetryCount == 0, item.Status + " / " + item.RetryCount);
        Volatile.Write(ref up, true);
        T.Check("once a network is back the normal retry rules apply again", await Program.WaitStatusPublic(item, DownloadStatus.Failed, 90), item.Status + " / " + item.LastError);
    }

    static async Task RemoteServer()
    {
        var dir = Sub();
        var torrentDir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        using var server = new RemoteStatusServer(manager, () => dir, () => torrentDir);
        var port = 39000 + Random.Shared.Next(2000);
        server.Start(port, "secret-token");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        var home = await http.GetAsync("/");
        T.Check("the plain page loads with no token needed (only the API is guarded)", home.IsSuccessStatusCode && (await home.Content.ReadAsStringAsync()).Contains("Epsilon"));

        var noToken = await http.GetAsync("/api/status");
        T.Check("the API refuses a request with no token", noToken.StatusCode == System.Net.HttpStatusCode.Unauthorized);
        var wrongToken = await http.GetAsync("/api/status?token=nope");
        T.Check("...and a wrong one", wrongToken.StatusCode == System.Net.HttpStatusCode.Unauthorized);

        var empty = await http.GetFromJsonAsync<JsonElement>("/api/status?token=secret-token");
        T.Check("an empty download list is still a valid, empty JSON list", empty.GetProperty("ok").GetBoolean() && empty.GetProperty("items").GetArrayLength() == 0);

        var added = await http.PostAsJsonAsync("/api/add?token=secret-token", new { url = "https://example.com/movie.mp4" });
        var addedJson = await added.Content.ReadFromJsonAsync<JsonElement>();
        T.Check("adding a plain HTTP link succeeds and names the file from the URL", addedJson.GetProperty("ok").GetBoolean() && manager.Items.Any(i => i.FileName == "movie.mp4"));

        var again = await http.PostAsJsonAsync("/api/add?token=secret-token", new { url = "https://example.com/movie.mp4" });
        var againJson = await again.Content.ReadFromJsonAsync<JsonElement>();
        T.Check("adding the very same link again is reported as a duplicate, not a second download", againJson.GetProperty("ok").GetBoolean() && againJson.TryGetProperty("duplicate", out var dup) && dup.GetBoolean());

        var magnet = "magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038&dn=Some+Show";
        var addedMagnet = await http.PostAsJsonAsync("/api/add?token=secret-token", new { url = magnet });
        var magnetJson = await addedMagnet.Content.ReadFromJsonAsync<JsonElement>();
        T.Check("a magnet link is routed to the torrent path, not treated as a broken HTTP URL", magnetJson.GetProperty("ok").GetBoolean() && manager.Items.Any(i => i.Url == magnet));
        T.Check("a torrent added remotely goes to its own separate torrent folder, not the general download folder", manager.Items.First(i => i.Url == magnet).FilePath.StartsWith(torrentDir));
        T.Check("...while the ordinary HTTP download still went to the general folder, unaffected", manager.Items.First(i => i.Url == "https://example.com/movie.mp4").FilePath.StartsWith(dir));

        var badBody = await http.PostAsync("/api/add?token=secret-token", new StringContent("not json"));
        T.Check("garbage instead of JSON is a clean 400, not a server crash", badBody.StatusCode == System.Net.HttpStatusCode.BadRequest);

        var statusNow = await http.GetFromJsonAsync<JsonElement>("/api/status?token=secret-token");
        T.Check("the status list reflects what was actually added (2 distinct items: the http link and the magnet)", statusNow.GetProperty("items").GetArrayLength() == 2);

        var missingRoute = await http.GetAsync("/api/nonsense?token=secret-token");
        T.Check("an unknown route is a clean 404", missingRoute.StatusCode == System.Net.HttpStatusCode.NotFound);
    }

    static Task NetworkBoostTests()
    {
        // not elevated: refuses cleanly, touches no service, and says why
        var calls = new List<string>();
        var notElevated = new NetworkBoost(runCommand: cmd => { calls.Add(cmd); return true; }, isElevated: () => false);
        T.Check("without administrator rights, Pause fails with a clear reason and runs no command at all", !notElevated.Pause() && notElevated.LastError != null && calls.Count == 0, notElevated.LastError);
        notElevated.Resume();   // never became Active: must be a safe no-op, not an error
        T.Check("Resume before a successful Pause is a harmless no-op", calls.Count == 0);

        // elevated, every service stops and starts cleanly
        calls.Clear();
        var boost = new NetworkBoost(runCommand: cmd => { calls.Add(cmd); return true; }, isElevated: () => true);
        T.Check("Pause succeeds and stops every listed service exactly once", boost.Pause() && boost.LastError == null && NetworkBoost.ServiceNames.All(s => calls.Contains("stop " + s)) && calls.Count == NetworkBoost.ServiceNames.Length);
        T.Check("Pause is Active now", boost.Active);
        calls.Clear();
        T.Check("calling Pause again while already active does nothing further (idempotent)", boost.Pause() && calls.Count == 0);
        boost.Resume();
        T.Check("Resume starts back up exactly the services that were stopped, and clears Active", NetworkBoost.ServiceNames.All(s => calls.Contains("start " + s)) && calls.Count == NetworkBoost.ServiceNames.Length && !boost.Active);
        calls.Clear();
        boost.Resume();
        T.Check("calling Resume again once already resumed does nothing further", calls.Count == 0);

        // one service refuses (e.g. disabled): the others still get paused, and only what actually stopped gets restarted
        calls.Clear();
        var partial = new NetworkBoost(runCommand: cmd => { calls.Add(cmd); return !cmd.Contains("bits"); }, isElevated: () => true);
        var ok = partial.Pause();
        T.Check("one service refusing to stop is reported, but does not stop the others from being paused", !ok && partial.LastError != null && calls.Contains("stop wuauserv") && calls.Contains("stop DoSvc") && calls.Contains("stop bits"));
        calls.Clear();
        partial.Resume();
        T.Check("only the services that actually stopped are restarted - not the one that refused to stop in the first place", calls.Contains("start wuauserv") && calls.Contains("start DoSvc") && !calls.Contains("start bits"));
        return Task.CompletedTask;
    }

    static Task ProxyTests()
    {
        using var manager = new DownloadManager(new MemoryStore());
        var proxy = new DownloadManager.LiveProxy(manager);
        var http = new Uri("http://example.com/file.zip");
        var https = new Uri("https://example.com/file.zip");

        manager.ProxyMode = "none";
        T.Check("'none' means a direct connection for both http and https", proxy.GetProxy(http) == null && proxy.GetProxy(https) == null);
        T.Check("IsBypassed agrees with GetProxy returning null", proxy.IsBypassed(http));

        manager.ProxyMode = "manual";
        T.Check("manual mode with no host configured yet is still a safe direct connection, not a crash", proxy.GetProxy(http) == null);

        manager.ProxyHost = "proxy.example.com"; manager.ProxyPort = 3128;
        manager.ProxyUseForHttp = true; manager.ProxyUseForHttps = false;
        T.Check("http goes through the configured proxy", proxy.GetProxy(http)?.ToString() == "http://proxy.example.com:3128/", proxy.GetProxy(http)?.ToString());
        T.Check("https is left alone since only http was ticked", proxy.GetProxy(https) == null);

        manager.ProxyUseForHttps = true;
        T.Check("ticking https too now routes it through the same proxy", proxy.GetProxy(https)?.ToString() == "http://proxy.example.com:3128/");
        T.Check("IsBypassed now says false - a proxy really is in use", !proxy.IsBypassed(http));

        T.Check("no username set: no credentials are offered to the proxy at all", proxy.Credentials == null);
        manager.ProxyUsername = "alice"; manager.ProxyPassword = "secret";
        var cred = proxy.Credentials?.GetCredential(new Uri("http://proxy.example.com:3128"), "Basic");
        T.Check("once a username is set, the exact username/password are handed to the proxy", cred?.UserName == "alice" && cred?.Password == "secret", cred?.UserName);
        manager.ProxyUsername = "";
        T.Check("clearing the username back out removes the credentials again", proxy.Credentials == null);

        manager.ProxyMode = "system";
        Exception? thrown = null;
        try { proxy.GetProxy(http); } catch (Exception ex) { thrown = ex; }
        T.Check("'system' mode never throws, whatever this machine's actual system proxy setting is", thrown == null, thrown?.Message);

        manager.ProxyMode = "bogus-value-nobody-asked-for";
        T.Check("an unrecognised mode falls back to a direct connection rather than guessing", proxy.GetProxy(http) == null);
        return Task.CompletedTask;
    }

    static async Task BridgeCapture()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
        using var bridge = new NativeBridge(manager, () => dir);
        var chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        var firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0";
        var rules = new CaptureRules { FileTypes = "ZIP", ExcludedSites = "", Browsers = "chrome,edge" };
        bridge.CaptureRulesProvider = () => rules;
        bridge.LanguageProvider = () => "fa";
        string Req(string path, string? file, string ua, string extra = "") => $"{{\"url\":\"{Base}{path}\",{(file != null ? $"\"filePath\":\"{file}\"," : "")}\"userAgent\":\"{ua}\"{extra}}}";

        var skippedType = await Ask(Req("/small.bin", "C:/x/a.bin", chrome));
        T.Check("a file type that is not on the list is 'skipped' (not an error): the browser keeps it", !Ok(skippedType) && skippedType.GetProperty("skipped").GetBoolean() && Str(skippedType, "error").Contains(".bin") && manager.Items.Count == 0, skippedType.ToString());
        T.Check("every reply carries Makan's language for the extension", Str(skippedType, "language") == "fa");
        var listed = await Ask(Req("/small.bin?z", "C:/x/a.zip", chrome));
        T.Check("a listed type is captured", Ok(listed) && manager.Items.Count == 1, listed.ToString());
        var explicitAsk = await Ask(Req("/small.bin?e", "C:/x/b.bin", chrome, ",\"explicit\":true"));
        T.Check("things the user asked for (explicit) ignore the rules", Ok(explicitAsk) && manager.Items.Count == 2, explicitAsk.ToString());
        var linkKind = await Ask(Req("/small.bin?l", "C:/x/c.bin", chrome, ",\"kind\":\"link\""));
        T.Check("the 'download link' context-menu kind ignores the rules too", Ok(linkKind) && manager.Items.Count == 3, linkKind.ToString());
        var ff = await Ask(Req("/small.bin?f", "C:/x/d.zip", firefox));
        T.Check("a browser that is switched off in Options is skipped", !Ok(ff) && ff.GetProperty("skipped").GetBoolean() && Str(ff, "error").Contains("firefox") && manager.Items.Count == 3, ff.ToString());
        var late = await Ask(Req("/cd", null, chrome));
        T.Check("no name from the browser: the server's real name (a .pdf) is checked after the probe and skipped", !Ok(late) && late.GetProperty("skipped").GetBoolean() && Str(late, "error").Contains(".pdf") && manager.Items.Count == 3, late.ToString());

        rules.ExcludedSites = "127.0.0.*";
        var before = await HitsOf("/small.bin?site");
        var site = await Ask(Req("/small.bin?site", "C:/x/e.zip", chrome));
        T.Check("an excluded site is skipped before any request is made", !Ok(site) && site.GetProperty("skipped").GetBoolean() && await HitsOf("/small.bin?site") == before, site.ToString());
        rules.ExcludedSites = ""; rules.ExcludedAddresses = "*/small.bin?addr*";
        var addr = await Ask(Req("/small.bin?addr1", "C:/x/f.zip", chrome));
        T.Check("an excluded address pattern is skipped", !Ok(addr) && addr.GetProperty("skipped").GetBoolean(), addr.ToString());
        bridge.CaptureRulesProvider = null;
        var free = await Ask(Req("/small.bin?free", "C:/x/g.bin", firefox));
        T.Check("without rules everything is captured (old behaviour)", Ok(free), free.ToString());
    }

    static async Task BridgeLinks()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
        using var bridge = new NativeBridge(manager, () => dir);
        var items = $"[{{\"url\":\"{Base}/small.bin?1\",\"text\":\"Episode 1\",\"kind\":\"link\"}},{{\"url\":\"{Base}/small.bin?1\",\"text\":\"dup\",\"kind\":\"link\"}},{{\"url\":\"{Base}/small.bin?2\",\"kind\":\"link\"}},{{\"url\":\"{Base}/small.bin?2\",\"text\":\"Episode 2\",\"kind\":\"link\"}},{{\"url\":\"{Base}/pic.png\",\"kind\":\"image\"}},{{\"url\":\"javascript:alert(1)\",\"kind\":\"link\"}},{{\"url\":\"http://localhost:18080/small.bin?3\",\"text\":\"other host\",\"kind\":\"link\"}}]";
        var req = $"{{\"kind\":\"links\",\"items\":{items},\"cookies\":{{\"127.0.0.1\":\"session=abc\",\"other.example\":\"x=y\"}},\"referrer\":\"https://site.example/page\",\"userAgent\":\"UA\",\"pageTitle\":\"Lanterns S01\"}}";

        LinksPrompt? seen = null;
        bridge.LinksPrompt = p => { seen = p; return true; };
        var r = await Ask(req);
        T.Check("handed to the picker window: ok/pending, nothing queued", Ok(r) && r.GetProperty("pending").GetBoolean() && manager.Items.Count == 0, r.ToString());
        T.Check("addresses are deduplicated, bad ones dropped, missing text filled from the duplicate", seen != null && seen.Links.Count == 4 && seen.Links.First(l => l.Url.EndsWith("?2")).Text == "Episode 2" && seen.Links.First(l => l.Url.EndsWith("?1")).Text == "Episode 1", seen?.Links.Count.ToString());
        T.Check("kinds, page title, referrer and per-host cookies arrive", seen!.Links.Any(l => l.Kind == "image") && seen.PageTitle == "Lanterns S01" && seen.Referrer == "https://site.example/page" && seen.Cookies["127.0.0.1"] == "session=abc");

        bridge.LinksPrompt = null; bridge.AskBeforeStart = () => true;     // no window: every non-image link is added, Stopped
        var direct = await Ask(req);
        T.Check("without a picker: images skipped, links added Stopped while asking", Ok(direct) && direct.GetProperty("count").GetInt32() == 3 && manager.Items.All(i => i.Status == "Paused"), direct.ToString());
        T.Check("a site's cookie goes only to that site's host", manager.Items.First(i => i.Url.Contains("127.0.0.1")).Cookie == "session=abc" && manager.Items.First(i => i.Url.Contains("localhost")).Cookie == null);
        var none = await Ask("{\"kind\":\"links\",\"items\":[{\"url\":\"ftp://x/y\"}]}");
        T.Check("no usable links is a clean error", !Ok(none) && Str(none, "error").Length > 0, none.ToString());
    }

    static async Task BridgeStreams()
    {
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        using var bridge = new NativeBridge(manager, () => dir);

        // The manifest needs the page's cookie + referer, exactly like a real protected site.
        var list = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/hls/master.m3u8\",\"cookie\":\"session=abc\",\"referrer\":\"https://site.example/watch\",\"userAgent\":\"UA\"}}");
        T.Check("stream list answered", Ok(list) && list.TryGetProperty("streams", out var arr) && arr.GetArrayLength() == 2, list.ToString());
        var hd = list.GetProperty("streams").EnumerateArray().First(x => x.GetProperty("height").GetInt32() == 720);
        T.Check("quality, bitrate, kind and codec are reported", Str(hd, "quality") == "1280x720" && hd.GetProperty("bitrate").GetInt64() == 2800000 && Str(hd, "kind") == "hls" && Str(hd, "codec").Contains("avc1"), hd.ToString());
        T.Check("separate audio playlist is reported", Str(hd, "audioUrl").EndsWith("/hls/audio/en.m3u8"), hd.ToString());
        // Length and quality of what will be downloaded (so the menu can say "12 sec, quality 720p").
        var dur = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/hls/master2.m3u8\"}}");
        T.Check("master playlist: every quality gets the length (read from the smallest variant)", Ok(dur) && dur.GetProperty("streams").EnumerateArray().All(x => x.GetProperty("duration").GetDouble() == 12), dur.ToString());
        var plain = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/hls/vod.m3u8\"}}");
        T.Check("media playlist: total length reported, quality is 'Source'", Ok(plain) && plain.GetProperty("streams")[0].GetProperty("duration").GetDouble() == 48 && Str(plain.GetProperty("streams")[0], "quality") == "Source", plain.ToString());
        var dashList = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/dash/static.mpd\"}}");
        T.Check("DASH: qualities, kind and length from the manifest", Ok(dashList) && dashList.GetProperty("streams").GetArrayLength() == 2 && Str(dashList.GetProperty("streams")[0], "kind") == "dash" && dashList.GetProperty("streams")[1].GetProperty("duration").GetDouble() == 12, dashList.ToString());
        var denied = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/hls/master.m3u8\"}}");
        T.Check("without the session the site refuses and the message is passed on", !Ok(denied) && Str(denied, "error").Length > 0, denied.ToString());
        var notStream = await Ask($"{{\"kind\":\"streams\",\"url\":\"{Base}/html\"}}");
        T.Check("a page that is not a playlist is a clean error", !Ok(notStream), notStream.ToString());

        // Queue directly (no UI prompt hooked up): name comes from the page title, made safe for Windows.
        var r = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/vod.m3u8\",\"title\":\"My Video: Part 1?\",\"format\":\"ts\"}}");
        T.Check("stream accepted", Ok(r), Str(r, "error"));
        T.Check("file name from the title, invalid characters replaced", Path.GetFileName(Str(r, "filePath")) == "My Video_ Part 1_.ts" && Path.GetDirectoryName(Str(r, "filePath")) == dir, Str(r, "filePath"));
        var item = manager.Items.Single();
        T.Check("downloads byte-exact through the normal queue", await Program_WaitStatus(item, DownloadStatus.Complete) && T.Sha(item.FilePath) == await ShaOf("/__expect/vod", true), item.LastError);

        var again = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/slow.m3u8\",\"title\":\"slow\"}}");
        var twice = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/slow.m3u8\",\"title\":\"slow\"}}");
        T.Check("the same stream twice is a duplicate", Ok(again) && twice.TryGetProperty("duplicate", out var dup) && dup.GetBoolean());
        foreach (var i in manager.Items.ToList()) manager.Cancel(i);

        // With the UI hooked up, the request is handed over for the save-as dialog instead of being queued.
        StreamRequest? seen = null;
        bridge.StreamPrompt = req => { seen = req; return true; };
        var before = manager.Items.Count;
        var prompted = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/small.m3u8\",\"audioUrl\":\"{Base}/hls/audio-en.m3u8\",\"title\":\"Clip\",\"format\":\"mp4\",\"cookie\":\"a=b\"}}");
        T.Check("prompt hook gets everything and nothing is queued behind its back", Ok(prompted) && seen is { Format: "mp4", Title: "Clip", Cookie: "a=b" } && seen.AudioUrl!.EndsWith("audio-en.m3u8") && manager.Items.Count == before, seen?.ToString());
        var forced = await Ask($"{{\"kind\":\"stream\",\"url\":\"{Base}/hls/small.m3u8\",\"title\":\"Forced\",\"noPrompt\":true}}");
        T.Check("noPrompt (Download all) skips the dialog", Ok(forced) && manager.Items.Count == before + 1);
        foreach (var i in manager.Items.ToList()) manager.Cancel(i);

        T.Check("stream file names: title, fallback and safety",
            NativeBridge.StreamFileName(new StreamRequest("https://x/y/index.m3u8", null, null, "ts", null, null, null)) == "video.ts" &&
            NativeBridge.StreamFileName(new StreamRequest("https://x/y/show-1.m3u8", null, null, "mp4", null, null, null)) == "show-1.mp4" &&
            NativeBridge.StreamFileName(new StreamRequest("https://x/a.m3u8", null, "CON", "ts", null, null, null)) == "_CON.ts");
    }

    static async Task<string> ShaOf(string path, bool expect = false)
    {
        if (expect) return await Http.GetStringAsync(Base + path);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        return doc.RootElement.GetProperty("sha").GetProperty(path).GetString()!;
    }

    static async Task HostRelay()
    {
        var dll = FindHostDll();
        if (dll == null) { T.Check("MakanNativeHost.dll built (run `dotnet build` in MakanNativeHost first)", false); return; }
        var dir = Sub();
        using var manager = new DownloadManager(new MemoryStore());
        using var bridge = new NativeBridge(manager, () => dir);

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" chrome-extension://abc/") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var host = Process.Start(psi)!;
        var stdin = host.StandardInput.BaseStream; var stdout = host.StandardOutput.BaseStream;

        async Task<JsonElement> Native(string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            await stdin.WriteAsync(BitConverter.GetBytes(payload.Length)); await stdin.WriteAsync(payload); await stdin.FlushAsync();
            var header = new byte[4]; await stdout.ReadExactlyAsync(header);
            var body = new byte[BitConverter.ToInt32(header)]; await stdout.ReadExactlyAsync(body);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }

        var ping = await Native("{\"kind\":\"ping\"}");
        T.Check("native-messaging frame in, reply frame out (ping)", Ok(ping) && ping.TryGetProperty("running", out var r) && r.GetBoolean());

        // Same field casing the extension sends (PascalCase, nulls included).
        var reply = await Native($"{{\"Url\":\"{Base}/small.bin\",\"FilePath\":\"small.bin\",\"Cookie\":null,\"Referrer\":null,\"UserAgent\":\"UA\",\"Priority\":5}}");
        T.Check("extension-style request accepted through the host", Ok(reply), Str(reply, "error"));
        var item = manager.Items.First();
        T.Check("and the file really downloads", await Program_WaitStatus(item, DownloadStatus.Complete) && T.Sha(item.FilePath) == (await ShaOf("/small.bin")), item.LastError);

        var second = await Native("{\"kind\":\"ping\"}");
        T.Check("host handles several messages on one connection", Ok(second));
        stdin.Close();
        T.Check("host exits cleanly when the browser closes stdin", host.WaitForExit(5000) && host.ExitCode == 0);
    }

    static async Task BridgeMagnet()
    {
        var script = FindSwarmScript();
        if (script == null) { T.Check("magnet swarm script found (skipping)", true); return; }
        var dir = Sub();
        var seedDir = Path.Combine(dir, "seed");
        using var swarm = Process.Start(new ProcessStartInfo("python3") { ArgumentList = { script, "serve", seedDir, "--files", "note.txt:60000", "--piece", "32768", "--seed-name", "bridge-test" }, RedirectStandardOutput = true, UseShellExecute = false })!;
        var infoLine = await swarm.StandardOutput.ReadLineAsync() ?? "{}";
        try
        {
            using var info = JsonDocument.Parse(infoLine);
            var magnet = info.RootElement.GetProperty("magnet").GetString()!;
            var payload = info.RootElement.GetProperty("payload").GetString()!;
            var name = info.RootElement.GetProperty("name").GetString()!;
            var torrentPath = Path.Combine(seedDir, "bridge-test.torrent");

            // one NativeBridge at a time: it listens on a fixed, shared pipe name, so a second one alive at the same time
            // would race the first for incoming connections instead of being reachable on its own.
            using (var manager = new DownloadManager(new MemoryStore()) { TorrentEngineFactory = () => StartTestEngine(dir) })
            using (var bridge = new NativeBridge(manager, () => dir))
            {
                var noScheme = await Ask("{\"url\":\"not a url at all\"}");
                T.Check("nonsense is refused (not silently accepted as a magnet)", !Ok(noScheme));

                var reply = await Ask($"{{\"url\":\"{JsonEscape(magnet)}\"}}");
                T.Check("a magnet link is accepted (the HTTP-only rule does not apply to it)", Ok(reply), Str(reply, "error"));
                var item = manager.Items.First(x => x.Id == reply.GetProperty("id").GetInt64());
                T.Check("named from the magnet's own title until the real metadata arrives", item.FileName == name, item.FileName);
                T.Check("the magnet link downloads through the ordinary queue and completes", await Program.WaitStatusPublic(item, DownloadStatus.Complete, 60), item.LastError);
                var content = Path.Combine(dir, name);
                T.Check("the downloaded file matches the seeder's payload", File.Exists(content) && T.Sha(content) == T.Sha(payload));

                var duplicate = await Ask($"{{\"url\":\"{JsonEscape(magnet)}\"}}");
                T.Check("adding the same magnet again is reported as a duplicate, not a second download", Ok(duplicate) && duplicate.TryGetProperty("duplicate", out var d) && d.GetBoolean());
            }

            // opening the .torrent file itself (as if double-clicked in Explorer, or "Open with Makan"): a plain local path, no HTTP involved
            var dir2 = Sub();
            using (var manager2 = new DownloadManager(new MemoryStore()) { TorrentEngineFactory = () => StartTestEngine(dir2) })
            using (var bridge2 = new NativeBridge(manager2, () => dir2))
            {
                var fileReply = await Ask($"{{\"url\":\"{JsonEscape(torrentPath)}\"}}");
                T.Check("a local .torrent file path is accepted (not rejected as a non-HTTP URL)", Ok(fileReply), Str(fileReply, "error"));
                var fileItem = manager2.Items.First(x => x.Id == fileReply.GetProperty("id").GetInt64());
                T.Check("named from the .torrent file's own title", fileItem.FileName == name, fileItem.FileName);
                T.Check("and it downloads the same content", await Program.WaitStatusPublic(fileItem, DownloadStatus.Complete, 60) && T.Sha(Path.Combine(dir2, name)) == T.Sha(payload), fileItem.LastError);

                var missing = await Ask("{\"url\":\"C:\\\\nowhere\\\\ghost.torrent\"}");
                T.Check("a .torrent path that does not exist fails with a clear error, not a crash", !Ok(missing) && Str(missing, "error").Length > 0);
            }
        }
        finally { try { swarm.Kill(true); } catch { } }
    }

    static TorrentEngine StartTestEngine(string dataDir)
    {
        var engine = new TorrentEngine(new TorrentEngineOptions { ListenPort = 0, EnableDht = false, StateDirectory = Path.Combine(dataDir, "state") }) { AllowLocalPeers = true };
        engine.Start();
        return engine;
    }

    static string? FindSwarmScript()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var candidate = Path.Combine(d.FullName, "server", "fake_swarm.py");
            if (File.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        return null;
    }

    static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string? FindHostDll()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            var candidate = Path.Combine(d.FullName, "MakanNativeHost", "bin", "Release", "net8.0", "MakanNativeHost.dll");
            if (File.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        return null;
    }

    static Task<bool> Program_WaitStatus(DownloadItem item, DownloadStatus s) => Program.WaitStatusPublic(item, s);

    // ---- media -----------------------------------------------------------------------------------------------

    static async Task Hls()
    {
        var media = new MediaService();
        var streams = await media.GetStreamsAsync(Base + "/hls/master.m3u8", "session=abc", "https://site.example/watch", "TestUA/1");
        T.Check("two variants found with credentials", streams.Count == 2, streams.Count.ToString());
        var hd = streams.First(s => s.Height == 720);
        T.Check("CODECS with a comma inside quotes is kept whole", hd.Codec == "avc1.640028,mp4a.40.2", hd.Codec);
        T.Check("bandwidth and resolution parsed", hd.Bitrate == 2800000 && hd.Width == 1280);
        T.Check("variant URL resolved against the manifest", hd.Url == Base + "/hls/v720/index.m3u8", hd.Url);
        T.Check("default audio rendition attached (video would be silent otherwise)", hd.AudioUrl == Base + "/hls/audio/en.m3u8", hd.AudioUrl);

        var denied = false;
        try { await media.GetStreamsAsync(Base + "/hls/master.m3u8"); } catch (HttpRequestException) { denied = true; }
        T.Check("manifest without cookie/referer is rejected by the site (so passing them matters)", denied);

        var plain = await media.GetStreamsAsync(Base + "/hls/plain.m3u8");
        T.Check("muxed variant has no separate audio", plain.Count == 1 && plain[0].AudioUrl == null);
    }

    static async Task Ffmpeg()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("  (skipped on Windows: uses a bash stand-in for ffmpeg)"); return; }
        var dir = Sub();
        var argsFile = Path.Combine(dir, "args.bin");
        var fake = Path.Combine(dir, "ffmpeg.exe");
        File.WriteAllText(fake, "#!/bin/bash\nprintf '%s\\0' \"$@\" > \"$FAKE_FFMPEG_ARGS\"\nhead -c 3000000 /dev/zero | tr '\\0' 'x' >&2\nout=\"${@: -1}\"\nif [ -n \"$FAKE_FFMPEG_FAIL\" ]; then echo 'Server returned 403 Forbidden' >&2; exit 1; fi\necho data > \"$out\"\nexit 0\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("FAKE_FFMPEG_ARGS", argsFile);
        Environment.SetEnvironmentVariable("FAKE_FFMPEG_FAIL", null);

        var media = new MediaService();
        var output = Path.Combine(dir, "out.mp4");
        var run = media.RunFfmpegAsync(fake, Base + "/hls/v720/index.m3u8", output, "session=abc", "https://site.example/watch", "TestUA/1", Base + "/hls/audio/en.m3u8");
        var finished = await Task.WhenAny(run, Task.Delay(20000)) == run;
        T.Check("3 MB of ffmpeg stderr does not freeze the runner", finished);
        if (!finished) return;
        await run;
        T.Check("output produced", File.Exists(output));

        var args = File.ReadAllText(argsFile).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var headerIdx = args.Select((a, i) => (a, i)).Where(x => x.a == "-headers").Select(x => x.i).ToList();
        T.Check("cookie and referer both sent, once per input (ffmpeg keeps only one -headers per input)", headerIdx.Count == 2 && args[headerIdx[0] + 1].Contains("Cookie: session=abc") && args[headerIdx[0] + 1].Contains("Referer: https://site.example/watch"), string.Join(" | ", args));
        T.Check("audio rendition is a second input and both streams are mapped", args.Count(a => a == "-i") == 2 && args.Contains("-map"));

        // Failure: the useful last lines of ffmpeg's output surface, the half-written file is removed.
        Environment.SetEnvironmentVariable("FAKE_FFMPEG_FAIL", "1");
        var failOut = Path.Combine(dir, "fail.mp4"); File.WriteAllText(failOut, "partial");
        string? message = null;
        try { await media.RunFfmpegAsync(fake, Base + "/x.m3u8", failOut); } catch (InvalidOperationException ex) { message = ex.Message; }
        T.Check("failure message contains ffmpeg's reason", message != null && message.Contains("403"), message);
        T.Check("partial output is cleaned up", !File.Exists(failOut));
        Environment.SetEnvironmentVariable("FAKE_FFMPEG_FAIL", null);

        // "ffmpeg.exe" typed in Settings is found on PATH.
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        try { T.Check("ResolveFfmpeg searches PATH", MediaService.ResolveFfmpeg("ffmpeg.exe") == fake, MediaService.ResolveFfmpeg("ffmpeg.exe")); }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        var missing = false;
        try { MediaService.ResolveFfmpeg("definitely-not-installed-ffmpeg.exe"); } catch (FileNotFoundException) { missing = true; }
        T.Check("missing ffmpeg gives a clear error", missing);
    }
}
