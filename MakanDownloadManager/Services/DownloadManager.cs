using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

public enum SpeedLimitScope
{
    Combined,
    PerDownload
}

/// <summary>
/// Owns every <see cref="DownloadItem"/> in memory (the UI binds to these same instances), schedules the queue,
/// and runs transfers. The transfer engine itself lives in DownloadManager.Engine.cs.
/// </summary>
public sealed partial class DownloadManager : IDisposable
{
    public const int MaxConnectionsPerDownload = 16;
    const int MaxRetries = RetryPolicy.MaxAttempts - 1;

    enum Intent { None, Pause, Cancel }

    sealed class Job
    {
        public readonly CancellationTokenSource Cts = new();
        public volatile Intent Intent;
        public Task? Task;
    }

    readonly IDownloadStore _store;
    readonly HttpClient _http;
    readonly ConcurrentDictionary<long, DownloadItem> _items = new();
    readonly ConcurrentDictionary<long, Job> _jobs = new();
    readonly ConcurrentDictionary<long, RateLimiter> _localLimiters = new();
    readonly ConcurrentDictionary<long, LiveState> _live = new();
    readonly RateLimiter _globalLimiter = new();
    readonly Timer _scheduler;
    readonly object _stateGate = new();
    readonly DiagnosticsService _diagnostics = new();
    public AdaptiveConnectionService AdaptiveConnections { get; } = new();
    /// <summary>V13 persistent per-server learning controller. Falls back to the legacy adaptive controller when unset.</summary>
    public SmartDownloadController? SmartController { get; set; }
    int _maxActive = 4;
    int _defaultConnections = 8;
    long _globalLimit;
    int _speedLimitScope;
    volatile bool _disposed;

    public int MaxActive { get => Volatile.Read(ref _maxActive); set => Volatile.Write(ref _maxActive, Math.Clamp(value, 1, 16)); }
    public int DefaultConnections { get => Volatile.Read(ref _defaultConnections); set => Volatile.Write(ref _defaultConnections, Math.Clamp(value, 1, MaxConnectionsPerDownload)); }
    /// <summary>Where partial files (.part / .seg) live while downloading; empty = next to the final file. A download that already has parts keeps using them.</summary>
    public string? TempDirectory { get; set; }
    /// <summary>After a download completes, give the file the server's Last-Modified date (IDM: "Set file creation date as provided by the server").</summary>
    public bool SetFileDateFromServer { get; set; }
    /// <summary>Resume even if the server says the file changed (only a different size still restarts).</summary>
    public bool IgnoreModifiedOnResume { get; set; }
    /// <summary>User-Agent used when a download has none of its own (downloads added by hand).</summary>
    public string? DefaultUserAgent { get; set; }
    /// <summary>yt-dlp, for YouTube and other sites Makan cannot read on its own (set when the tool is installed; null = not set up).</summary>
    public YtDlpService? YtDlp { get; set; }
    /// <summary>V13 adaptive connection selection. When disabled, configured connections are used.</summary>
    public bool AdaptiveConnectionsEnabled { get; set; } = true;

