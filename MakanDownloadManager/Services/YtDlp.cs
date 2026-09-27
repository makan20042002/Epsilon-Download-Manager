using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

/// <summary>One thing the user can pick for a video page: a quality, audio only, or a subtitle language.</summary>
public sealed record YtOption(string Key, string Label, string Kind, int? Height, string? Extension, long? ApproxBytes);

public sealed record YtVideoInfo(string Title, double? DurationSeconds, string? Uploader, IReadOnlyList<YtOption> Options);

/// <summary>One entry found in a playlist: enough to queue it, not its full format list yet (that is looked up per video,
/// same as a normal single-video add, once the person has chosen a quality for the whole playlist).</summary>
public sealed record YtPlaylistEntry(string Url, string Title);
public sealed record YtPlaylistInfo(string Title, IReadOnlyList<YtPlaylistEntry> Entries);

/// <summary>One machine-readable progress line of a yt-dlp download.</summary>
public sealed record YtProgress(string Status, long Downloaded, long? Total, long? TotalEstimate, double? Speed, double? Eta, string? FormatId);

/// <summary>What a selection key means for yt-dlp.</summary>
public sealed record YtPlan(string Key, string Selector, bool AudioOnly, string? AudioFormat, string? SubtitleLanguage, string OutputExtension);

/// <summary>yt-dlp (or Deno / FFmpeg) is missing: the user has to set it up first (Options > YouTube).</summary>
public sealed class YtDlpNotReadyException : Exception
{
    public YtDlpNotReadyException(string message) : base(message) { }
}

/// <summary>
/// YouTube (and many other video sites) protect their streams with constantly changing player code, so Makan lets the open-source
/// tool yt-dlp do the extraction and the download, and supervises it like any other download: progress, pause, resume, cancel.
/// A download of this kind is an item whose address is  &lt;page address&gt;#makan-ytdl=&lt;selection key&gt;  (v1080, v720, a, a-mp3, s:en ...).
/// </summary>
public sealed class YtDlpService
{
    public const string FragmentKey = "#makan-ytdl=";
    public const string ProgressPrefix = "MAKAN|";
    public const string ProgressTemplate = "download:" + ProgressPrefix +
        "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s";

    public string ExePath { get; }
    public string? FfmpegPath { get; set; }
    /// <summary>Folder that holds yt-dlp, Deno and FFmpeg when Makan downloaded them; put in front of PATH for the child process (yt-dlp finds Deno there).</summary>
    public string? ToolsDirectory { get; set; }
    /// <summary>"chrome" | "edge" | "firefox" | "brave" | "" (none): lets yt-dlp use the browser's login for private or age-restricted videos.</summary>
    public string? CookiesFromBrowser { get; set; }

    public YtDlpService(string exePath) => ExePath = exePath;

    // ---------------------------------------------------------------- addresses

    static readonly Regex VideoId = new("^[A-Za-z0-9_-]{6,}$", RegexOptions.Compiled);

