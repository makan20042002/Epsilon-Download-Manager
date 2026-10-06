using System.Collections.Concurrent;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services.Torrent;

namespace MakanDownloadManager.Services;

// BitTorrent items: a magnet link or a .torrent file is a download like any other (queue, pause, list), run by the torrent engine.
public sealed partial class DownloadManager
{
    readonly ConcurrentDictionary<long, TorrentSession> _torrentSessions = new();
    TorrentEngine? _torrents;

    /// <summary>Creates the torrent engine the first time a torrent is used (so nobody who never uses torrents gets a listening port).</summary>
    public Func<TorrentEngine>? TorrentEngineFactory { get; set; }
    public TorrentEngine? Torrents { get => _torrents; set => _torrents = value; }
    /// <summary>Seed finished torrents again when Makan starts.</summary>
    public bool TorrentSeedOnStart { get; set; } = true;

    // ---- schedule: separate windows for actively downloading vs. seeding, pushed in from AppSettings (App.ApplySettings)
    public bool TorrentDownloadScheduleEnabled { get; set; }
    public TimeSpan TorrentDownloadStart { get; set; }
    public TimeSpan TorrentDownloadStop { get; set; }
    public bool TorrentSeedScheduleEnabled { get; set; }
    public TimeSpan TorrentSeedStart { get; set; }
    public TimeSpan TorrentSeedStop { get; set; }
    readonly HashSet<long> _scheduleHeldDownloads = new();
    readonly HashSet<long> _scheduleHeldSeeds = new();
    readonly HashSet<long> _forcedTorrents = new();

    /// <summary>Same window a start/stop time makes anywhere else in Makan: a plain range, or - when the stop time is earlier
    /// than the start time - a range that crosses midnight (23:00 to 07:00 means "from 11pm to 7am the next day").</summary>
    static bool InTimeWindow(TimeSpan start, TimeSpan stop, TimeSpan now) =>
        start == stop || (start < stop ? now >= start && now < stop : now >= start || now < stop);

    /// <summary>Pauses an actively-downloading torrent outside its allowed hours and resumes it once they return (only the ones
    /// the schedule itself paused - a torrent the person paused by hand stays paused until they resume it); does the same for
    /// seeding, on its own separate hours, since sharing is a different activity from downloading and often wanted at different
    /// times (e.g. only overnight, to not compete with everyday internet use). Takes the time to check as a parameter, the way
    /// QueueService.CheckSchedule does, so a test can drive it without waiting on the real clock.</summary>
    public void CheckTorrentSchedule(DateTime now)
    {
        var tod = now.TimeOfDay;
        var downloadOk = !TorrentDownloadScheduleEnabled || InTimeWindow(TorrentDownloadStart, TorrentDownloadStop, tod);
        var seedOk = !TorrentSeedScheduleEnabled || InTimeWindow(TorrentSeedStart, TorrentSeedStop, tod);

        foreach (var item in Items)
        {
            if (!IsTorrentUrl(item.Url)) continue;
            if (_forcedTorrents.Contains(item.Id)) continue;   // Force Download: this one ignores the schedule entirely, in both directions
            if (!Is(item, DownloadStatus.Complete))
            {
                if (!downloadOk && Is(item, DownloadStatus.Downloading)) { _scheduleHeldDownloads.Add(item.Id); Pause(item); }
                else if (downloadOk && _scheduleHeldDownloads.Remove(item.Id) && Is(item, DownloadStatus.Paused)) Enqueue(item);
            }
            else if (_torrentSessions.TryGetValue(item.Id, out var session))
            {
                if (!seedOk && session.State == TorrentState.Seeding) { _scheduleHeldSeeds.Add(item.Id); _ = session.PauseAsync(); }
                else if (seedOk && _scheduleHeldSeeds.Remove(item.Id) && session.State == TorrentState.Paused) session.Start();
            }
        }
    }