    /// <summary>The number of connections to use for this download: the chosen count, lowered for hosts that are known to throttle.</summary>
    internal int SuggestConnections(DownloadItem item, int wanted)
    {
        if (!AdaptiveConnectionsEnabled || !Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return wanted;
        var count = AdaptiveConnections.Suggest(uri, wanted);
        if (SmartController != null) count = Math.Min(count, SmartController.Suggest(uri, wanted));
        return Math.Clamp(count, 1, wanted);
    }
    /// <summary>Optional V13 rule engine applied immediately before a new item is persisted.</summary>
    public DownloadRuleEngine? RuleEngine { get; set; }
    /// <summary>
    /// Asks the queue service whether a queued item belongs to a queue that is running right now. A running queue feeds its own items
    /// (StartFromQueue) and enforces its "N files at the same time"; every other queued item - for instance one the user starts by hand
    /// from the progress window - is started by the normal scheduler. Must never block (it is called under the scheduler lock).
    /// </summary>
    public Func<DownloadItem, bool>? QueueControlled { get; set; }

    /// <summary>The limit selected in the toolbar. Depending on <see cref="LimitScope"/>, this is either shared by all
    /// ordinary downloads or applied independently to every ordinary download.</summary>
    public long GlobalLimitBytesPerSec
    {
        get => Interlocked.Read(ref _globalLimit);
        set
        {
            var v = Math.Max(0, value);
            Interlocked.Exchange(ref _globalLimit, v);
            _globalLimiter.Limit = LimitScope == SpeedLimitScope.Combined ? v : 0;
            RefreshLocalLimiterRates();
        }
    }

    public SpeedLimitScope LimitScope
    {
        get => (SpeedLimitScope)Volatile.Read(ref _speedLimitScope);
        set
        {
            var normalized = value == SpeedLimitScope.PerDownload ? SpeedLimitScope.PerDownload : SpeedLimitScope.Combined;
            Volatile.Write(ref _speedLimitScope, (int)normalized);
            _globalLimiter.Limit = normalized == SpeedLimitScope.Combined ? GlobalLimitBytesPerSec : 0;
            RefreshLocalLimiterRates();
        }
    }

    void RefreshLocalLimiterRates()
    {
        foreach (var (id, limiter) in _localLimiters)
        {
            if (_items.TryGetValue(id, out var item)) limiter.Limit = EffectiveLocalLimit(item);
        }
    }

    long EffectiveLocalLimit(DownloadItem item)
    {
        var itemLimit = Math.Max(0, item.SpeedLimitBytesPerSec);
        var toolbarLimit = LimitScope == SpeedLimitScope.PerDownload ? GlobalLimitBytesPerSec : 0;
        if (itemLimit == 0) return toolbarLimit;
        if (toolbarLimit == 0) return itemLimit;
        return Math.Min(itemLimit, toolbarLimit);
    }

    /// <summary>Changes one file's own cap and wakes it immediately if it is currently waiting at the previous rate.</summary>
    public void SetItemSpeedLimit(DownloadItem item, long bytesPerSecond)
    {
        item.SpeedLimitBytesPerSec = Math.Max(0, bytesPerSecond);
        if (_localLimiters.TryGetValue(item.Id, out var limiter)) limiter.Limit = EffectiveLocalLimit(item);
    }

    /// <summary>Replaces the "is any network connected" check (tests).</summary>
    public Func<bool>? NetworkUpProbe { get; set; }
    bool IsNetworkUp()
    {
        try { return (NetworkUpProbe ?? System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable)(); }
        catch (Exception) { return true; }
    }

    /// <summary>Raised (from any thread) when a download is created.</summary>
    public event Action<DownloadItem>? ItemAdded;
    /// <summary>Raised (from any thread) after an item is removed.</summary>
    public event Action<DownloadItem>? ItemRemoved;
    /// <summary>Raised when a download completes or fails, for tray notifications.</summary>
    public event Action<DownloadItem, string>? Notification;
    public event Action<DownloadItem, DownloadProgress>? Progress;

    readonly bool _autoResume;

    /// <param name="autoResumeUnfinished">When false, downloads that were running or queued when the app closed come back as "Stopped": nothing ever starts without the user asking.</param>
    public DownloadManager(IDownloadStore store, HttpMessageHandler? handler = null, bool autoResumeUnfinished = true)
    {
        _store = store;
        _autoResume = autoResumeUnfinished;
        _http = new HttpClient(handler ?? new SafeRedirectHandler(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false, // SafeRedirectHandler follows redirects without leaking cookies across sites
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UseProxy = true, Proxy = new LiveProxy(this)
        }), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var item in _store.Load())
        {
            if (Is(item, DownloadStatus.Downloading))
            {
                Set(item, _autoResume ? DownloadStatus.Queued : DownloadStatus.Paused);
                item.LastError = _autoResume ? "Recovered after application restart." : null;
                _store.Save(item);
            }
            else if (!_autoResume && Is(item, DownloadStatus.Queued))
            {
                Set(item, DownloadStatus.Paused);
                _store.Save(item);
            }
            RefreshDisplay(item);
            _items[item.Id] = item;
        }
        _scheduler = new Timer(_ => SafeTick(), null, 500, 1000);
    }

    /// <summary>Snapshot of all items ordered by id.</summary>
    public IReadOnlyList<DownloadItem> Items => _items.Values.OrderBy(x => x.Id).ToList();

    /// <summary>An unfinished download (queued, running or paused) for the same URL, if any.</summary>
    public DownloadItem? FindActive(string url) =>
        _items.Values.FirstOrDefault(x => string.Equals(x.Url, url, StringComparison.Ordinal) &&
            (Is(x, DownloadStatus.Queued) || Is(x, DownloadStatus.Downloading) || Is(x, DownloadStatus.Paused)));

