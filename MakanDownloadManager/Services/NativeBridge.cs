using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services.Torrent;

namespace MakanDownloadManager.Services;

/// <summary>Something the bridge needs the desktop UI to do (the bridge itself has no WPF dependency).</summary>
public sealed record BridgeCommand(string Kind, string? Url = null, string? Cookie = null, string? Referrer = null, string? UserAgent = null);

/// <summary>A file the browser wants downloaded, handed to the UI so the user can confirm name/folder and choose Start or Later.</summary>
public sealed record DownloadPrompt(string Url, string? FileName, string? Cookie, string? Referrer, string? UserAgent, string? Mime,
                                    long? Size = null, string? ProbedName = null, string? ContentType = null, long SpeedLimitBytesPerSec = 0, int Connections = 0, string? Folder = null);

/// <summary>One link found on a web page: address, the text the user sees for it, and what it is ("link", "image" or "media").</summary>
public sealed record LinkEntry(string Url, string? Text, string? Kind);

/// <summary>Every link of a page, for IDM's "Download all links" window. Cookies are per host so a site's login never travels to another site.</summary>
public sealed record LinksPrompt(IReadOnlyList<LinkEntry> Links, IReadOnlyDictionary<string, string> Cookies, string? Referrer, string? UserAgent, string? PageTitle);

/// <summary>Several links at once (e.g. "download all links on this page").</summary>
public sealed record BatchPrompt(IReadOnlyList<string> Urls, string? Cookie, string? Referrer, string? UserAgent);

/// <summary>A video the browser wants saved: an HLS playlist (plus optional separate audio playlist) in TS or MP4.</summary>
public sealed record StreamRequest(string Url, string? AudioUrl, string? Title, string Format, string? Cookie, string? Referrer, string? UserAgent, long? Size = null);

/// <summary>One downloadable quality of a stream, as shown in the browser's "Download this video" menu.</summary>
public sealed record StreamDto(string Kind, string Quality, int? Height, long? Bitrate, string? Codec, string Url, string? AudioUrl, double? Duration = null);

public sealed class BridgeRequest
{
    public string? Kind { get; set; }
    public string? Url { get; set; }
    public string? FilePath { get; set; }
    public string? Cookie { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Mime { get; set; }
    public int Priority { get; set; } = 5;
    public string[]? Urls { get; set; }
    public List<LinkEntry>? Items { get; set; }
    public Dictionary<string, string>? Cookies { get; set; }
    public string? PageTitle { get; set; }
    public string? Title { get; set; }
    public string? Format { get; set; }
    public string? AudioUrl { get; set; }
    public long? Size { get; set; }
    /// <summary>The user asked for this download themselves (menu, popup, context menu): the browser-capture rules (file types, sites, browsers) do not apply.</summary>
    public bool Explicit { get; set; }
    /// <summary>Skip the "where to save" question (used when several qualities are queued at once).</summary>
    public bool NoPrompt { get; set; }
}

/// <summary>One choice of the "Download this video" menu for pages Makan reads through yt-dlp (YouTube etc.).</summary>
public sealed record YtOptionDto(string Key, string Label, string Kind, int? Height, string? Extension, long? Size);

public sealed class BridgeResponse
{
    public bool Ok { get; set; }
    public bool Duplicate { get; set; }
    public long Id { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public int? Count { get; set; }
    public string? Version { get; set; }
    public bool? Running { get; set; }
    public IReadOnlyList<StreamDto>? Streams { get; set; }
    /// <summary>The desktop app is asking the user (name/folder/Start/Later); nothing has started yet.</summary>
    public bool Pending { get; set; }
    /// <summary>Not an error: Makan's options say this download stays with the browser (file type, site or browser excluded).</summary>
    public bool Skipped { get; set; }
    /// <summary>Video information for a page read with yt-dlp ("ytformats").</summary>
    public string? Title { get; set; }
    public double? DurationSeconds { get; set; }
    public List<YtOptionDto>? Options { get; set; }
    /// <summary>yt-dlp (or Deno / FFmpeg) is not installed yet: the extension explains how to set it up.</summary>
    public bool NeedsSetup { get; set; }
    /// <summary>Makan's interface language ("en" / "fa"), so the browser extension can speak it too.</summary>
    public string? Language { get; set; }
    /// <summary>The resolved desktop colour theme, so browser surfaces can follow the app.</summary>
    public string? Theme { get; set; }
}

/// <summary>
/// Named-pipe server inside the desktop app. The tiny MakanNativeHost.exe (launched by the browser) relays
/// extension messages here, one JSON line in, one JSON line out.
/// </summary>
public sealed class NativeBridge : IDisposable
{
    public const string PipeName = "com.makan.downloadmanager";
    public const string Version = "1.7.1";
    public static string EffectivePipeName => Environment.GetEnvironmentVariable("EPSILON_NATIVE_PIPE") is { Length: > 0 } value ? value : PipeName;