    public bool IsForced(DownloadItem item) => _forcedTorrents.Contains(item.Id);

    /// <summary>Force Download: starts a torrent right now and keeps it running regardless of the download/seed
    /// schedule, the same way uTorrent's/qBittorrent's "Force Start" works - a deliberate, per-torrent override, not
    /// a change to the schedule itself. Turning it back off returns the torrent to obeying the schedule normally.</summary>
    public void SetForced(DownloadItem item, bool forced)
    {
        if (!IsTorrentUrl(item.Url)) return;
        if (forced)
        {
            _forcedTorrents.Add(item.Id);
            _scheduleHeldDownloads.Remove(item.Id);
            _scheduleHeldSeeds.Remove(item.Id);
            if (!Is(item, DownloadStatus.Complete)) { if (!Is(item, DownloadStatus.Downloading)) Enqueue(item); }
            else if (_torrentSessions.TryGetValue(item.Id, out var session) && session.State == TorrentState.Paused) session.Start();
        }
        else _forcedTorrents.Remove(item.Id);
    }

    TorrentEngine RequireEngine() => _torrents ??= TorrentEngineFactory?.Invoke() ?? throw new InvalidOperationException("BitTorrent is not available.");

    public static bool IsTorrentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (MagnetLink.IsMagnet(url)) return true;
        var path = url.Trim();
        var q = path.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) path = path[..q];
        return path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    public TorrentSession? SessionOf(DownloadItem item) => _torrentSessions.TryGetValue(item.Id, out var s) ? s : null;

    /// <summary>An existing list entry for this exact magnet/.torrent address that is still meaningful to reuse - including one that
    /// finished downloading and is now only seeding, since the engine would otherwise hand two different list items the very same
    /// underlying session. Cancelled and failed entries do not count: those are fine to retry as a fresh item.</summary>
    public DownloadItem? FindTorrentDuplicate(string url) =>
        IsTorrentUrl(url) ? _items.Values.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.Ordinal) && !Is(x, DownloadStatus.Cancelled) && !Is(x, DownloadStatus.Failed)) : null;

    public sealed record AddTorrentResult(DownloadItem? Item, bool Duplicate, string? Error);

    /// <summary>Adds a magnet link, or an existing .torrent file (a plain local path - not fetched over HTTP), as a new download.
    /// The one place both the browser bridge and the main window's own "Add Torrent" go through, so the two behave identically.
    /// <paramref name="start"/> false = added as "Download later" (IDM-style): it waits, stopped, until the user starts it.</summary>
    public AddTorrentResult AddTorrent(string urlOrPath, string saveFolder, bool start = true)
    {
        urlOrPath = urlOrPath.Trim();
        if (!IsTorrentUrl(urlOrPath)) return new AddTorrentResult(null, false, "That is not a magnet link or a .torrent file.");
        var existing = FindTorrentDuplicate(urlOrPath);
        if (existing != null) return new AddTorrentResult(existing, true, null);

        string name;
        if (MagnetLink.TryParse(urlOrPath, out var link))
            name = DownloadFileNamer.Sanitize(string.IsNullOrWhiteSpace(link!.Name) ? link.InfoHashHex : link.Name!);
        else
        {
            if (!File.Exists(urlOrPath)) return new AddTorrentResult(null, false, "That .torrent file could not be found.");
            try { name = DownloadFileNamer.Sanitize(MetaInfo.Parse(File.ReadAllBytes(urlOrPath)).Name); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return new AddTorrentResult(null, false, "That .torrent file could not be read."); }
        }

        try { Directory.CreateDirectory(saveFolder); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new AddTorrentResult(null, false, ex.Message); }
        var item = new DownloadItem { Url = urlOrPath, FilePath = Path.Combine(saveFolder, name), AutoName = false, Priority = 5, Connections = DefaultConnections };
        Enqueue(item, start);
        return new AddTorrentResult(item, false, null);
    }

    async Task TorrentDownloadAsync(DownloadItem item, CancellationToken ct)
    {
        var engine = RequireEngine();
        var saveDir = Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!;
        var session = await OpenTorrentAsync(item, engine, saveDir, ct).ConfigureAwait(false);
        _torrentSessions[item.Id] = session;
        ApplyTorrentLimits(item, session);
        item.ActiveConnections = 0;
        session.Start();
        var applied = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!applied && session.Meta != null) { ApplyTorrentMeta(item, session, saveDir); applied = true; }
                ApplyTorrentLimits(item, session);
                UpdateTorrentItem(item, session);
                if (session.State == TorrentState.Error) throw new IOException(session.Error ?? "The torrent failed.");
                if (session.Completed.IsCompleted) { await session.Completed.ConfigureAwait(false); break; }
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await session.PauseAsync().ConfigureAwait(false);          // keeps the verified pieces and the resume data
            throw;
        }
        if (!applied) ApplyTorrentMeta(item, session, saveDir);
        item.TotalBytes = session.WantedBytes;
        Report(item, session.BytesDone, 0);
    }

    async Task<TorrentSession> OpenTorrentAsync(DownloadItem item, TorrentEngine engine, string saveDir, CancellationToken ct)
    {
        if (MagnetLink.TryParse(item.Url, out var link)) return engine.AddMagnet(link!, saveDir);
        var bytes = await LoadTorrentFileAsync(item, ct).ConfigureAwait(false);
        return engine.AddTorrent(MetaInfo.Parse(bytes), saveDir);
    }

    /// <summary>The bytes of a .torrent file from a web address (with the browser's cookies) or from the disk.</summary>
    public async Task<byte[]> LoadTorrentFileAsync(DownloadItem item, CancellationToken ct)
    {
        var url = item.Url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(url)) return await File.ReadAllBytesAsync(url, ct).ConfigureAwait(false);
        using var request = BuildRequest(HttpMethod.Get, item);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw RetryPolicy.StatusError(response);
        if (response.Content.Headers.ContentLength > 20 * 1024 * 1024) throw new InvalidDataException("That .torrent file is far too large.");
        var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (data.Length > 20 * 1024 * 1024) throw new InvalidDataException("That .torrent file is far too large.");
        return data;
    }

    void ApplyTorrentLimits(DownloadItem item, TorrentSession session)
    {
        if (session.DownloadLimit.Rate != item.SpeedLimitBytesPerSec) session.DownloadLimit.Rate = item.SpeedLimitBytesPerSec;
        if (session.UploadLimit.Rate != item.UploadLimitBytesPerSec) session.UploadLimit.Rate = item.UploadLimitBytesPerSec;
    }

    void ApplyTorrentMeta(DownloadItem item, TorrentSession session, string saveDir)
    {
        var meta = session.Meta!;
        var content = meta.IsMultiFile ? Path.Combine(saveDir, meta.Name) : Path.Combine(saveDir, meta.Files[0].Path);
        item.AutoName = false;
        if (!string.Equals(item.FilePath, content, StringComparison.OrdinalIgnoreCase)) item.FilePath = content;
        item.TotalBytes = session.WantedBytes;
        _store.Save(item);
    }

    void UpdateTorrentItem(DownloadItem item, TorrentSession session)
    {
        item.ActiveConnections = session.PeerCount;
        if (session.WantedBytes > 0) item.TotalBytes = session.WantedBytes;
        Report(item, session.BytesDone, session.DownloadRate);
        item.Note = TorrentNote(session);
        if (session.State == TorrentState.Metadata) item.EtaText = "Getting torrent info…";
        else if (session.State == TorrentState.Checking) item.EtaText = $"Checking {session.CheckProgress:0}%";
    }

    static string TorrentNote(TorrentSession s) => s.State switch
    {
        TorrentState.Metadata => $"Looking for peers to get the torrent info · {s.PeerCount} connected",
        TorrentState.Checking => $"Checking files {s.CheckProgress:0}%",
        TorrentState.Seeding => $"Seeding · ↑ {Format(s.UploadRate)}/s · {s.PeerCount} peers ({s.SeedCount} seeds) · ratio {s.Ratio:0.00}",
        TorrentState.Finished => s.UploadedTotal > 0 ? $"Finished · uploaded {FormatSizeShort(s.UploadedTotal)} · ratio {s.Ratio:0.00}" : "Finished · not sharing (open it and choose Resume to seed)",
        TorrentState.Paused => "Paused",
        _ => $"↓ {Format(s.DownloadRate)}/s · ↑ {Format(s.UploadRate)}/s · {s.PeerCount} peers ({s.SeedCount} seeds) · ratio {s.Ratio:0.00}"
    };

    static string FormatSizeShort(long bytes) => Format(bytes);

    /// <summary>Once a second: torrents that finished but are still being shared show how the sharing goes.</summary>
    void RefreshTorrentNotes()
    {
        foreach (var (id, session) in _torrentSessions)
        {
            if (!_items.TryGetValue(id, out var item)) continue;
            item.UpSpeedText = session.UploadRate > 0 ? Format(session.UploadRate) + "/s" : "";
            item.PeersText = $"{session.SeedCount} ({session.KnownPeers})";
            if (!Is(item, DownloadStatus.Complete)) continue;
            item.Note = TorrentNote(session);
            item.SpeedText = session.UploadRate > 0 ? "↑ " + Format(session.UploadRate) + "/s" : "";
        }
    }

    /// <summary>Removes a torrent item's session; <paramref name="deleteFiles"/> also deletes the downloaded files.</summary>
    async Task RemoveTorrentAsync(DownloadItem item, bool deleteFiles)
    {
        if (_torrentSessions.TryRemove(item.Id, out var session))
        {
            if (_torrents != null) await _torrents.RemoveAsync(session, deleteFiles).ConfigureAwait(false);
            return;
        }
        if (!deleteFiles || !MagnetLink.TryParse(item.Url, out var link)) return;
        // after a restart there is no session: the stored metadata still tells which files belong to the torrent
        try
        {
            var engine = RequireEngine();
            var s = engine.AddMagnet(link!, Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!);
            await engine.RemoveAsync(s, s.Meta != null).ConfigureAwait(false);
        }
        catch (Exception ex) { _diagnostics.Error("Could not delete the torrent's files.", ex); }
    }

    /// <summary>Starts sharing the finished torrents again (their pieces are verified quickly from the saved position).</summary>
    public void ResumeTorrentSeeding()
    {
        if (!TorrentSeedOnStart || _disposed) return;
        foreach (var item in Items.Where(i => Is(i, DownloadStatus.Complete) && MagnetLink.TryParse(i.Url, out _)))
        {
            try
            {
                MagnetLink.TryParse(item.Url, out var link);
                var engine = RequireEngine();
                var session = engine.AddMagnet(link!, Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!);
                if (session.Meta == null) { _ = engine.RemoveAsync(session, false); continue; }        // no stored metadata: cannot seed without fetching it
                _torrentSessions[item.Id] = session;
                session.Start();
            }
            catch (Exception ex) { _diagnostics.Error("Could not resume seeding.", ex); }
        }
    }

    /// <summary>Progress-window rows for a torrent: one per peer.</summary>
    IReadOnlyList<ConnectionInfo>? TorrentConnections(DownloadItem item)
    {
        if (!_torrentSessions.TryGetValue(item.Id, out var s)) return null;
        return s.Peers().Take(16).Select((p, i) => new ConnectionInfo(i + 1, p.Downloaded, $"{p.Address}  {p.Client}  ↓ {Format(p.DownRate)}/s  ↑ {Format(p.UpRate)}/s", p.Address.Contains(':') ? "IPv6 peer" : "IPv4 peer")).ToList();
    }
}