    /// <param name="start">false = add it to the list without starting (IDM's "Download later"): it waits as Stopped until the user resumes it.</param>
    /// <summary>Starts a queued item only when QueueService has granted a slot. This prevents the global scheduler from bypassing queue MaxParallel.</summary>
    public void StartFromQueue(DownloadItem item)
    {
        if (_disposed) return;
        Job? job = null;
        lock (_stateGate)
        {
            if (item.Id == 0 || !_items.ContainsKey(item.Id) || _jobs.ContainsKey(item.Id)) return;
            if (!Is(item, DownloadStatus.Queued) && !Is(item, DownloadStatus.Paused) && !Is(item, DownloadStatus.Failed)) return;
            job = new Job();
            if (!_jobs.TryAdd(item.Id, job)) return;
            Set(item, DownloadStatus.Downloading);
        }
        job.Task = Task.Run(() => RunAsync(item, job));
    }

    public void Enqueue(DownloadItem item, bool start = true)
    {
        if (_disposed) return;
        var added = false;
        lock (_stateGate)
        {
            if (item.Id == 0)
            {
                RuleEngine?.Apply(item);
                if (!item.Overwrite && !IsTorrentUrl(item.Url)) item.FilePath = DownloadFileNamer.MakeUnique(item.FilePath, p => IsPathClaimed(item, p));
                item.Category = CategoryService.For(item.FilePath);
                Set(item, start ? DownloadStatus.Queued : DownloadStatus.Paused);
                item.Id = _store.Add(item);
                _items[item.Id] = item;
                added = true;
            }
            else
            {
                _items.TryAdd(item.Id, item);
                if (_jobs.ContainsKey(item.Id)) return;
                if (Is(item, DownloadStatus.Complete)) ResetFinalState(item);
                Set(item, DownloadStatus.Queued);
                item.FinishedAt = null;
                item.LastError = null;
                _store.Save(item);
            }
        }
        if (added) ItemAdded?.Invoke(item);
        SafeTick();
    }

