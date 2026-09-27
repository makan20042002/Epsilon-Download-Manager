using System.Diagnostics;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>YouTube and other protected sites through yt-dlp: parsing, the download supervisor (progress, pause/resume, cancel, errors), the bridge, the tools installer.</summary>
static class YtDlpTests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;
    const string Watch = "https://www.youtube.com/watch?v=abcdefghijk";

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("yt-dlp: addresses, selections, command lines, JSON and progress parsing", Parsing);
        var fake = FindFake();
        if (fake == null) { Console.WriteLine("\n== yt-dlp with a stand-in program: skipped (needs Linux/macOS and python3)"); }
        else
        {
            await Run("yt-dlp: resolving a page (qualities, audio, subtitles) and its errors", () => Resolving(fake));
            await Run("yt-dlp: a playlist is listed as its videos (a single video comes back as one entry, the same way)", () => Playlists(fake));
            await Run("yt-dlp: a download is supervised like any other (progress, merge, pause/resume, cancel, errors)", () => Downloads(fake));
            await Run("yt-dlp: the browser bridge lists choices and queues a selection", () => Bridge(fake));
        }
        await Run("Tools installer: yt-dlp, Deno and FFmpeg are downloaded, verified and unpacked", Installer);
    }

    static string? FindFake()
    {
        if (OperatingSystem.IsWindows()) return null;
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "server", "fake_ytdlp.py"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return null;
        var path = Path.Combine(dir, "server", "fake_ytdlp.py");
        try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); }   // zip files lose the executable bit
        catch (Exception) { /* read-only file system: the tests will say so */ }
        return path;
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static Task Parsing()
    {
        foreach (var good in new[] { "https://www.youtube.com/watch?v=abcdefghijk&t=5s", "https://youtu.be/abcdefghijk?si=x", "https://m.youtube.com/shorts/abcdefghijk", "https://music.youtube.com/watch?v=abcdefghijk", "https://www.youtube.com/live/abcdefghijk", "https://www.youtube-nocookie.com/embed/abcdefghijk" })
            T.Check("recognised as a YouTube video: " + good, YtDlpService.IsYouTubeUrl(good));
        foreach (var bad in new[] { "https://www.youtube.com/", "https://www.youtube.com/results?search_query=x", "https://www.youtube.com/@channel", "https://notyoutube.com/watch?v=abcdefghijk", "https://youtube.com.evil.example/watch?v=abcdefghijk", "ftp://youtube.com/watch?v=abcdefghijk", "" })
            T.Check("not a video address: " + bad, !YtDlpService.IsYouTubeUrl(bad));

        var withKey = YtDlpService.WithSelection(Watch + "#old", "v1080");
        T.Check("selection travels in the address fragment", withKey == Watch + "#makan-ytdl=v1080" && YtDlpService.TryGetSelection(withKey, out var page, out var key) && page == Watch && key == "v1080");
        T.Check("no selection in an ordinary address", !YtDlpService.TryGetSelection(Watch, out _, out _));
        T.Check("subtitle keys survive escaping", YtDlpService.TryGetSelection(YtDlpService.WithSelection(Watch, "s:en"), out _, out var sk) && sk == "s:en");

        T.Check("video plan: best mp4 up to that height, merged", YtDlpService.PlanFor("v720") is { Selector: var sel, OutputExtension: "mp4", AudioOnly: false } && sel.Contains("height<=720") && sel.Contains("+ba"));
        T.Check("audio plans", YtDlpService.PlanFor("a") is { AudioOnly: true, OutputExtension: "m4a" } && YtDlpService.PlanFor("a-mp3") is { AudioFormat: "mp3", OutputExtension: "mp3" });
        T.Check("subtitle plan", YtDlpService.PlanFor("s:fa") is { SubtitleLanguage: "fa", OutputExtension: "srt" });
        foreach (var evil in new[] { "v1080; calc", "--exec x", "../a", "", "v", "s:", "a b" })
        {
            var rejected = false; try { YtDlpService.PlanFor(evil); } catch (ArgumentException) { rejected = true; }
            T.Check("bad selection is rejected: '" + evil + "'", rejected);
        }

        var yt = new YtDlpService(@"C:\tools\yt-dlp.exe") { FfmpegPath = @"C:\tools\ffmpeg.exe", CookiesFromBrowser = "firefox" };
        var args = yt.BuildDownloadArguments(Watch + "#makan-ytdl=v720", "v720", @"C:\Videos\100% Title [720p]");
        T.Check("command line: no user config, template, output path with % escaped, ffmpeg, cookies, merge, address last",
            args[0] == "--ignore-config" && args.Contains("--progress-template") && args.Contains(@"C:\Videos\100%% Title [720p].%(ext)s") && args.Contains("--ffmpeg-location") &&
            args.Contains("--cookies-from-browser") && args.Contains("mp4/mkv") && args[^1] == Watch, string.Join(" ", args));
        T.Check("subtitle command line skips the video", yt.BuildDownloadArguments(Watch, "s:en", "x").Contains("--skip-download"));
        T.Check("audio mp3 command line extracts audio", yt.BuildDownloadArguments(Watch, "a-mp3", "x").Contains("--audio-format"));

        // ---- finding the tools
        var tools = Path.Combine(Dir, "toolsdir" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(tools);
        T.Check("no yt-dlp anywhere: null (the app then explains how to set it up)", YtDlpTools.Create(null, null, tools, null) == null || Environment.GetEnvironmentVariable("PATH")!.Contains("yt-dlp"));
        File.WriteAllText(Path.Combine(tools, "yt-dlp.exe"), "x"); File.WriteAllText(Path.Combine(tools, "ffmpeg.exe"), "x");
        var found = YtDlpTools.Create(null, null, tools, "firefox");
        T.Check("Makan's own tools folder is used, with FFmpeg and the cookie browser", found != null && found.ExePath == Path.Combine(tools, "yt-dlp.exe") && found.FfmpegPath == Path.Combine(tools, "ffmpeg.exe") && found.CookiesFromBrowser == "firefox" && found.ToolsDirectory == tools);
        var chosen = Path.Combine(Dir, "my-ytdlp.exe"); File.WriteAllText(chosen, "x");
        T.Check("a path chosen in Options wins over the tools folder", YtDlpTools.Create(chosen, null, tools, "")!.ExePath == Path.GetFullPath(chosen) && YtDlpTools.Create(chosen, null, tools, "")!.CookiesFromBrowser == null);
        T.Check("a chosen path that does not exist falls back to the tools folder", YtDlpTools.Create(Path.Combine(Dir, "nope.exe"), null, tools, null)!.ExePath == Path.Combine(tools, "yt-dlp.exe"));

        // ---- Persian texts that carry numbers
        Loc.SetLanguage("fa");
        try
        {
            T.Check("installer progress is translated (name and percent kept)", Loc.T("Downloading yt-dlp… 45%").Contains("yt-dlp") && Loc.T("Downloading yt-dlp… 45%").Contains("45") && !Loc.T("Downloading yt-dlp… 45%").StartsWith("Downloading"));
            T.Check("subtitle choices are translated", Loc.T("Subtitles: en (auto-generated)").Contains("en") && !Loc.T("Subtitles: en (auto-generated)").StartsWith("Subtitles"));
            T.Check("connection states of the progress window are translated", new[] { "Receiving data...", "Disconnect.", "Send GET...", "Download complete." }.All(x => Loc.T(x) != x));
        }
        finally { Loc.SetLanguage("en"); }

        var p = YtDlpService.ParseProgressLine("MAKAN|downloading|1048576|5242880|NA|2097152.5|3.0|137");
        T.Check("progress line", p is { Status: "downloading", Downloaded: 1048576, Total: 5242880, TotalEstimate: null, FormatId: "137" } && p.Speed == 2097152.5);
        T.Check("progress line with unknown values", YtDlpService.ParseProgressLine("MAKAN|downloading|500|NA|1000|None|NA|18") is { Total: null, TotalEstimate: 1000, Speed: null, Eta: null });
        T.Check("other output is not progress", YtDlpService.ParseProgressLine("[download] 50%") == null && YtDlpService.ParseProgressLine(null) == null);
        T.Check("error text is cleaned", YtDlpService.CleanError("ERROR: [youtube] abcdefghijk: Video unavailable") == "Video unavailable" && YtDlpService.CleanError("") .Length > 0);
        return Task.CompletedTask;
    }

    static async Task Resolving(string fake)
    {
        var yt = new YtDlpService(fake);
        var info = await yt.ResolveAsync(Watch, CancellationToken.None);
        T.Check("title and length", info.Title.StartsWith("The #1 Workout") && info.DurationSeconds == 100 && info.Uploader == "Test Channel");
        var keys = info.Options.Select(o => o.Key).ToList();
        T.Check("qualities from best to worst, one per height (thumbnails ignored)", keys.Take(3).SequenceEqual(new[] { "v1080", "v720", "v360" }), string.Join(",", keys));
        T.Check("HD label and size estimate = best video + best audio", info.Options[0].Label == "1080p HD" && info.Options[0].ApproxBytes == 9_000_000 && info.Options[1].ApproxBytes == 5_000_000, info.Options[0].ApproxBytes + " / " + info.Options[1].ApproxBytes);
        T.Check("size from the bit rate when the server gives none", info.Options[2].Label == "360p" && info.Options[2].ApproxBytes is > 0);
        T.Check("audio only (M4A, MP3)", keys.Contains("a") && keys.Contains("a-mp3") && info.Options.First(o => o.Key == "a").ApproxBytes == 1_000_000);
        T.Check("subtitles: real ones first, automatic English (and 'orig'), no live chat", keys.Contains("s:en") && keys.Contains("s:fa") && !keys.Contains("s:live_chat") && keys.Contains("s:en-orig") && !keys.Contains("s:de") && info.Options.Count(o => o.Key == "s:en") == 1);

        var failed = "";
        try { await yt.ResolveAsync("https://example.com/unsupported", CancellationToken.None); } catch (InvalidOperationException ex) { failed = ex.Message; }
        T.Check("yt-dlp's error text is passed on", failed.Contains("Unsupported URL"), failed);
        var notReady = false;
        try { await new YtDlpService(Path.Combine(Dir, "missing-yt-dlp.exe")).ResolveAsync(Watch, CancellationToken.None); } catch (YtDlpNotReadyException) { notReady = true; }
        T.Check("a missing yt-dlp is 'not set up', not a crash", notReady);
    }

    static async Task Playlists(string fake)
    {
        var yt = new YtDlpService(fake);
        var single = await yt.ResolvePlaylistAsync(Watch, CancellationToken.None);
        T.Check("a normal video page is one entry - the page itself, not fabricated as a one-video playlist", single.Entries.Count == 1 && single.Entries[0].Url == Watch && single.Entries[0].Title.StartsWith("The #1 Workout"), single.Entries.Count + " " + single.Entries.FirstOrDefault()?.Url);

        var playlist = await yt.ResolvePlaylistAsync("https://www.youtube.com/playlist?list=abc", CancellationToken.None);
        T.Check("the playlist's own title is read", playlist.Title == "Wider Shoulders - Full Series", playlist.Title);
        T.Check("a broken/removed entry (blank id and title) is skipped rather than crashing the whole listing", playlist.Entries.Count == 3, playlist.Entries.Count.ToString());
        T.Check("a full webpage_url is used as-is", playlist.Entries[0] is { Title: "Day 1 - Warm Up", Url: "https://www.youtube.com/watch?v=abc111" });
        T.Check("a full url field works the same way when webpage_url is absent", playlist.Entries[1] is { Title: "Day 2 - Shoulders", Url: "https://www.youtube.com/watch?v=abc222" });
        T.Check("a bare id (no scheme) is turned back into a real YouTube address, using the playlist's own site", playlist.Entries[2] is { Title: "Day 3 - Recovery", Url: "https://www.youtube.com/watch?v=abc333" });

        // each entry can go straight into the normal single-video pipeline once a quality is chosen for the whole playlist
        var withSelection = YtDlpService.WithSelection(playlist.Entries[0].Url, "v720");
        T.Check("an entry's address still works with the ordinary selection-in-the-URL mechanism", YtDlpService.TryGetSelection(withSelection, out var page, out var key) && page == playlist.Entries[0].Url && key == "v720");
    }

    static async Task Downloads(string fake)
    {
        var dir = Path.Combine(Dir, "yt" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { YtDlp = new YtDlpService(fake) };
        DownloadItem Make(string url, string key, string name) => new() { Url = YtDlpService.WithSelection(url, key), FilePath = Path.Combine(dir, name), AutoName = false, Connections = 1 };

        // ---- 720p: video + audio downloaded separately, then merged
        var video = Make(Watch, "v720", "Workout [720p].mp4");
        var seen = new List<double>(); m.Progress += (i, p) => { if (i == video) seen.Add(p.Percent); };
        m.Enqueue(video);
        T.Check("completes", await Program.WaitStatusPublic(video, DownloadStatus.Complete, 30), video.LastError);
        T.Check("the merged file is there, video + audio", File.Exists(video.FilePath) && new FileInfo(video.FilePath).Length == 5_000_000, video.FilePath);
        T.Check("size and progress are complete", video.TotalBytes == 5_000_000 && video.Progress == 100 && video.DoneBytes == 5_000_000);
        T.Check("progress was reported and reached 100 while two streams were fetched", seen.Count >= 2 && seen.Max() >= 99 && seen.Min() <= 90, $"{seen.Count} reports, {seen.Min():0}..{seen.Max():0}");
        T.Check("no leftovers (.part, per-stream files)", Directory.GetFiles(dir).Length == 1, string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));

        // ---- audio and subtitles
        var audio = Make(Watch + "&x=1", "a", "Workout audio.m4a");
        var mp3 = Make(Watch + "&x=2", "a-mp3", "Workout audio.mp3");   // same name as the M4A: the extension tells them apart
        var subs = Make(Watch + "&x=3", "s:en", "Workout.srt");
        m.Enqueue(audio); m.Enqueue(mp3); m.Enqueue(subs);
        T.Check("audio, mp3 and subtitles complete", await Program.WaitStatusPublic(audio, DownloadStatus.Complete, 30) && await Program.WaitStatusPublic(mp3, DownloadStatus.Complete, 30) && await Program.WaitStatusPublic(subs, DownloadStatus.Complete, 30), $"{audio.LastError}|{mp3.LastError}|{subs.LastError}");
        T.Check("audio file", File.Exists(audio.FilePath) && audio.FilePath.EndsWith(".m4a") && new FileInfo(audio.FilePath).Length == 1_000_000);
        T.Check("mp3 file", mp3.FilePath.EndsWith(".mp3") && File.Exists(mp3.FilePath));
        T.Check("subtitles: yt-dlp's name (<name>.en.srt) becomes the item's file", subs.FilePath.EndsWith("Workout.en.srt") && File.Exists(subs.FilePath), subs.FilePath);

        // ---- pause and resume (partial files are continued)
        var slow = Make("https://www.youtube.com/watch?v=slowslowslo", "v1080", "Slow [1080p].mp4");
        m.Enqueue(slow);
        var sw = Stopwatch.StartNew(); while (slow.DoneBytes < 800_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        T.Check("progress arrives while it runs", slow.DoneBytes >= 800_000 && slow.Progress > 0 && slow.Status == "Downloading", $"{slow.DoneBytes} {slow.Status}");
        m.Pause(slow);
        T.Check("paused", await Program.WaitStatusPublic(slow, DownloadStatus.Paused, 15));
        var before = Directory.GetFiles(dir, "Slow*").Length;
        T.Check("partial files stay for resuming", before >= 1 && Directory.GetFiles(dir, "Slow*").Any(f => f.EndsWith(".part")), string.Join(",", Directory.GetFiles(dir, "Slow*").Select(Path.GetFileName)));
        var resumedFrom = Directory.GetFiles(dir, "Slow*.part").Sum(f => new FileInfo(f).Length);
        m.Enqueue(slow);
        T.Check("resumes and completes", await Program.WaitStatusPublic(slow, DownloadStatus.Complete, 60), slow.LastError);
        T.Check("complete file, nothing left over", new FileInfo(slow.FilePath).Length == 5_000_000 && !Directory.GetFiles(dir, "Slow*").Any(f => f.EndsWith(".part") || f.Contains(".f1")), string.Join(",", Directory.GetFiles(dir, "Slow*").Select(Path.GetFileName)));

        // ---- cancel removes the partial files and stops the process
        var cancel = Make("https://www.youtube.com/watch?v=slowcancel1", "v720", "Cancelled [720p].mp4");
        m.Enqueue(cancel);
        var sw2 = Stopwatch.StartNew(); while (cancel.DoneBytes < 500_000 && sw2.Elapsed.TotalSeconds < 30) await Task.Delay(50);
        m.Cancel(cancel); await Program.WaitStatusPublic(cancel, DownloadStatus.Cancelled, 15);
        await Task.Delay(500);
        T.Check("cancel leaves no files", !Directory.GetFiles(dir, "Cancelled*").Any(), string.Join(",", Directory.GetFiles(dir, "Cancelled*").Select(Path.GetFileName)));
        var stillRunning = Process.GetProcesses().Count(p => { try { return p.ProcessName.Contains("python") && p.StartTime > DateTime.Now.AddSeconds(-20) && p.MainModule != null && false; } catch { return false; } });
        T.Check("(the stand-in process was stopped)", stillRunning == 0);

        // ---- failure, and not set up
        var fail = Make("https://www.youtube.com/watch?v=failfailfai", "v720", "Fail.mp4");
        m.Enqueue(fail);
        T.Check("an error of yt-dlp fails the item with yt-dlp's message", await Program.WaitStatusPublic(fail, DownloadStatus.Failed, 15) && (fail.LastError ?? "").Contains("Sign in to confirm"), fail.LastError);
        using var bare = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
        var none = Make(Watch, "v720", "None.mp4"); bare.Enqueue(none);
        T.Check("without yt-dlp the item fails with a message that says what to do", await Program.WaitStatusPublic(none, DownloadStatus.Failed, 15) && (none.LastError ?? "").Contains("Download / update tools"), none.LastError);
    }

    static async Task Bridge(string fake)
    {
        var dir = Path.Combine(Dir, "yb" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { YtDlp = new YtDlpService(fake) };
        using var bridge = new NativeBridge(manager, () => dir);
        YtDlpService? provided = null;
        bridge.YtDlpProvider = () => provided;
        var pipeless = async (BridgeRequest r) => await bridge.ProcessAsync(r, CancellationToken.None);

        var notReady = await pipeless(new BridgeRequest { Kind = "ytformats", Url = Watch });
        T.Check("not set up: the extension is told to explain it", !notReady.Ok && notReady.NeedsSetup && (notReady.Error ?? "").Contains("yt-dlp"));

        provided = new YtDlpService(fake);
        var formats = await pipeless(new BridgeRequest { Kind = "ytformats", Url = Watch });
        T.Check("choices for the menu", formats.Ok && formats.Title!.StartsWith("The #1") && formats.Options!.Select(o => o.Key).Take(2).SequenceEqual(new[] { "v1080", "v720" }) && formats.DurationSeconds == 100);
        var bad = await pipeless(new BridgeRequest { Kind = "ytformats", Url = "https://example.com/unsupported" });
        T.Check("an unsupported page is an error (not 'set up')", !bad.Ok && !bad.NeedsSetup && (bad.Error ?? "").Contains("Unsupported"));
        var notHttp = await pipeless(new BridgeRequest { Kind = "ytformats", Url = "file:///c:/x" });
        T.Check("only http(s)", !notHttp.Ok);

        StreamRequest? prompted = null;
        bridge.StreamPrompt = r => { prompted = r; return true; };
        var ask = await pipeless(new BridgeRequest { Kind = "ytdl", Url = Watch, Format = "v1080", Title = "The #1 Workout: Wider / Fast! [1080p]" });
        T.Check("with 'always ask' the app shows its dialog first, with the selection in the address", ask.Ok && ask.Pending && prompted != null && prompted.Url == Watch + "#makan-ytdl=v1080" && prompted.Format == "mp4", ask.Error);
        T.Check("the file name is made from the title", NativeBridge.StreamFileName(prompted!) == "The #1 Workout_ Wider - Fast! [1080p].mp4", NativeBridge.StreamFileName(prompted!));

        bridge.StreamPrompt = null;
        var direct = await pipeless(new BridgeRequest { Kind = "ytdl", Url = Watch, Format = "v720", Title = "Clip [720p]", NoPrompt = true });
        T.Check("queued", direct.Ok && manager.Items.Any(i => i.Id == direct.Id && i.Url == Watch + "#makan-ytdl=v720" && i.FilePath.EndsWith("Clip [720p].mp4")), direct.Error);
        var item = manager.Items.First(i => i.Id == direct.Id);
        T.Check("and downloaded", await Program.WaitStatusPublic(item, DownloadStatus.Complete, 30), item.LastError);
        var again = await pipeless(new BridgeRequest { Kind = "ytdl", Url = Watch, Format = "v720", Title = "Clip [720p]", NoPrompt = true });
        T.Check("the same selection twice is a duplicate (only while active)", again.Ok);
        var evil = await pipeless(new BridgeRequest { Kind = "ytdl", Url = Watch, Format = "--exec calc", Title = "x", NoPrompt = true });
        T.Check("a made-up format is refused", !evil.Ok);
        var subs = await pipeless(new BridgeRequest { Kind = "ytdl", Url = Watch, Format = "s:en", Title = "Clip", NoPrompt = true });
        T.Check("subtitles get a .srt name", subs.Ok && manager.Items.Any(i => i.Id == subs.Id && i.FilePath.EndsWith("Clip.srt")), subs.Error);
    }

    static async Task Installer()
    {
        var tools = Path.Combine(Dir, "tools" + Guid.NewGuid().ToString("N")[..6]);
        var installer = new ToolsInstaller(tools, new HttpClient())
        {
            YtDlpUrl = Base + "/tools/yt-dlp.exe", YtDlpSumsUrl = Base + "/tools/SHA2-256SUMS",
            DenoUrl = Base + "/tools/deno.zip", DenoSumsUrl = Base + "/tools/deno.zip.sha256sum",
            FfmpegUrl = Base + "/tools/ffmpeg-master-latest-win64-gpl.zip", FfmpegSumsUrl = Base + "/tools/checksums.sha256"
        };
        var messages = new List<string>(); var progress = new Progress<string>(messages.Add);
        await installer.InstallYtDlpAsync(progress, CancellationToken.None);
        T.Check("yt-dlp is installed after its SHA-256 sum matched", File.Exists(installer.YtDlpPath) && new FileInfo(installer.YtDlpPath).Length > 5000 && !File.Exists(installer.YtDlpPath + ".download"));
        await installer.InstallDenoAsync(progress, CancellationToken.None);
        T.Check("Deno is unpacked from its archive", File.ReadAllText(installer.DenoPath).Contains("fake deno"));
        await installer.InstallFfmpegAsync(progress, CancellationToken.None);
        T.Check("FFmpeg (and ffprobe) are unpacked from the folder inside the archive", File.ReadAllText(installer.FfmpegPath).Contains("fake ffmpeg") && File.Exists(Path.Combine(tools, "ffprobe.exe")));
        T.Check("no temporary files stay behind", !Directory.GetFiles(tools).Any(f => f.EndsWith(".download") || f.EndsWith(".tmp")), string.Join(",", Directory.GetFiles(tools).Select(Path.GetFileName)));

        var bad = new ToolsInstaller(Path.Combine(tools, "bad"), new HttpClient()) { YtDlpUrl = Base + "/tools/yt-dlp.exe", YtDlpSumsUrl = Base + "/tools/SHA2-256SUMS-bad" };
        var refused = false; try { await bad.InstallYtDlpAsync(null, CancellationToken.None); } catch (InvalidDataException) { refused = true; }
        T.Check("a file whose sum does not match is refused and not installed", refused && !File.Exists(bad.YtDlpPath));
        var noSums = new ToolsInstaller(Path.Combine(tools, "nosums"), new HttpClient()) { YtDlpUrl = Base + "/tools/yt-dlp.exe", YtDlpSumsUrl = Base + "/tools/does-not-exist" };
        await noSums.InstallYtDlpAsync(null, CancellationToken.None);
        T.Check("no published sums: installed anyway (only a wrong sum is refused)", File.Exists(noSums.YtDlpPath));
        var missing = new ToolsInstaller(Path.Combine(tools, "missing"), new HttpClient()) { YtDlpUrl = Base + "/tools/nothing.exe" };
        var failed = false; try { await missing.InstallYtDlpAsync(null, CancellationToken.None); } catch (HttpRequestException) { failed = true; }
        T.Check("a 404 is an error and leaves nothing behind", failed && !File.Exists(missing.YtDlpPath) && !Directory.GetFiles(Path.Combine(tools, "missing")).Any());
        T.Check("progress messages were reported", messages.Any(x => x.Contains("ready")));
    }
}
