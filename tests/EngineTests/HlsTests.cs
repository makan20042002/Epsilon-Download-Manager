using System.Diagnostics;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>Native HLS downloading: plain, encrypted, fMP4, master, separate audio, resume, errors, cookies.</summary>
static class HlsTests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("HLS: plain VOD is byte-exact (ordered, parallel), no leftovers", Vod);
        await Run("HLS: master playlist pasted directly picks the best quality; .m3u8 name becomes .ts", Master);
        await Run("HLS: AES-128 (explicit IV and sequence-number IV) decrypts correctly", Encrypted);
        await Run("HLS: fragmented MP4 (init segment) becomes .mp4 even if .ts was asked", Fmp4);
        await Run("HLS: byte-range segments (one big file)", ByteRange);
        await Run("HLS: live streams, DRM and login pages are refused with clear messages", Refusals);
        await Run("HLS: missing segment fails fast (404 not retried); flaky 503 is retried", Errors);
        await Run("HLS: pause keeps finished segments, resume completes without re-fetching them", PauseResume);
        await Run("HLS: cancel deletes the partial file and resume data", CancelCleans);
        await Run("HLS: app restart mid-download resumes from the saved segment", Restart);
        await Run("HLS: cookies go only to the playlist's own host", Cookies);
        await Run("HLS: MP4 and separate audio need FFmpeg; without it nothing is lost", FfmpegPaths);
        await Run("HLS: URL detection", UrlDetection);
        await Run("DASH: stream list for the menu (qualities, best audio attached)", DashList);
        await Run("DASH: templates (number / time / timeline) and SegmentList download byte-exact", DashDownloads);
        await Run("DASH: video + audio need FFmpeg to merge; without it both parts are kept", DashMerge);
        await Run("DASH: live, DRM and single-file manifests are refused clearly", DashRefusals);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static string Sub() { var d = Path.Combine(Dir, "h" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }
    static async Task<string> Expect(string name) => await Http.GetStringAsync(Base + "/__expect/" + name);
    static DownloadItem Item(string path, string file, int conns = 6, string? cookie = null) =>
        new() { Url = path.StartsWith("http") ? path : Base + path, FilePath = file, Connections = conns, Cookie = cookie };
    static Task<bool> Wait(DownloadItem i, DownloadStatus s, int sec = 60) => Program.WaitStatusPublic(i, s);
    static async Task<int> Hits(string path)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        return doc.RootElement.GetProperty("hits").TryGetProperty(path, out var v) ? v.GetInt32() : 0;
    }
    static string[] Leftovers(string dir) => Directory.GetFiles(dir).Where(f => f.EndsWith(".part") || f.EndsWith(".hls.json") || f.EndsWith(".mux") || f.EndsWith(".tmp")).ToArray();

    static async Task Vod()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/vod.m3u8", Path.Combine(dir, "clip.ts")); m.Enqueue(item);
        T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
        T.Check("sha256 equals the segments in order", T.Sha(item.FilePath) == await Expect("vod"));
        T.Check("size reported = real size, 100%", item.TotalBytes == new FileInfo(item.FilePath).Length && item.Progress >= 99.9, $"{item.TotalBytes} {item.Progress}");
        T.Check("no .part / resume file left behind", Leftovers(dir).Length == 0, string.Join(",", Leftovers(dir)));
    }

    static async Task Master()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/master2.m3u8", Path.Combine(dir, "index.m3u8")); m.Enqueue(item);
        T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
        T.Check("renamed to .ts", item.FilePath.EndsWith(".ts") && File.Exists(item.FilePath), item.FilePath);
        T.Check("best (720p) variant downloaded", T.Sha(item.FilePath) == await Expect("master2"));
        T.Check("the 240p variant was never touched", await Hits("/hls/small.m3u8") == 0);
    }

    static async Task Encrypted()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var a = Item("/hls/enc.m3u8", Path.Combine(dir, "a.ts")); var b = Item("/hls/enc-seq.m3u8", Path.Combine(dir, "b.ts"));
        m.Enqueue(a); m.Enqueue(b);
        T.Check("explicit-IV stream completes", await Wait(a, DownloadStatus.Complete), a.LastError);
        T.Check("explicit-IV plaintext is exact", T.Sha(a.FilePath) == await Expect("enc"));
        T.Check("sequence-IV stream completes", await Wait(b, DownloadStatus.Complete), b.LastError);
        T.Check("sequence-IV plaintext is exact", T.Sha(b.FilePath) == await Expect("enc-seq"));
    }

    static async Task Fmp4()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/fmp4.m3u8", Path.Combine(dir, "clip.ts")); m.Enqueue(item);
        T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
        T.Check("saved as .mp4", item.FilePath.EndsWith(".mp4"), item.FilePath);
        T.Check("init segment + fragments, exact", T.Sha(item.FilePath) == await Expect("fmp4"));
    }

    static async Task ByteRange()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/range.m3u8", Path.Combine(dir, "r.ts")); m.Enqueue(item);
        T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
        T.Check("ranges (incl. one without offset) reassemble the file exactly", T.Sha(item.FilePath) == await Expect("range"));
    }

    static async Task Refusals()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var live = Item("/hls/live.m3u8", Path.Combine(dir, "l.ts")); var drm = Item("/hls/drm.m3u8", Path.Combine(dir, "d.ts")); var html = Item("/hls/html.m3u8", Path.Combine(dir, "h.ts"));
        m.Enqueue(live); m.Enqueue(drm); m.Enqueue(html);
        T.Check("live => Failed", await Wait(live, DownloadStatus.Failed), live.Status);
        T.Check("live message is understandable", (live.LastError ?? "").Contains("live", StringComparison.OrdinalIgnoreCase), live.LastError);
        T.Check("DRM => Failed with DRM message", await Wait(drm, DownloadStatus.Failed) && (drm.LastError ?? "").Contains("DRM"), drm.LastError);
        T.Check("login page instead of playlist => Failed", await Wait(html, DownloadStatus.Failed) && (html.LastError ?? "").Contains("playlist"), html.LastError);
    }

    static async Task Errors()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var missing = Item("/hls/missing.m3u8", Path.Combine(dir, "m.ts")); m.Enqueue(missing);
        T.Check("missing segment => Failed", await Wait(missing, DownloadStatus.Failed), missing.Status);
        T.Check("message names the 404", (missing.LastError ?? "").Contains("404"), missing.LastError);
        T.Check("404 requested once (not retried)", await Hits("/hls/gone.ts") == 1, (await Hits("/hls/gone.ts")).ToString());

        var flaky = Item("/hls/flaky.m3u8", Path.Combine(dir, "f.ts")); m.Enqueue(flaky);
        T.Check("flaky 503 segment still completes", await Wait(flaky, DownloadStatus.Complete), flaky.LastError);
        T.Check("byte-exact after retries", T.Sha(flaky.FilePath) == await Expect("flaky"));
        T.Check("the flaky segment was retried (3 requests)", await Hits("/hls/fseg/3.ts") == 3, (await Hits("/hls/fseg/3.ts")).ToString());
    }

    static async Task PauseResume()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/slow.m3u8", Path.Combine(dir, "s.ts"), conns: 2); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 1_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m.Pause(item);
        T.Check("becomes Paused", await Wait(item, DownloadStatus.Paused));
        var kept = item.DoneBytes;
        T.Check("progress preserved", kept >= 1_000_000, kept.ToString());
        T.Check("partial + resume file kept", File.Exists(item.FilePath + ".part") && File.Exists(item.FilePath + ".hls.json"));
        var doneSegments = (int)(kept / (200 * 1024));
        m.Enqueue(item);
        T.Check("resumes to Complete", await Wait(item, DownloadStatus.Complete, 90), item.LastError);
        T.Check("byte-exact", T.Sha(item.FilePath) == await Expect("slow"));
        var firstThree = 0; for (var i = 0; i < 3; i++) firstThree += await Hits($"/hls/sseg/{i}.ts");
        T.Check("segments finished before the pause were not fetched again", firstThree == 3, $"first three fetched {firstThree}x, {doneSegments} segments were done");
    }

    static async Task CancelCleans()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var item = Item("/hls/slow.m3u8", Path.Combine(dir, "c.ts"), conns: 2); m.Enqueue(item);
        var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 400_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m.Cancel(item);
        T.Check("ends Cancelled", await Wait(item, DownloadStatus.Cancelled));
        await Task.Delay(300);
        T.Check("partial files removed", !File.Exists(item.FilePath) && Leftovers(dir).Length == 0, string.Join(",", Directory.GetFiles(dir)));
    }

    static async Task Restart()
    {
        var dir = Sub(); var store = new MemoryStore();
        var m1 = new DownloadManager(store);
        var item = Item("/hls/slow.m3u8", Path.Combine(dir, "r.ts"), conns: 2); m1.Enqueue(item);
        var id = item.Id; var sw = Stopwatch.StartNew();
        while (item.DoneBytes < 1_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m1.Dispose();
        var row = store.Row(id)!;
        T.Check("stored as Queued for auto-resume", row.Status == "Queued", row.Status);
        await Http.GetAsync(Base + "/__reset");
        using var m2 = new DownloadManager(store);
        var again = m2.Items.Single(x => x.Id == id);
        T.Check("completes after restart", await Wait(again, DownloadStatus.Complete, 90), again.LastError);
        T.Check("byte-exact", T.Sha(again.FilePath) == await Expect("slow"));
        var fetched = 0; for (var i = 0; i < 24; i++) fetched += await Hits($"/hls/sseg/{i}.ts");
        T.Check("only the remainder was fetched after restart", fetched < 24, $"{fetched} of 24 segments fetched after restart");
    }

    static async Task Cookies()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var same = Item("/hls/cookie.m3u8", Path.Combine(dir, "c.ts"), cookie: "session=abc");
        var cross = Item("/hls/xhost.m3u8", Path.Combine(dir, "x.ts"), cookie: "session=abc");
        m.Enqueue(same); m.Enqueue(cross);
        T.Check("cookie-protected playlist + segments download", await Wait(same, DownloadStatus.Complete), same.LastError);
        T.Check("byte-exact", T.Sha(same.FilePath) == await Expect("cookie"));
        T.Check("segments on another host download", await Wait(cross, DownloadStatus.Complete), cross.LastError);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        var seen = doc.RootElement.GetProperty("cookie_at");
        T.Check("cookie sent to the playlist's host", seen.GetProperty("/hls/cseg/0.ts").GetString()!.Contains("session=abc"));
        T.Check("cookie NOT sent to the other host", seen.GetProperty("/hls/xseg/0.ts").GetString() == "", seen.ToString());
    }

    static async Task FfmpegPaths()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("  (skipped: uses a bash stand-in for ffmpeg)"); return; }
        var dir = Sub();
        var fake = Path.Combine(dir, "fake-ffmpeg");
        var argsFile = Path.Combine(dir, "args.txt");
        File.WriteAllText(fake, "#!/bin/bash\nprintf '%s\\n' \"$@\" > \"" + argsFile + "\"\nargs=(\"$@\"); out=\"${args[-1]}\"; ins=()\nfor ((i=0;i<${#args[@]};i++)); do if [ \"${args[$i]}\" = \"-i\" ]; then ins+=(\"${args[$((i+1))]}\"); fi; done\ncat \"${ins[@]}\" > \"$out\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // 1) no FFmpeg: MP4 requested for a TS stream => raw .ts kept, user told why
        using (var m = new DownloadManager(new MemoryStore()) { FfmpegPath = "/nonexistent/ffmpeg-not-here" })
        {
            var item = Item("/hls/vod.m3u8", Path.Combine(dir, "one.mp4")); m.Enqueue(item);
            T.Check("completes without FFmpeg", await Wait(item, DownloadStatus.Complete), item.LastError);
            T.Check("kept as .ts, bytes exact", item.FilePath.EndsWith(".ts") && T.Sha(item.FilePath) == await Expect("vod"), item.FilePath);
            T.Check("message says FFmpeg is needed for MP4", (item.LastError ?? "").Contains("FFmpeg"), item.LastError);
            T.Check("no leftovers", Leftovers(dir).Length == 0, string.Join(",", Leftovers(dir)));
        }
        // 2) no FFmpeg + separate audio => video kept, audio saved beside it
        await Http.GetAsync(Base + "/__reset");
        using (var m = new DownloadManager(new MemoryStore()) { FfmpegPath = "/nonexistent/ffmpeg-not-here" })
        {
            var item = Item("/hls/master-audio.m3u8", Path.Combine(dir, "two.mp4")); m.Enqueue(item);
            T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
            T.Check("video track exact", T.Sha(item.FilePath) == await Expect("vonly"), item.FilePath);
            var audio = Directory.GetFiles(dir, "two.audio.*").FirstOrDefault();
            T.Check("audio track saved separately and exact", audio != null && T.Sha(audio) == await Expect("aonly"), audio);
        }
        // 3) with FFmpeg: TS -> MP4 and audio merge
        await Http.GetAsync(Base + "/__reset");
        using (var m = new DownloadManager(new MemoryStore()) { FfmpegPath = fake })
        {
            var mp4 = Item("/hls/vod.m3u8", Path.Combine(dir, "three.mp4")); m.Enqueue(mp4);
            T.Check("TS->MP4 completes", await Wait(mp4, DownloadStatus.Complete), mp4.LastError);
            T.Check("output is .mp4 (via ffmpeg) and no temp files remain", mp4.FilePath.EndsWith("three.mp4") && T.Sha(mp4.FilePath) == await Expect("vod") && Leftovers(dir).Length == 0);
            var args = File.ReadAllText(argsFile);
            T.Check("ffmpeg asked for stream copy into mp4", args.Contains("-c\ncopy") && args.Contains("-f\nmp4"), args.Replace("\n", " "));

            var merged = Item(DownloadManager.WithAudio(Base + "/hls/vonly.m3u8", Base + "/hls/audio-en.m3u8"), Path.Combine(dir, "four.mp4")); m.Enqueue(merged);
            T.Check("video + separate audio (URL fragment) completes", await Wait(merged, DownloadStatus.Complete), merged.LastError);
            T.Check("merged by ffmpeg: both inputs given, output exact", T.Sha(merged.FilePath) == await Expect("vplusa") && File.ReadAllText(argsFile).Split('\n').Count(l => l == "-i") == 2);
            T.Check("parts removed", Leftovers(dir).Length == 0 && !Directory.GetFiles(dir, "four.audio.*").Any(), string.Join(",", Directory.GetFiles(dir)));

            var tsOut = Item("/hls/vod.m3u8", Path.Combine(dir, "five.ts")); m.Enqueue(tsOut);
            T.Check("a .ts request never needs FFmpeg", await Wait(tsOut, DownloadStatus.Complete) && T.Sha(tsOut.FilePath) == await Expect("vod"));
        }
    }


    static async Task DashList()
    {
        var streams = await new MediaService().GetStreamsAsync(Base + "/dash/static.mpd");
        T.Check("two video qualities, ascending", streams.Count == 2 && streams[0].Height == 360 && streams[1].Height == 720, string.Join(",", streams.Select(x => x.Height)));
        T.Check("bitrate and codec reported", streams[1].Bitrate == 1500000 && streams[1].Codec == "avc1.4d401f");
        T.Check("selection (video + best audio) travels in the URL fragment", streams[1].Url == Base + "/dash/static.mpd#makan-v=v2&makan-a=a1", streams[1].Url);
        T.Check("detected as DASH url", DownloadManager.IsDashUrl(streams[1].Url) && !DownloadManager.IsHlsUrl(streams[1].Url));
        var (v, a) = DashManifest.ParseSelection(new Uri(streams[1].Url).Fragment);
        T.Check("selection round-trips", v == "v2" && a == "a1");
        var refused = false;
        try { await new MediaService().GetStreamsAsync(Base + "/dash/live.mpd"); } catch (NotSupportedException) { refused = true; }
        T.Check("live manifest is refused when listing", refused);
    }

    static string MakeFakeFfmpeg(string dir)
    {
        var fake = Path.Combine(dir, "fake-ffmpeg");
        File.WriteAllText(fake, "#!/bin/bash\nargs=(\"$@\"); out=\"${args[-1]}\"; ins=()\nfor ((i=0;i<${#args[@]};i++)); do if [ \"${args[$i]}\" = \"-i\" ]; then ins+=(\"${args[$((i+1))]}\"); fi; done\ncat \"${ins[@]}\" > \"$out\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return fake;
    }

    static async Task DashDownloads()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore()) { FfmpegPath = "/nonexistent/ffmpeg-not-here" };
        // video only (v1 selected, no audio): a plain fragmented MP4, no FFmpeg needed
        var v1 = Item(DashManifest.WithSelection(new Uri(Base + "/dash/static.mpd"), "v1", null), Path.Combine(dir, "low.mp4"));
        var time = Item("/dash/time.mpd", Path.Combine(dir, "time.mp4"));
        var list = Item("/dash/list.mpd", Path.Combine(dir, "list.mp4"));
        m.Enqueue(v1); m.Enqueue(time); m.Enqueue(list);
        T.Check("template + $Number%03d$ download completes", await Wait(v1, DownloadStatus.Complete), v1.LastError);
        T.Check("init + 3 segments, byte-exact", T.Sha(v1.FilePath) == await Expect("dash-v1"));
        T.Check("$Time$ with SegmentTimeline (repeat + next entry) completes", await Wait(time, DownloadStatus.Complete), time.LastError);
        T.Check("segments at t=0, 2000, 4000 in order", T.Sha(time.FilePath) == await Expect("dash-time"));
        T.Check("SegmentList with BaseURL completes", await Wait(list, DownloadStatus.Complete), list.LastError);
        T.Check("init + listed segments exact", T.Sha(list.FilePath) == await Expect("dash-list"));
        T.Check("no leftovers", Leftovers(dir).Length == 0, string.Join(",", Leftovers(dir)));
    }

    static async Task DashMerge()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("  (skipped: bash stand-in for ffmpeg)"); return; }
        var dir = Sub(); var fake = MakeFakeFfmpeg(dir);
        using (var m = new DownloadManager(new MemoryStore()) { FfmpegPath = fake })
        {
            var item = Item("/dash/static.mpd", Path.Combine(dir, "merged.mp4")); m.Enqueue(item);   // no selection: best video + best audio
            T.Check("completes", await Wait(item, DownloadStatus.Complete), item.LastError);
            T.Check("best video (720p) + audio merged by ffmpeg", T.Sha(item.FilePath) == await Expect("dash-v2a1"));
            T.Check("parts removed", Leftovers(dir).Length == 0 && !Directory.GetFiles(dir, "merged.audio.*").Any(), string.Join(",", Directory.GetFiles(dir)));
        }
        await Http.GetAsync(Base + "/__reset");
        using (var m = new DownloadManager(new MemoryStore()) { FfmpegPath = "/nonexistent/ffmpeg-not-here" })
        {
            var item = Item("/dash/static.mpd", Path.Combine(dir, "nomerge.mp4")); m.Enqueue(item);
            T.Check("completes without FFmpeg", await Wait(item, DownloadStatus.Complete), item.LastError);
            T.Check("video part exact", T.Sha(item.FilePath) == await Expect("dash-v2"));
            var audio = Directory.GetFiles(dir, "nomerge.audio.*").FirstOrDefault();
            T.Check("audio part saved beside it, exact", audio != null && audio.EndsWith(".mp4") && T.Sha(audio) == await Expect("dash-a1"), audio);
            T.Check("user is told the audio needs FFmpeg", (item.LastError ?? "").Contains("FFmpeg"), item.LastError);
        }
    }

    static async Task DashRefusals()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore());
        var live = Item("/dash/live.mpd", Path.Combine(dir, "l.mp4")); var drm = Item("/dash/drm.mpd", Path.Combine(dir, "d.mp4")); var single = Item("/dash/base.mpd", Path.Combine(dir, "b.mp4"));
        var html = Item("/html", Path.Combine(dir, "h.mpd"));
        m.Enqueue(live); m.Enqueue(drm); m.Enqueue(single);
        T.Check("live => Failed 'live stream'", await Wait(live, DownloadStatus.Failed) && (live.LastError ?? "").Contains("live", StringComparison.OrdinalIgnoreCase), live.LastError);
        T.Check("DRM => Failed with DRM message", await Wait(drm, DownloadStatus.Failed) && (drm.LastError ?? "").Contains("DRM"), drm.LastError);
        T.Check("single-file layout => Failed, says it's not supported", await Wait(single, DownloadStatus.Failed) && (single.LastError ?? "").Contains("single-file"), single.LastError);
    }

    static Task UrlDetection()
    {
        T.Check(".m3u8 path", DownloadManager.IsHlsUrl("https://a.example/x/index.m3u8"));
        T.Check(".m3u8 with token", DownloadManager.IsHlsUrl("https://a.example/x/index.m3u8?token=1&e=2"));
        T.Check("m3u8 in the query", DownloadManager.IsHlsUrl("https://a.example/play?file=list.m3u8"));
        T.Check("normal files are not HLS", !DownloadManager.IsHlsUrl("https://a.example/movie.mp4") && !DownloadManager.IsHlsUrl("https://a.example/a.zip?x=1"));
        T.Check("garbage is not HLS", !DownloadManager.IsHlsUrl("not a url"));
        return Task.CompletedTask;
    }
}