    public void Pause(DownloadItem item)
    {
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(item.Id, out var job))
            {
                job.Intent = Intent.Pause;
                job.Cts.Cancel();
                return;
            }
            if (Is(item, DownloadStatus.Queued))
            {
                Set(item, DownloadStatus.Paused);
                _store.Save(item);
            }
        }
    }

    public void Cancel(DownloadItem item)
    {
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(item.Id, out var job))
            {
                job.Intent = Intent.Cancel;
                job.Cts.Cancel();
                return;
            }
            if (Is(item, DownloadStatus.Complete) || Is(item, DownloadStatus.Cancelled)) return;
            FinishCancelled(item);
        }
    }

    public void Retry(DownloadItem item)
    {
        lock (_stateGate)
        {
            if (_jobs.ContainsKey(item.Id)) return;
            item.RetryCount = 0;
            item.LastError = null;
            item.FinishedAt = null;
            Set(item, DownloadStatus.Queued);
            _store.Save(item);
        }
        SafeTick();
    }

    /// <summary>Stops the download, removes its library entry and optionally deletes data from disk.
    /// The UI can preserve unfinished data for an explicit "remove only" choice.</summary>
    public async Task RemoveAsync(DownloadItem item, bool deleteFile, bool preservePartialData = false)
    {
        Task? running = null;
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(item.Id, out var job))
            {
                job.Intent = Intent.Cancel;
                job.Cts.Cancel();
                running = job.Task;
            }
        }
        if (running != null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        if (!preservePartialData) DeletePartialFiles(item);
        DownloadStateManifest.Delete(item);
        if (IsTorrentUrl(item.Url)) await RemoveTorrentAsync(item, deleteFile);          // a torrent's files are its own list, never "the folder"
        else if (deleteFile) TryDelete(item.FilePath);
        _items.TryRemove(item.Id, out _);
        try { _store.Delete(item.Id); } catch (Exception ex) { _diagnostics.Error($"Could not delete #{item.Id} from the database.", ex); }
        ItemRemoved?.Invoke(item);
    }

    /// <summary>Deletes the downloaded/partial payload but keeps the library entry so it can be downloaded again.</summary>
    public async Task DeleteDataKeepEntryAsync(DownloadItem item)
    {
        Task? running = null;
        lock (_stateGate)
        {
            if (_jobs.TryGetValue(item.Id, out var job))
            {
                job.Intent = Intent.Cancel;
                job.Cts.Cancel();
                running = job.Task;
            }
        }
        if (running != null)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
        if (IsTorrentUrl(item.Url)) await RemoveTorrentAsync(item, true).ConfigureAwait(false);
        else
        {
            DeletePartialFiles(item);
            TryDelete(item.FilePath);
        }
        DownloadStateManifest.Delete(item);
        item.DoneBytes = 0;
        item.DiskLoadedBytes = 0;
        item.Progress = 0;
        item.FinishedAt = null;
        item.LastError = null;
        Set(item, DownloadStatus.Paused);
        _store.Save(item);
    }

    void SafeTick()
    {
        try { Tick(); RefreshTorrentNotes(); CheckTorrentSchedule(DateTime.Now); CheckNetworkBoost(); }
        catch (Exception ex) { _diagnostics.Error("Download scheduler tick failed.", ex); }
    }

    /// <summary>Off unless the person turns it on in Options; pushed in from AppSettings the same way the torrent
    /// schedule settings are (App.ApplySettings).</summary>
    public bool BoostDownloadSpeedEnabled { get; set; }
    NetworkBoost? _networkBoost;
    public bool BoostActive => _networkBoost?.Active ?? false;
    public string? BoostError => _networkBoost?.LastError;

    /// <summary>Pauses Windows Update / BITS / Delivery Optimization for as long as something is actively downloading,
    /// and only then - never while everything is idle, paused, or only seeding a torrent.</summary>
    void CheckNetworkBoost()
    {
        if (!BoostDownloadSpeedEnabled) { if (_networkBoost is { Active: true } b) b.Resume(); return; }
        var anyDownloading = Items.Any(i => Is(i, DownloadStatus.Downloading));
        _networkBoost ??= new NetworkBoost();
        if (anyDownloading) _networkBoost.Pause(); else _networkBoost.Resume();
    }

    void Tick()
    {
        if (_disposed) return;
        List<(DownloadItem Item, Job Job)> toStart = new();
        lock (_stateGate)
        {
            var now = DateTime.UtcNow;
            // MaxActive limits downloads started on their own; a queue decides how many of ITS files run at once.
            var loose = _jobs.Keys.Count(id => !(_items.TryGetValue(id, out var running) && running.QueueName != null));
            foreach (var item in _items.Values
                         .Where(x => Is(x, DownloadStatus.Queued))
                         .OrderByDescending(x => x.Priority)
                         .ThenBy(x => x.Id))
            {
                // A running queue owns its items and grants their slots itself: the global scheduler must not bypass its MaxParallel.
                // Anything else (also a queue item started by hand while its queue is stopped) is started here.
                var inQueue = item.QueueName != null;
                var forcedTorrent = IsTorrentUrl(item.Url) && _forcedTorrents.Contains(item.Id);
                if (inQueue && (QueueControlled?.Invoke(item) ?? false)) continue;
                if (!inQueue && !forcedTorrent && loose >= MaxActive) continue;   // Force Download means "ignore the usual limits for this one", MaxActive included
                if (item.ScheduledAt.HasValue && item.ScheduledAt.Value.ToUniversalTime() > now) continue;
                var job = new Job();
                if (!_jobs.TryAdd(item.Id, job)) continue;
                // Claim it under the lock so Pause/Cancel/another tick can never race the start.
                Set(item, DownloadStatus.Downloading);
                toStart.Add((item, job));
                if (!inQueue) loose++;
            }
        }
        foreach (var (item, job) in toStart)
            job.Task = Task.Run(() => RunAsync(item, job));
    }

    async Task RunAsync(DownloadItem item, Job job)
    {
        var ct = job.Cts.Token;
        try
        {
            item.StartedAt ??= DateTime.UtcNow;
            item.FinishedAt = null;
            item.LastError = null;
            _store.Save(item);

            Exception? last = null;
            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    item.RetryCount = attempt;
                    await DownloadAsync(item, ct);
                    last = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (RetryPolicy.IsNetworkFailure(ex) && !IsNetworkUp())
                {
                    // No network at all (cable out, Wi-Fi off, waking from sleep): that is not the download's fault.
                    // Wait for a connection instead of using up the retries and failing; Pause/Stop still work.
                    last = ex;
                    item.LastError = "Waiting for the network connection…";
                    _store.Save(item);
                    _diagnostics.Error($"Download #{item.Id} is waiting for the network to come back", ex);
                    while (!IsNetworkUp()) await Task.Delay(1000, ct);
                    await Task.Delay(1500, ct);   // give the adapter a moment to get its address
                    item.LastError = null;
                    attempt--;
                }
                catch (Exception ex) when (attempt < MaxRetries && RetryPolicy.IsTransient(ex))
                {
                    last = ex;
                    item.RetryCount = attempt + 1;
                    item.LastError = $"Retrying: {ex.Message}";
                    _store.Save(item);
                    _diagnostics.Error($"Transient download error for #{item.Id}, retry {attempt + 1}", ex);
                    await Task.Delay(RetryPolicy.DelayFor(ex, attempt), ct);
                }
                catch (Exception ex)
                {
                    last = ex;
                    break;
                }
            }

            if (last != null) ExceptionDispatchInfo.Capture(last).Throw();

            if (AdaptiveConnectionsEnabled && Uri.TryCreate(item.Url, UriKind.Absolute, out var successUri)) AdaptiveConnections.ReportSuccess(successUri);
            SmartController?.ReportSuccess(item, item.StartedAt.HasValue ? DateTime.UtcNow - item.StartedAt.Value : TimeSpan.FromSeconds(1));
            Set(item, DownloadStatus.Complete);
            item.DoneBytes = item.TotalBytes ?? item.DoneBytes;
            item.Progress = 100;
            item.SpeedText = "0 B/s"; item.SpeedBytesPerSec = 0;
            item.EtaText = "Complete";
            item.SizeText = FormatSize(item);
            item.ActiveConnections = 0;
            item.FinishedAt = DateTime.UtcNow;
            item.LastError = item.CompletionNote;
            item.CompletionNote = null;
            _store.Save(item);
            _store.AddHistory(item, "Complete");
            _diagnostics.Info($"Download #{item.Id} completed: {item.FilePath}");
            Notification?.Invoke(item, "Download complete");
        }
        catch (OperationCanceledException)
        {
            item.ActiveConnections = 0;
            item.SpeedText = "0 B/s"; item.SpeedBytesPerSec = 0;
            switch (job.Intent)
            {
                case Intent.Cancel:
                    FinishCancelled(item);
                    break;
                case Intent.Pause:
                    if (item.LimitIsTemporary) { item.SpeedLimitBytesPerSec = 0; item.LimitIsTemporary = false; }   // "remember" was not ticked
                    Set(item, DownloadStatus.Paused);
                    item.EtaText = "—";
                    item.FinishedAt = null;
                    _store.Save(item);
                    break;
                default: // application is shutting down: resume automatically next launch (unless the user turned that off)
                    Set(item, _autoResume ? DownloadStatus.Queued : DownloadStatus.Paused);
                    item.FinishedAt = null;
                    _store.Save(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var failedUri) && (item.StatusCode == 429 || item.StatusCode == 503 || ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("503", StringComparison.OrdinalIgnoreCase))) { if (AdaptiveConnectionsEnabled) AdaptiveConnections.ReportThrottle(failedUri); SmartController?.ReportThrottle(failedUri, item.Connections); }
            Set(item, DownloadStatus.Failed);
            item.ActiveConnections = 0;
            item.SpeedText = "0 B/s"; item.SpeedBytesPerSec = 0;
            item.EtaText = "—";
            item.FinishedAt = DateTime.UtcNow;
            item.LastError = ex.Message;
            _store.Save(item);
            _store.AddHistory(item, "Failed", ex.Message);
            _diagnostics.Error($"Download #{item.Id} failed", ex);
            Notification?.Invoke(item, "Download failed");
        }
        finally
        {
            _jobs.TryRemove(item.Id, out _);
            _localLimiters.TryRemove(item.Id, out var limiter);
            limiter?.Dispose();
            _live.TryRemove(item.Id, out _);
            SafeTick();
        }
    }

    void FinishCancelled(DownloadItem item)
    {
        DeletePartialFiles(item);
        DownloadStateManifest.Delete(item);
        Set(item, DownloadStatus.Cancelled);
        item.DoneBytes = 0;
        item.Progress = 0;
        item.SpeedText = "0 B/s"; item.SpeedBytesPerSec = 0;
        item.EtaText = "—";
        item.ActiveConnections = 0;
        item.FinishedAt = DateTime.UtcNow;
        _store.Save(item);
        _store.AddHistory(item, "Cancelled");
    }

    bool IsPathClaimed(DownloadItem self, string path) =>
        _items.Values.Any(x => !ReferenceEquals(x, self) && !Is(x, DownloadStatus.Cancelled) &&
            string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase));

    static bool Is(DownloadItem item, DownloadStatus status) => item.Status == status.ToString();
    static void Set(DownloadItem item, DownloadStatus status) => item.Status = status.ToString();

    static void RefreshDisplay(DownloadItem item)
    {
        item.Progress = item.TotalBytes is > 0 ? item.DoneBytes * 100.0 / item.TotalBytes.Value : 0;
        item.SizeText = FormatSize(item);
        if (Is(item, DownloadStatus.Complete)) { item.Progress = 100; item.EtaText = "Complete"; }
    }

    static string FormatSize(DownloadItem item) =>
        item.TotalBytes.HasValue ? $"{Format(item.DoneBytes)} / {Format(item.TotalBytes.Value)}" : Format(item.DoneBytes);

    static void ResetFinalState(DownloadItem item)
    {
        item.DoneBytes = 0; item.TotalBytes = null; item.Progress = 0; item.SpeedText = "0 B/s"; item.SpeedBytesPerSec = 0; item.SizeText = "Unknown";
        item.EtaText = "—"; item.LastError = null; item.StartedAt = null; item.FinishedAt = null; item.RetryCount = 0;
    }

    static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }

    static string FormatEta(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" :
        $"{Math.Max(0, t.Seconds)}s";

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.Dispose();
        if (_networkBoost is { Active: true } boost) boost.Resume();   // never leave Windows Update paused after Makan closes
        var tasks = new List<Task>();
        foreach (var job in _jobs.Values)
        {
            job.Cts.Cancel();
            if (job.Task != null) tasks.Add(job.Task);
        }
        if (tasks.Count > 0)
        {
            // ConfigureAwait(false): the application waits for this on its UI thread, so the continuation must not need that thread.
            try { await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false); } catch { /* process shutdown must never hang */ }
        }
        if (_torrents != null) { try { await _torrents.ShutdownAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); } catch { } }
        _globalLimiter.Dispose();
        foreach (var limiter in _localLimiters.Values) limiter.Dispose();
        _http.Dispose();
        DisposeLinkClients();
    }

    /// <summary>Stops the scheduler and every transfer, waiting up to six seconds for them to save their position (they resume next time).</summary>
    public void Dispose() => ShutdownAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();

    sealed class RateLimiter : IDisposable
    {
        readonly object _gate = new();
        long _nextTicks;
        long _limit;
        bool _disposed;
        CancellationTokenSource _changed = new();

        public long Limit
        {
            get { lock (_gate) return _limit; }
            set
            {
                CancellationTokenSource? wake = null;
                lock (_gate)
                {
                    var next = Math.Max(0, value);
                    if (_disposed || next == _limit) return;
                    _limit = next;
                    // A reservation made at the old rate is no longer valid. Wake current waiters so they can reserve
                    // again using the new rate instead of sitting at 0 B/s until the old delay expires.
                    _nextTicks = 0;
                    wake = _changed;
                    _changed = new CancellationTokenSource();
                }
                wake.Cancel();
            }
        }

        public async Task WaitAsync(int bytes, CancellationToken ct)
        {
            while (true)
            {
                TimeSpan wait;
                CancellationToken changed;
                lock (_gate)
                {
                    if (_disposed || _limit <= 0) return;
                    var now = Stopwatch.GetTimestamp();
                    var start = Math.Max(now, _nextTicks);
                    _nextTicks = start + (long)(bytes / (double)_limit * Stopwatch.Frequency);
                    wait = TimeSpan.FromSeconds(Math.Max(0, (start - now) / (double)Stopwatch.Frequency));
                    changed = _changed.Token;
                }
                if (wait <= TimeSpan.Zero) return;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, changed);
                try { await Task.Delay(wait, linked.Token); return; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && changed.IsCancellationRequested)
                {
                    // The user changed the rate or scope. Recalculate immediately.
                }
            }
        }

        public void Dispose()
        {
            CancellationTokenSource wake;
            lock (_gate) { if (_disposed) return; _disposed = true; _nextTicks = 0; wake = _changed; }
            wake.Cancel();
        }
    }
}