    static readonly JsonSerializerOptions In = new() { PropertyNameCaseInsensitive = true };
    static readonly JsonSerializerOptions Out = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    readonly DownloadManager _manager;
    readonly Func<string?>? _downloadDir;
    readonly Func<string?>? _streamDir;
    readonly Func<string, string?>? _folderFor;
    readonly MediaService _media = new();

    /// <summary>
    /// When set, the desktop UI is asked to confirm name/folder for a video before it is queued (IDM-style "save as" step).
    /// Return true if the UI took over; false (or leave unset) to queue immediately in the default folder.
    /// </summary>
    public Func<StreamRequest, bool>? StreamPrompt { get; set; }

    /// <summary>Same idea for ordinary files and for batches of links. Return true when the UI took over.</summary>
    public Func<DownloadPrompt, bool>? DownloadPrompt { get; set; }
    /// <summary>Duplicates must always reach the desktop so the user can explicitly add another copy, even when the ordinary “ask first” option is off.</summary>
    public Func<DownloadPrompt, bool>? DuplicatePrompt { get; set; }
    public Func<BatchPrompt, bool>? BatchPrompt { get; set; }
    /// <summary>The "Download all links" picker (always shown, whatever the ask setting says). Return true when the UI took over.</summary>
    public Func<LinksPrompt, bool>? LinksPrompt { get; set; }

    /// <summary>True when the user wants to be asked before anything starts. Items that skip a dialog (e.g. "Download all") are then added as Stopped.</summary>
    public Func<bool>? AskBeforeStart { get; set; }

    /// <summary>The current capture rules (Options > File types). Null = capture everything the browser sends.</summary>
    public Func<CaptureRules>? CaptureRulesProvider { get; set; }
    /// <summary>The yt-dlp service (null = not set up).</summary>
    public Func<YtDlpService?>? YtDlpProvider { get; set; }
    /// <summary>"en" or "fa", added to every reply.</summary>
    public Func<string>? LanguageProvider { get; set; }
    /// <summary>The currently resolved desktop theme, added to every reply.</summary>
    public Func<string>? ThemeProvider { get; set; }
    /// <summary>Where a browser-captured magnet link or .torrent address is saved; falls back to the regular download
    /// folder when not set (or when it returns empty), the same way AppSettings.TorrentSaveFolder does.</summary>
    public Func<string>? TorrentFolderProvider { get; set; }
    readonly CancellationTokenSource _cts = new();
    readonly Task _serverTask;
    readonly DiagnosticsService _diagnostics = new();
    readonly string _pipeName;

    /// <summary>Raised on a background thread when the browser asks to show the window or open a media stream.</summary>
    public event Action<BridgeCommand>? UiCommand;

    /// <param name="downloadDir">Returns the default download folder each time a browser download arrives (so a Settings change applies immediately).</param>
    public NativeBridge(DownloadManager manager, Func<string?>? downloadDir = null, Func<string?>? streamDir = null, Func<string, string?>? folderForFile = null)
    {
        _manager = manager;
        _downloadDir = downloadDir;
        _streamDir = streamDir;
        _folderFor = folderForFile;
        _pipeName = EffectivePipeName;
        _serverTask = Task.Run(ServerLoopAsync);
    }