    /// <summary>A YouTube video, short, live or embed address (not the home page, a channel or a search).</summary>
    public static bool IsYouTubeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be") return VideoId.IsMatch(uri.AbsolutePath.Trim('/').Split('/')[0]);
        var youtube = host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
        if (!youtube) return false;
        var path = uri.AbsolutePath;
        if (path.Equals("/watch", StringComparison.OrdinalIgnoreCase))
            return Regex.IsMatch(uri.Query, @"[?&]v=[A-Za-z0-9_-]{6,}");
        return Regex.IsMatch(path, @"^/(shorts|live|embed)/[A-Za-z0-9_-]{6,}", RegexOptions.IgnoreCase);
    }

    public static string WithSelection(string pageUrl, string key) => pageUrl.Split('#')[0] + FragmentKey + Uri.EscapeDataString(key);

    public static bool TryGetSelection(string? url, out string pageUrl, out string key)
    {
        pageUrl = ""; key = "";
        if (string.IsNullOrEmpty(url)) return false;
        var i = url.IndexOf(FragmentKey, StringComparison.Ordinal);
        if (i < 0) return false;
        pageUrl = url[..i];
        key = Uri.UnescapeDataString(url[(i + FragmentKey.Length)..].Split('&')[0]);
        return pageUrl.Length > 0 && key.Length > 0;
    }

    // ---------------------------------------------------------------- what a key means

    static readonly Regex KeyPattern = new(@"^(v\d{3,4}|a|a-mp3|s:[A-Za-z0-9_-]{2,16})$", RegexOptions.Compiled);

    public static YtPlan PlanFor(string key)
    {
        if (key == null || !KeyPattern.IsMatch(key)) throw new ArgumentException("Unknown format \"" + key + "\".");
        if (key[0] == 'v')
        {
            var h = int.Parse(key[1..], CultureInfo.InvariantCulture);
            return new YtPlan(key, $"bv*[height<={h}][ext=mp4]+ba[ext=m4a]/bv*[height<={h}]+ba/b[height<={h}]/b", false, null, null, "mp4");
        }
        if (key == "a") return new YtPlan(key, "ba[ext=m4a]/ba/b", true, null, null, "m4a");
        if (key == "a-mp3") return new YtPlan(key, "ba/b", true, "mp3", null, "mp3");
        return new YtPlan(key, "", false, null, key[2..], "srt");
    }

    /// <summary>The command line for one download (the address goes last).</summary>
    public IReadOnlyList<string> BuildDownloadArguments(string pageUrl, string key, string outputBaseWithoutExtension)
    {
        var plan = PlanFor(key);
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--no-warnings", "--no-colors", "--newline", "--progress",
            "--progress-template", ProgressTemplate,
            "--retries", "10", "--fragment-retries", "10", "--socket-timeout", "30", "--concurrent-fragments", "8",
            "-o", outputBaseWithoutExtension.Replace("%", "%%") + ".%(ext)s"
        };
        if (!string.IsNullOrWhiteSpace(FfmpegPath)) { args.Add("--ffmpeg-location"); args.Add(FfmpegPath!); }
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser)) { args.Add("--cookies-from-browser"); args.Add(CookiesFromBrowser!); }
        if (plan.SubtitleLanguage != null)
        {
            args.AddRange(new[] { "--skip-download", "--write-subs", "--write-auto-subs", "--sub-langs", plan.SubtitleLanguage, "--convert-subs", "srt" });
        }
        else
        {
            args.Add("-f"); args.Add(plan.Selector);
            if (plan.AudioOnly) { if (plan.AudioFormat != null) { args.Add("-x"); args.Add("--audio-format"); args.Add(plan.AudioFormat); } }
            else { args.Add("--merge-output-format"); args.Add("mp4/mkv"); }
        }
        args.Add(pageUrl.Split('#')[0]);
        return args;
    }

    public IReadOnlyList<string> BuildInfoArguments(string pageUrl)
    {
        var args = new List<string> { "--ignore-config", "-J", "--no-playlist", "--no-warnings", "--no-colors", "--socket-timeout", "30" };
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser)) { args.Add("--cookies-from-browser"); args.Add(CookiesFromBrowser!); }
        args.Add(pageUrl.Split('#')[0]);
        return args;
    }

    /// <summary>A quick, shallow listing (title and address only, no per-video format lookup - fast even for a playlist with
    /// hundreds of entries) of what a page contains. The same command works for a single video; that just comes back as one entry.</summary>
    public IReadOnlyList<string> BuildPlaylistArguments(string pageUrl)
    {
        var args = new List<string> { "--ignore-config", "-J", "--flat-playlist", "--yes-playlist", "--no-warnings", "--no-colors", "--socket-timeout", "30" };
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser)) { args.Add("--cookies-from-browser"); args.Add(CookiesFromBrowser!); }
        args.Add(pageUrl.Split('#')[0]);
        return args;
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>Turns the JSON of "yt-dlp -J" into the choices shown to the user (like IDM's list: qualities, audio, subtitles).</summary>
    public static YtVideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble()) : null;
        double? Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var title = Str(root, "title"); if (title.Length == 0) title = "video";
        var duration = Dbl(root, "duration");
        var options = new List<YtOption>();

        var videos = new List<(int Height, long? Size)>();
        long? bestAudio = null; var hasAudio = false;
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            foreach (var f in formats.EnumerateArray())
            {
                var vcodec = Str(f, "vcodec"); var acodec = Str(f, "acodec");
                var size = Num(f, "filesize") ?? Num(f, "filesize_approx");
                if (size == null && duration is > 0 && Dbl(f, "tbr") is { } tbr) size = (long)(tbr * 1000 / 8 * duration.Value);
                var hasVideo = vcodec.Length > 0 && vcodec != "none";
                if (hasVideo && Num(f, "height") is { } h && h >= 100) videos.Add(((int)h, size));
                else if (!hasVideo && acodec.Length > 0 && acodec != "none") { hasAudio = true; if (size != null && (bestAudio == null || size > bestAudio)) bestAudio = size; }
            }

        foreach (var group in videos.GroupBy(v => v.Height).OrderByDescending(g => g.Key))
        {
            var video = group.Where(v => v.Size != null).Select(v => v.Size!.Value).DefaultIfEmpty(0).Max();
            long? approx = video > 0 ? video + (bestAudio ?? 0) : null;
            options.Add(new YtOption("v" + group.Key, group.Key + "p" + (group.Key >= 720 ? " HD" : ""), "video", group.Key, "mp4", approx));
        }
        if (hasAudio)
        {
            options.Add(new YtOption("a", "Audio only (M4A)", "audio", null, "m4a", bestAudio));
            options.Add(new YtOption("a-mp3", "Audio only (MP3)", "audio", null, "mp3", null));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
            foreach (var lang in subs.EnumerateObject().Select(p => p.Name).Where(n => n != "live_chat").Take(12))
                if (seen.Add(lang)) options.Add(new YtOption("s:" + lang, "Subtitles: " + lang, "subtitle", null, "srt", null));
        if (root.TryGetProperty("automatic_captions", out var auto) && auto.ValueKind == JsonValueKind.Object)
            foreach (var lang in auto.EnumerateObject().Select(p => p.Name).Where(n => n == "en" || n.EndsWith("-orig", StringComparison.Ordinal)).Take(2))
                if (seen.Add(lang)) options.Add(new YtOption("s:" + lang, "Subtitles: " + lang + " (auto-generated)", "subtitle", null, "srt", null));

        var uploader = Str(root, "uploader");
        return new YtVideoInfo(title, duration, uploader.Length > 0 ? uploader : null, options);
    }

    static long? ParseWhole(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? (long)d : null;

    static double? ParseNumber(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : null;

    public static YtProgress? ParseProgressLine(string? line)
    {
        if (line == null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal)) return null;
        var p = line[ProgressPrefix.Length..].Split('|');
        if (p.Length < 7) return null;
        return new YtProgress(p[0].Trim(), ParseWhole(p[1]) ?? 0, ParseWhole(p[2]), ParseWhole(p[3]), ParseNumber(p[4]), ParseNumber(p[5]), p[6].Trim());
    }

    /// <summary>"ERROR: [youtube] abc: Video unavailable" -> "Video unavailable".</summary>
    public static string CleanError(string? line)
    {
        var text = (line ?? "").Trim();
        if (text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) text = text[6..].Trim();
        text = Regex.Replace(text, @"^\[[^\]]+\]\s*[A-Za-z0-9_-]{6,}:\s*", "");
        return text.Length == 0 ? "yt-dlp could not download this video." : text;
    }

    // ---------------------------------------------------------------- running yt-dlp

    ProcessStartInfo Start(IEnumerable<string> args)
    {
        if (!File.Exists(ExePath)) throw new YtDlpNotReadyException("yt-dlp was not found. Open Options > YouTube & other sites and click \"Download / update tools\".");
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (!string.IsNullOrWhiteSpace(ToolsDirectory))
            psi.Environment["PATH"] = ToolsDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return psi;
    }

    /// <summary>Asks yt-dlp what the page offers (takes a few seconds; YouTube pages need Deno for the player code).</summary>
    public async Task<YtVideoInfo> ResolveAsync(string pageUrl, CancellationToken ct)
    {
        using var process = Process.Start(Start(BuildInfoArguments(pageUrl))) ?? throw new YtDlpNotReadyException("yt-dlp could not be started.");
        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var json = await output; var err = await errors;
        if (process.ExitCode != 0)
        {
            var last = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new InvalidOperationException(CleanError(last));
        }
        return ParseInfo(json);
    }

    /// <summary>A quick look at what the address contains: one video, or a playlist of several. Cheap even for a large
    /// playlist since it does not resolve any download format - that still happens per video, once a quality is chosen
    /// for the whole playlist. A page that is not a playlist at all comes back as a single entry (the page itself).</summary>
    public async Task<YtPlaylistInfo> ResolvePlaylistAsync(string pageUrl, CancellationToken ct)
    {
        using var process = Process.Start(Start(BuildPlaylistArguments(pageUrl))) ?? throw new YtDlpNotReadyException("yt-dlp could not be started.");
        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var json = await output; var err = await errors;
        if (process.ExitCode != 0)
        {
            var last = err.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new InvalidOperationException(CleanError(last));
        }
        return ParsePlaylist(json, pageUrl);
    }

    public static YtPlaylistInfo ParsePlaylist(string json, string pageUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            var title = Str(root, "title"); if (title.Length == 0) title = "video";
            return new YtPlaylistInfo(title, new[] { new YtPlaylistEntry(pageUrl, title) });
        }

        var playlistTitle = Str(root, "title"); if (playlistTitle.Length == 0) playlistTitle = "playlist";
        var list = new List<YtPlaylistEntry>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var title = Str(entry, "title"); if (title.Length == 0) title = Str(entry, "id");
            if (title.Length == 0) continue;
            var url = ResolveEntryUrl(entry, Str, pageUrl);
            if (url != null) list.Add(new YtPlaylistEntry(url, title));
        }
        return new YtPlaylistInfo(playlistTitle, list);
    }

    /// <summary>A "flat" playlist entry gives just enough to find the real page again - a full address if it has one,
    /// otherwise an id that has to be turned back into one relative to the playlist's own site.</summary>
    static string? ResolveEntryUrl(JsonElement entry, Func<JsonElement, string, string> str, string pageUrl)
    {
        var webpage = str(entry, "webpage_url");
        if (webpage.Length > 0 && Uri.TryCreate(webpage, UriKind.Absolute, out _)) return webpage;
        var url = str(entry, "url");
        if (url.Length > 0 && Uri.TryCreate(url, UriKind.Absolute, out var absolute) && (absolute.Scheme is "http" or "https")) return url;

        var id = url.Length > 0 ? url : str(entry, "id");
        if (id.Length == 0) return null;
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && (page.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase) || page.Host == "youtu.be"))
            return "https://www.youtube.com/watch?v=" + id;
        return null;   // an id with no known way to rebuild a page address for this site: skip rather than guess wrong
    }

    /// <summary>
    /// Downloads one selection. Returns the final file. Cancelling the token stops yt-dlp; running it again continues from the partial files.
    /// </summary>
    public async Task<string> DownloadAsync(string pageUrl, string key, string outputBaseWithoutExtension, Action<YtProgress> onProgress, Action<string> onStage, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputBaseWithoutExtension))!);
        using var process = Process.Start(Start(BuildDownloadArguments(pageUrl, key, outputBaseWithoutExtension))) ?? throw new YtDlpNotReadyException("yt-dlp could not be started.");
        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        var lastError = "";
        var errors = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null) if (line.Trim().Length > 0) lastError = line.Trim();
        });
        string? text;
        while ((text = await process.StandardOutput.ReadLineAsync()) != null)
        {
            if (ParseProgressLine(text) is { } progress) onProgress(progress);
            else if (text.StartsWith('[') && Regex.IsMatch(text, @"^\[(Merger|ExtractAudio|VideoRemuxer|VideoConvertor|FixupM3u8|FixupM4a|EmbedSubtitle|ConvertSubtitles)\]")) onStage(text);
            else if (text.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) lastError = text;
        }
        await process.WaitForExitAsync(CancellationToken.None);
        await errors;
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(CleanError(lastError));
        return FindResult(outputBaseWithoutExtension, PlanFor(key))
               ?? throw new FileNotFoundException("yt-dlp finished but the file was not found next to " + outputBaseWithoutExtension + ".*");
    }

    static readonly Regex Leftover = new(@"(\.part$|\.ytdl$|\.temp\.|\.f\d+\.|\.part-Frag\d+$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The finished file: the biggest file called "&lt;base&gt;.*" that is not a partial or per-stream leftover.</summary>
    public static string? FindResult(string outputBaseWithoutExtension, YtPlan plan)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputBaseWithoutExtension))!;
        var name = Path.GetFileName(outputBaseWithoutExtension);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir).Where(f => Path.GetFileName(f).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && !Leftover.IsMatch(Path.GetFileName(f))).ToList();
        if (plan.SubtitleLanguage != null) files = files.Where(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)).ToList();
        // two selections of one video can share a name (M4A and MP3): the expected extension decides, then mp4 before mkv, then the biggest file
        var preferred = files.FirstOrDefault(f => f.EndsWith("." + plan.OutputExtension, StringComparison.OrdinalIgnoreCase))
                        ?? (plan.OutputExtension == "mp4" ? files.FirstOrDefault(f => f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) : null);
        return preferred ?? files.OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    /// <summary>Removes yt-dlp's partial and per-stream files (after Cancel / Delete).</summary>
    public static void DeleteLeftovers(string outputBaseWithoutExtension)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputBaseWithoutExtension))!;
            var name = Path.GetFileName(outputBaseWithoutExtension);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
                if (Path.GetFileName(file).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && Leftover.IsMatch(Path.GetFileName(file)))
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>Runs "yt-dlp -U" when the tool is older than <paramref name="maxAge"/> (it updates itself; YouTube changes often).</summary>
    public async Task<bool> UpdateIfOldAsync(TimeSpan maxAge, CancellationToken ct)
    {
        if (!File.Exists(ExePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(ExePath) < maxAge) return false;
        using var process = Process.Start(Start(new[] { "-U" }));
        if (process == null) return false;
        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception) { } });
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode == 0;
    }
}

/// <summary>Where Makan looks for yt-dlp, Deno and FFmpeg: the path chosen in Options, Makan's own tools folder, then PATH.</summary>
public static class YtDlpTools
{
    public static string DefaultToolsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakanDownloadManager", "tools");

    public static string? FindExecutable(string fileName, string? configured, string toolsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        var own = Path.Combine(toolsDirectory, fileName);
        if (File.Exists(own)) return own;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a bad PATH entry */ }
        }
        return null;
    }

    /// <summary>A ready service, or null when yt-dlp is not installed.</summary>
    public static YtDlpService? Create(string? configuredYtDlp, string? configuredFfmpeg, string toolsDirectory, string? cookiesBrowser)
    {
        var exe = FindExecutable("yt-dlp.exe", configuredYtDlp, toolsDirectory);
        if (exe == null) return null;
        return new YtDlpService(exe)
        {
            FfmpegPath = FindExecutable("ffmpeg.exe", configuredFfmpeg, toolsDirectory),
            ToolsDirectory = toolsDirectory,
            CookiesFromBrowser = string.IsNullOrWhiteSpace(cookiesBrowser) ? null : cookiesBrowser
        };
    }
}