    async Task ServerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var connected = pipe;
                pipe = null; // ownership moves to the handler
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _diagnostics.Error("Native bridge listener error.", ex);
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await reader.ReadLineAsync(readTimeout.Token);
                if (string.IsNullOrWhiteSpace(line)) return;

                BridgeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<BridgeRequest>(line, In);
                    response = request is null ? Fail("Invalid request.") : await ProcessAsync(request, _cts.Token);
                    response.Language ??= LanguageProvider?.Invoke();
                    response.Theme ??= ThemeProvider?.Invoke();
                }
                catch (JsonException) { response = Fail("Invalid request."); }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Out));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            catch (Exception ex) { _diagnostics.Error("Native bridge request failed.", ex); }
        }
    }

    public async Task<BridgeResponse> ProcessAsync(BridgeRequest r, CancellationToken ct)
    {
        switch ((r.Kind ?? "download").Trim().ToLowerInvariant())
        {
            case "ping":
                return new BridgeResponse { Ok = true, Version = Version, Running = true };
            case "show":
                UiCommand?.Invoke(new BridgeCommand("show"));
                return new BridgeResponse { Ok = true };
            case "media":
                if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
                UiCommand?.Invoke(new BridgeCommand("media", r.Url, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent)));
                return new BridgeResponse { Ok = true };
            case "streams":
                return await ListStreamsAsync(r, ct);
            case "stream":
                return AddStream(r);
            case "ytformats":
                return await YtFormatsAsync(r, ct);
            case "ytdl":
                return AddYtDl(r);
            case "links":
                return AddLinks(r);
            case "batch":
                return AddBatch(r);
            case "link":
                return await AddAsync(r, verify: false, ct);
            default:
                return await AddAsync(r, verify: true, ct);
        }
    }

    async Task<BridgeResponse> AddAsync(BridgeRequest r, bool verify, CancellationToken ct)
    {
        // A magnet link never comes from the browser's own downloads (there is no HTTP transfer to intercept) - only from a page
        // click or a context menu, which is always an explicit request, so it always skips the probe/rules a captured HTTP file gets.
        if (DownloadManager.IsTorrentUrl(r.Url) && !IsHttpUrl(r.Url)) return AddLocalTorrent(r);   // a magnet link, or a .torrent file already on disk (opened by double-click / "Open with")
        if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
        var url = r.Url!;

        var hint = NameHint(r.FilePath);
        var existing = _manager.FindTorrentDuplicate(url) ?? _manager.FindActive(url);
        if (existing != null)
        {
            var duplicatePrompt = new DownloadPrompt(url, hint, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent), Blank(r.Mime));
            if (DuplicatePrompt?.Invoke(duplicatePrompt) == true)
                return new BridgeResponse { Ok = true, Duplicate = true, Pending = true, Id = existing.Id, FilePath = existing.FilePath };
            return new BridgeResponse { Ok = true, Duplicate = true, Id = existing.Id, FilePath = existing.FilePath };
        }

        var item = NewItem(url, hint, r);
        long? probedSize = null; string? probedName = null, probedType = null;

        // Downloads the browser started by itself follow the user's capture rules; things the user asked for never do.
        var rules = verify && !r.Explicit ? CaptureRulesProvider?.Invoke() : null;
        if (rules != null)
        {
            var early = rules.Evaluate(url, hint, r.UserAgent);
            if (!early.Capture) return new BridgeResponse { Ok = false, Skipped = true, Error = early.Reason };
        }

        if (verify)
        {
            // Prove Makan can actually fetch this URL *before* the browser is told to drop its own download.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var probe = await _manager.ProbeAsync(item, timeout.Token);
                probedSize = probe.Length; probedName = probe.FileName; probedType = probe.ContentType;
                if (rules != null && hint == null)
                {
                    var late = rules.Evaluate(url, probe.FileName, r.UserAgent);   // the server's real name may reveal an unwanted file type
                    if (!late.Capture) return new BridgeResponse { Ok = false, Skipped = true, Error = late.Reason };
                }
                var ext = Path.GetExtension(hint ?? probe.FileName ?? "").ToLowerInvariant();
                var expectsHtml = ext is ".html" or ".htm" or ".xhtml" || (r.Mime ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
                if (!expectsHtml && probe.ContentType is "text/html" or "application/xhtml+xml")
                    return Fail("The server sent a web page instead of a file (a login or session may be required).");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Some large-download CDNs take a long time to answer a second range request while the browser
                // already has the first one open. A slow preflight must not make capture fail: the real download
                // has its own retry and idle-timeout handling and can safely perform the probe after it is queued.
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex) { return Fail(ex.Message); }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        if (DownloadPrompt is { } prompt &&
            prompt(new DownloadPrompt(url, hint, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent), Blank(r.Mime), probedSize, probedName, probedType)))
            return new BridgeResponse { Ok = true, Pending = true };

        _manager.Enqueue(item);
        return new BridgeResponse { Ok = true, Id = item.Id, FilePath = item.FilePath };
    }

    async Task<BridgeResponse> ListStreamsAsync(BridgeRequest r, CancellationToken ct)
    {
        if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
        try
        {
            var found = await _media.GetStreamsAsync(r.Url!, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent), ct);
            var list = found.Select(x => new StreamDto(
                x.Url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? "dash" : "hls",
                x.Quality, x.Height, x.Bitrate, x.Codec == "Unknown" ? null : x.Codec, x.Url, x.AudioUrl, x.DurationSeconds)).ToList();

            // All qualities of a master playlist last equally long: read the smallest variant once to learn the length.
            if (list.Count > 0 && list.All(x => x.Duration == null && x.Kind == "hls"))
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(6));
                    var smallest = list.OrderBy(x => x.Bitrate ?? long.MaxValue).First();
                    var seconds = await _media.GetHlsDurationAsync(smallest.Url, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent), limit.Token);
                    if (seconds != null) list = list.Select(x => x with { Duration = seconds }).ToList();
                }
                catch (Exception) { /* the length is optional information */ }
            }
            return new BridgeResponse { Ok = true, Streams = list };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    BridgeResponse AddStream(BridgeRequest r)
    {
        if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
        if (!string.IsNullOrWhiteSpace(r.AudioUrl) && !IsHttpUrl(r.AudioUrl)) return Fail("Invalid audio URL.");
        var format = DownloadManager.IsDashUrl(r.Url!) || string.Equals(r.Format, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "ts";   // DASH is always fragmented MP4
        var request = new StreamRequest(r.Url!, Blank(r.AudioUrl), Blank(r.Title), format, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent));
        return AddStreamRequest(request, r);
    }

    BridgeResponse AddStreamRequest(StreamRequest request, BridgeRequest r)
    {
        var existing = _manager.FindActive(DownloadManager.WithAudio(request.Url, request.AudioUrl));
        if (existing != null) return new BridgeResponse { Ok = true, Duplicate = true, Id = existing.Id, FilePath = existing.FilePath };

        var ask = AskBeforeStart?.Invoke() == true;
        if (!r.NoPrompt && StreamPrompt is { } prompt && prompt(request)) return new BridgeResponse { Ok = true, Pending = true };

        var streamFolder = _streamDir?.Invoke();
        var folder = string.IsNullOrWhiteSpace(streamFolder) ? DefaultFolder() : streamFolder;
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { return Fail("Can't use the video folder: " + ex.Message); }
        var item = BuildStreamItem(request, Path.Combine(folder, StreamFileName(request)), _manager.DefaultConnections);
        _manager.Enqueue(item, start: !ask);   // "Download all" while asking: added as Stopped, the user starts them
        return new BridgeResponse { Ok = true, Id = item.Id, FilePath = item.FilePath };
    }

    /// <summary>What can be downloaded from a page yt-dlp understands (YouTube ...): qualities, audio, subtitles.</summary>
    async Task<BridgeResponse> YtFormatsAsync(BridgeRequest r, CancellationToken ct)
    {
        if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
        var yt = YtDlpProvider?.Invoke();
        if (yt == null) return new BridgeResponse { Ok = false, NeedsSetup = true, Error = "yt-dlp is not set up. Open Options > YouTube & other sites and click \"Download / update tools\"." };
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(90));
            var info = await yt.ResolveAsync(r.Url!, limit.Token);
            return new BridgeResponse
            {
                Ok = true, Title = info.Title, DurationSeconds = info.DurationSeconds,
                Options = info.Options.Select(o => new YtOptionDto(o.Key, o.Label, o.Kind, o.Height, o.Extension, o.ApproxBytes)).ToList()
            };
        }
        catch (YtDlpNotReadyException ex) { return new BridgeResponse { Ok = false, NeedsSetup = true, Error = ex.Message }; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("yt-dlp took too long to read this page."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>Queues one selection (quality / audio / subtitles) of a page that yt-dlp downloads.</summary>
    BridgeResponse AddYtDl(BridgeRequest r)
    {
        if (!IsHttpUrl(r.Url)) return Fail("Only HTTP/HTTPS URLs are accepted.");
        YtPlan plan;
        try { plan = YtDlpService.PlanFor(r.Format ?? ""); } catch (ArgumentException ex) { return Fail(ex.Message); }
        var request = new StreamRequest(YtDlpService.WithSelection(r.Url!, plan.Key), null, Blank(r.Title) ?? "video", plan.OutputExtension, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent), r.Size > 0 ? r.Size : null);
        return AddStreamRequest(request, r);
    }

    /// <summary>"My Video.mp4" style name from the page title (or the playlist URL when there is no title).</summary>
    public static string StreamFileName(StreamRequest request)
    {
        var title = request.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            try { title = Path.GetFileNameWithoutExtension(new Uri(request.Url).AbsolutePath); } catch { title = null; }
            if (string.IsNullOrWhiteSpace(title) || title.Equals("index", StringComparison.OrdinalIgnoreCase) || title.Equals("playlist", StringComparison.OrdinalIgnoreCase)) title = "video";
        }
        // page titles often contain "/" ("Shoulders / Fast!"): that is not a folder
        return DownloadFileNamer.Sanitize(title.Replace(" / ", " - ").Replace('/', '-').Replace('\\', '-').Trim().TrimEnd('.'), "video") + "." + request.Format;
    }

    public static DownloadItem BuildStreamItem(StreamRequest request, string filePath, int connections) => new()
    {
        Url = DownloadManager.WithAudio(request.Url, request.AudioUrl),
        FilePath = filePath,
        Cookie = Blank(request.Cookie),
        Referrer = Blank(request.Referrer),
        UserAgent = Blank(request.UserAgent),
        Priority = 5,
        Connections = connections,
        TotalBytes = request.Size,
        AutoName = false
    };

    string DefaultFolder()
    {
        var configured = _downloadDir?.Invoke();
        var dir = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") : configured;
        Directory.CreateDirectory(dir);
        return dir;
    }

    string TorrentFolder()
    {
        var configured = TorrentFolderProvider?.Invoke();
        var dir = string.IsNullOrWhiteSpace(configured) ? DefaultFolder() : configured;
        Directory.CreateDirectory(dir);
        return dir;
    }

    BridgeResponse AddLinks(BridgeRequest r)
    {
        // one entry per address (first wins, but a later one may supply the missing link text)
        var byUrl = new Dictionary<string, LinkEntry>(StringComparer.Ordinal);
        foreach (var e in (r.Items ?? new()).Where(e => e != null && IsHttpUrl(e.Url)).Take(4000))
        {
            var text = string.IsNullOrWhiteSpace(e.Text) ? null : e.Text.Trim();
            if (!byUrl.TryGetValue(e.Url, out var existing)) byUrl[e.Url] = e with { Text = text };
            else if (existing.Text == null && text != null) byUrl[e.Url] = existing with { Text = text };
        }
        var links = byUrl.Values.Take(3000).ToList();
        if (links.Count == 0) return Fail("No links were found on this page.");
        var cookies = (r.Cookies ?? new()).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        if (LinksPrompt is { } prompt && prompt(new LinksPrompt(links, cookies, Blank(r.Referrer), Blank(r.UserAgent), Blank(r.PageTitle))))
            return new BridgeResponse { Ok = true, Count = links.Count, Pending = true };

        // No window to choose in (tests / headless): take every non-image link.
        var ask = AskBeforeStart?.Invoke() == true;
        var added = 0;
        foreach (var link in links.Where(l => l.Kind != "image"))
        {
            if (_manager.FindActive(link.Url) != null) continue;
            var host = new Uri(link.Url).Host;
            var item = NewItem(link.Url, null, new BridgeRequest { Cookie = cookies.GetValueOrDefault(host), Referrer = r.Referrer, UserAgent = r.UserAgent });
            _manager.Enqueue(item, start: !ask);
            added++;
        }
        return new BridgeResponse { Ok = true, Count = added };
    }

    BridgeResponse AddBatch(BridgeRequest r)
    {
        var urls = (r.Urls ?? Array.Empty<string>()).Where(u => IsHttpUrl(u)).Distinct(StringComparer.Ordinal).Where(u => _manager.FindActive(u) == null).Take(500).ToList();
        if (urls.Count > 0 && BatchPrompt is { } prompt && prompt(new BatchPrompt(urls, Blank(r.Cookie), Blank(r.Referrer), Blank(r.UserAgent))))
            return new BridgeResponse { Ok = true, Count = urls.Count, Pending = true };
        foreach (var url in urls) _manager.Enqueue(NewItem(url, null, r));
        return new BridgeResponse { Ok = true, Count = urls.Count };
    }

    /// <summary>A magnet link (from a page, or forwarded from another launch of Makan), or a .torrent file already sitting on disk
    /// (double-clicked in Explorer, or "Open with Makan"). Neither is an HTTP transfer, so neither goes through the normal probe.
    /// Unlike an ordinary capture, this always starts right away: clicking a magnet link or opening a .torrent file is already
    /// a deliberate "download this" action, so there is no separate "Start or Later?" prompt for it (yet).</summary>
    BridgeResponse AddLocalTorrent(BridgeRequest r)
    {
        var url = r.Url!;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var fileUri)) url = fileUri.LocalPath;

        var result = _manager.AddTorrent(url, TorrentFolder());
        if (result.Error != null) return Fail(result.Error);
        var item = result.Item!;
        return new BridgeResponse { Ok = true, Duplicate = result.Duplicate, Id = item.Id, FilePath = item.FilePath };
    }


    DownloadItem NewItem(string url, string? hint, BridgeRequest r) => new()
    {
        Url = url,
        FilePath = ResolvePath(hint, url),
        Cookie = Blank(r.Cookie),
        Referrer = Blank(r.Referrer),
        UserAgent = Blank(r.UserAgent),
        Priority = Math.Clamp(r.Priority, 0, 10),
        Connections = _manager.DefaultConnections,
        AutoName = hint is null
    };

    static BridgeResponse Fail(string message) => new() { Ok = false, Error = message };
    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>File name suggested by the browser (directory stripped), or null when we have to guess.</summary>
    static string? NameHint(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var name = Path.GetFileName(requested.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? null : DownloadFileNamer.Sanitize(name);
    }

    string ResolvePath(string? hint, string url)
    {
        string name;
        if (hint != null) name = hint;
        else
        {
            try { name = DownloadFileNamer.Sanitize(Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath))); }
            catch { name = "download.bin"; }
        }
        var configured = _folderFor?.Invoke(name) ?? _downloadDir?.Invoke();
        var dir = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") : configured;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _serverTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
