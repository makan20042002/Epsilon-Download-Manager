using System.Globalization;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

public enum PowerAction { ShutDown, Hibernate, Sleep, Restart }

/// <summary>What a queue does on its own (IDM's Scheduler tab): when to start/stop and what to do when everything is finished.</summary>
public sealed class QueueSchedule
{
    public bool StartOnStartup { get; set; }
    public bool StartAtEnabled { get; set; }
    public string StartTime { get; set; } = "23:00";          // 24-hour "HH:mm"
    public bool Daily { get; set; } = true;                    // false = once, on OnceDate
    public string? OnceDate { get; set; }                      // "yyyy-MM-dd"
    public int Days { get; set; } = 0x7F;                      // bit 0 = Sunday ... bit 6 = Saturday
    public bool StopAtEnabled { get; set; }
    public string StopTime { get; set; } = "07:30";
    public bool RetriesEnabled { get; set; }
    public int Retries { get; set; } = 3;                      // extra tries per file that failed
    public bool OpenFileEnabled { get; set; }
    public string? OpenFile { get; set; }
    public bool ExitWhenDone { get; set; }
    public bool PowerOffWhenDone { get; set; }
    public PowerAction PowerAction { get; set; } = PowerAction.ShutDown;
    public bool ForcePowerOff { get; set; }
    public string? LastStartedOn { get; set; }                 // "yyyy-MM-dd": a scheduled start fires once per day

    public QueueSchedule Clone() => JsonSerializer.Deserialize<QueueSchedule>(JsonSerializer.Serialize(this))!;
}

public sealed class DownloadQueue
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsMain { get; set; }
    /// <summary>IDM: "Download N files at the same time".</summary>
    public int MaxParallel { get; set; } = 4;
    public QueueSchedule Schedule { get; set; } = new();
    public List<long> ItemIds { get; set; } = new();
}

public interface IQueueStore { string? Load(); void Save(string json); }

/// <summary>Things a finished queue may do to the outside world; the desktop app implements them (with a cancellable countdown before power-off).</summary>
public interface IPowerActions
{
    void OpenFile(string path);
    void ExitApplication();
    /// <summary>Returns true when the power action was carried out (false = the user cancelled it).</summary>
    Task<bool> PowerOffAsync(PowerAction action, bool force);
}

/// <summary>
/// Several named queues of downloads. A queue never starts by itself unless its schedule says so: items in it wait as "Stopped"
/// until the queue is started (by hand, at a set time, or when Makan starts). A running queue feeds its items to the download engine in order,
/// retries failures, and when everything is done can open a file, exit Makan or turn the computer off.
/// </summary>
public sealed class QueueService : IDisposable
{
    const int StartWindowMinutes = 5;

    sealed class Run
    {
        public DateTime StartedAt;
        public Queue<long> Pending = new();
        public Dictionary<long, int> RetriesLeft = new();
        public bool StartedAny;
    }

    readonly DownloadManager _manager;
    readonly IQueueStore _store;
    readonly IPowerActions _power;
    readonly Func<DateTime> _now;
    readonly object _gate = new();
    readonly List<DownloadQueue> _queues = new();
    readonly Dictionary<int, Run> _runs = new();
    readonly Timer? _pumpTimer, _scheduleTimer;
    readonly DiagnosticsService _diagnostics = new();
    int _nextId = 2;
    bool _disposed;

    public event Action? Changed;
    public event Action<DownloadQueue>? QueueFinished;

    public QueueService(DownloadManager manager, IQueueStore store, IPowerActions power, Func<DateTime>? clock = null, bool autoTimers = true)
    {
        _manager = manager; _store = store; _power = power; _now = clock ?? (() => DateTime.Now);
        Load();
        _manager.QueueControlled = IsControlled;
        _manager.ItemRemoved += OnItemRemoved;
        _manager.ItemAdded += OnItemAdded;
        if (autoTimers)
        {
            _pumpTimer = new Timer(_ => SafePump(), null, 1000, 1000);
            _scheduleTimer = new Timer(_ => SafeSchedule(), null, 5000, 15000);
        }
    }

    // ---------------------------------------------------------------- queries

    public IReadOnlyList<DownloadQueue> Queues { get { lock (_gate) return _queues.ToList(); } }
    public DownloadQueue Main { get { lock (_gate) return _queues.First(q => q.IsMain); } }
    public DownloadQueue? Find(int id) { lock (_gate) return _queues.FirstOrDefault(q => q.Id == id); }
    public DownloadQueue? QueueOf(long itemId) { lock (_gate) return _queues.FirstOrDefault(q => q.ItemIds.Contains(itemId)); }
    public bool IsRunning(int id) { lock (_gate) return _runs.ContainsKey(id); }

    /// <summary>True when the item's queue is running (the queue then decides when the item starts). Never blocks: if the queue is busy the
    /// answer is "yes" and the scheduler simply asks again a second later.</summary>
    bool IsControlled(DownloadItem item)
    {
        if (!Monitor.TryEnter(_gate)) return true;
        try
        {
            var q = _queues.FirstOrDefault(x => x.ItemIds.Contains(item.Id));
            return q != null && _runs.ContainsKey(q.Id);
        }
        finally { Monitor.Exit(_gate); }
    }

    public IReadOnlyList<DownloadItem> ItemsOf(int id)
    {
        List<long> ids;
        lock (_gate) ids = _queues.FirstOrDefault(q => q.Id == id)?.ItemIds.ToList() ?? new();
        var byId = _manager.Items.ToDictionary(i => i.Id);
        return ids.Where(byId.ContainsKey).Select(i => byId[i]).ToList();
    }

    // ---------------------------------------------------------------- editing

    public DownloadQueue AddQueue(string name)
    {
        DownloadQueue q;
        lock (_gate) { q = new DownloadQueue { Id = _nextId++, Name = UniqueName(name) }; _queues.Add(q); Save(); }
        Changed?.Invoke();
        return q;
    }

    public void RenameQueue(int id, string name)
    {
        lock (_gate) { var q = _queues.FirstOrDefault(x => x.Id == id); if (q == null || q.IsMain || string.IsNullOrWhiteSpace(name)) return; q.Name = UniqueName(name.Trim(), q); Save(); }
        RefreshNames(); Changed?.Invoke();
    }

    /// <summary>Deleting a queue never deletes downloads: its items stay in the list (stopped).</summary>
    public void RemoveQueue(int id)
    {
        List<DownloadItem> active = new();
        lock (_gate)
        {
            var q = _queues.FirstOrDefault(x => x.Id == id);
            if (q == null || q.IsMain) return;
            _runs.Remove(id);
            active = ItemsOfUnsafe(q).Where(IsActive).ToList();
            _queues.Remove(q);
            Save();
        }
        foreach (var item in active) _manager.Pause(item);
        RefreshNames(); Changed?.Invoke();
    }

    List<DownloadItem> ItemsOfUnsafe(DownloadQueue q)
    {
        var byId = _manager.Items.ToDictionary(i => i.Id);
        return q.ItemIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public void SetMaxParallel(int id, int count)
    {
        lock (_gate) { var q = _queues.FirstOrDefault(x => x.Id == id); if (q == null) return; q.MaxParallel = Math.Clamp(count, 1, 16); Save(); }
        Changed?.Invoke();
    }

    public void UpdateSchedule(int id, QueueSchedule schedule)
    {
        lock (_gate) { var q = _queues.FirstOrDefault(x => x.Id == id); if (q == null) return; q.Schedule = schedule.Clone(); Save(); }
        Changed?.Invoke();
    }

    /// <summary>Puts a download into a queue (moving it out of any other). The download is stopped unless its queue is running.</summary>
    public bool AddItem(int queueId, DownloadItem item)
    {
        bool queueRunning;
        lock (_gate)
        {
            var q = _queues.FirstOrDefault(x => x.Id == queueId);
            if (q == null || item.Id == 0 || item.Status == nameof(DownloadStatus.Complete)) return false;

            foreach (var other in _queues) other.ItemIds.Remove(item.Id);
            q.ItemIds.Add(item.Id);
            item.QueueName = q.Name;
            queueRunning = _runs.ContainsKey(queueId);
            if (queueRunning && item.Status != nameof(DownloadStatus.Downloading))
            {
                item.Status = nameof(DownloadStatus.Queued);
                var run = _runs[queueId];
                if (!run.Pending.Contains(item.Id)) run.Pending.Enqueue(item.Id);
            }
            Save();
        }
        Changed?.Invoke();
        if (queueRunning) Pump();
        return true;
    }

    /// <summary>IDM's "Download later": add it to the list without starting and park it in a queue (the main one by default).</summary>
    public void AddLater(DownloadItem item, int? queueId = null)
    {
        _manager.Enqueue(item, start: false);
        AddItem(queueId ?? Main.Id, item);
    }

    public void RemoveItem(DownloadItem item)
    {
        lock (_gate) { foreach (var q in _queues) q.ItemIds.Remove(item.Id); Save(); }
        item.QueueName = null;
        Changed?.Invoke();
    }

    public void MoveItem(int queueId, long itemId, int delta)
    {
        lock (_gate)
        {
            var q = _queues.FirstOrDefault(x => x.Id == queueId); if (q == null) return;
            var i = q.ItemIds.IndexOf(itemId); var j = i + delta;
            if (i < 0 || j < 0 || j >= q.ItemIds.Count) return;
            q.ItemIds.RemoveAt(i); q.ItemIds.Insert(j, itemId);   // move, so "up 3" means up 3 places
            Save();
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- running

    static bool CanStart(DownloadItem x) => x.Status is nameof(DownloadStatus.Paused) or nameof(DownloadStatus.Queued) or nameof(DownloadStatus.Failed);   // a download the user cancelled stays cancelled
    static bool IsActive(DownloadItem x) => x.Status == nameof(DownloadStatus.Downloading);

    public void Start(int id)
    {
        var items = ItemsOf(id);
        lock (_gate)
        {
            var q = _queues.FirstOrDefault(x => x.Id == id);
            if (q == null || _runs.ContainsKey(id)) return;
            var run = new Run { StartedAt = _now() };
            foreach (var item in items.Where(CanStart)) run.Pending.Enqueue(item.Id);
            if (run.Pending.Count == 0 && !items.Any(IsActive)) return;   // nothing to do
            run.StartedAny = items.Any(IsActive);
            _runs[id] = run;
        }
        Changed?.Invoke();
        Pump();
    }

    /// <summary>Stops the queue and every download it has running. No "when done" actions.</summary>
    public void Stop(int id)
    {
        lock (_gate) { if (!_runs.Remove(id)) return; }
        foreach (var item in ItemsOf(id).Where(IsActive)) _manager.Pause(item);
        Changed?.Invoke();
    }

    /// <summary>Stops every active queue as one operation, then pauses its transfers. Removing all runs first prevents
    /// a concurrent pump tick from filling a newly freed slot with the next scheduled file.</summary>
    public void StopAll()
    {
        List<DownloadItem> active;
        lock (_gate)
        {
            if (_runs.Count == 0) return;
            var runningIds = _runs.Keys.ToHashSet();
            _runs.Clear();
            active = _queues.Where(q => runningIds.Contains(q.Id)).SelectMany(ItemsOfUnsafe).Where(IsActive).DistinctBy(x => x.Id).ToList();
        }
        foreach (var item in active) _manager.Pause(item);
        Changed?.Invoke();
    }

    /// <summary>Resumes one file without starting its whole queue. If that queue is already running, put the file back
    /// into the run's pending list; otherwise it is a normal manual start. This is especially important after a failure:
    /// the engine deliberately lets a running queue control its own files, so bypassing this method would leave the
    /// failed file saying Queued forever.</summary>
    public void ResumeItem(DownloadItem item)
    {
        var controlled = false;
        lock (_gate)
        {
            var queue = _queues.FirstOrDefault(q => q.ItemIds.Contains(item.Id));
            if (queue != null && _runs.TryGetValue(queue.Id, out var run))
            {
                controlled = true;
                if (!IsActive(item) && !run.Pending.Contains(item.Id)) run.Pending.Enqueue(item.Id);
            }
        }
        if (controlled) { Changed?.Invoke(); Pump(); }
        else _manager.Enqueue(item);
    }

    /// <summary>Called once after start-up: queues set to "start download on startup" begin.</summary>
    public void OnStartup()
    {
        foreach (var q in Queues.Where(q => q.Schedule.StartOnStartup)) Start(q.Id);
    }

    void SafePump() { try { Pump(); } catch (Exception ex) { _diagnostics.Error("Queue scheduler tick failed.", ex); } }
    void SafeSchedule() { try { CheckSchedule(_now()); } catch (Exception ex) { _diagnostics.Error("Queue schedule check failed.", ex); } }

    /// <summary>Feeds queued downloads to the engine in order, retries failures and detects "queue finished". Runs every second.</summary>
    public void Pump()
    {
        if (_disposed) return;
        List<(int Id, Run Run)> runs;
        lock (_gate) runs = _runs.Select(kv => (kv.Key, kv.Value)).ToList();

        foreach (var (id, run) in runs)
        {
            if (!IsRunning(id)) continue;   // Stop/StopAll may have removed this run after the snapshot was made.
            var q = Find(id); if (q == null) continue;
            var items = ItemsOf(id);
            var byId = items.ToDictionary(i => i.Id);
            // Only actively running transfers consume MaxParallel. Queued items are pending work,
            // not active slots. Counting Queued here previously caused a queue to starve itself.
            var active = items.Count(IsActive);

            // per-file retries (only for files this run started)
            foreach (var item in items.Where(i => i.Status == nameof(DownloadStatus.Failed)))
            {
                if (!run.RetriesLeft.TryGetValue(item.Id, out var left) || left <= 0) continue;
                run.RetriesLeft[item.Id] = left - 1;
                _manager.StartFromQueue(item);
                active++;
            }

            // feed in order
            var slots = Math.Clamp(q.MaxParallel, 1, 16) - active;
            while (slots > 0 && run.Pending.Count > 0)
            {
                if (!IsRunning(id)) break;
                var itemId = run.Pending.Dequeue();
                if (!byId.TryGetValue(itemId, out var item) || !CanStart(item)) continue;   // removed, or the user already handled it
                if (q.Schedule.RetriesEnabled) run.RetriesLeft[itemId] = Math.Max(0, q.Schedule.Retries);
                _manager.StartFromQueue(item);
                run.StartedAny = true;
                slots--; active++;
            }

            var retryPending = items.Any(i => i.Status == nameof(DownloadStatus.Failed) && run.RetriesLeft.TryGetValue(i.Id, out var l) && l > 0);
            if (run.Pending.Count == 0 && !items.Any(IsActive) && !retryPending) Finish(id, q, run);
        }
    }

    void Finish(int id, DownloadQueue q, Run run)
    {
        lock (_gate) { if (!_runs.Remove(id)) return; }
        Changed?.Invoke();
        if (!run.StartedAny) return;
        QueueFinished?.Invoke(q);
        _ = RunFinishActionsAsync(q.Schedule);
    }

    async Task RunFinishActionsAsync(QueueSchedule s)
    {
        try
        {
            if (s.OpenFileEnabled && !string.IsNullOrWhiteSpace(s.OpenFile)) { try { _power.OpenFile(s.OpenFile!); } catch (Exception ex) { new DiagnosticsService().Error($"Could not open \"{s.OpenFile}\" after the queue finished", ex); } }
            var poweredOff = false;
            if (s.PowerOffWhenDone) poweredOff = await _power.PowerOffAsync(s.PowerAction, s.ForcePowerOff);
            if (s.ExitWhenDone && !poweredOff) _power.ExitApplication();
        }
        catch (Exception ex) { _diagnostics.Error("Queue finish action failed.", ex); }
    }

    // ---------------------------------------------------------------- schedule

    static bool TryTime(string? text, out TimeSpan time) => TimeSpan.TryParseExact(text ?? "", @"h\:mm", CultureInfo.InvariantCulture, out time) || TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time);

    /// <summary>Starts queues whose "start download at" time has come and stops those whose "stop download at" time has passed.</summary>
    public void CheckSchedule(DateTime now)
    {
        var changed = false;
        foreach (var q in Queues)
        {
            var s = q.Schedule;
            bool running; Run? run;
            lock (_gate) { running = _runs.TryGetValue(q.Id, out run); }

            if (!running && s.StartAtEnabled && TryTime(s.StartTime, out var start))
            {
                var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var due = now.Date + start;
                var inWindow = now >= due && now < due.AddMinutes(StartWindowMinutes);
                var dayOk = s.Daily ? ((s.Days >> (int)now.DayOfWeek) & 1) == 1 : s.OnceDate == today;
                if (inWindow && dayOk && s.LastStartedOn != today)
                {
                    lock (_gate) { s.LastStartedOn = today; if (!s.Daily) s.StartAtEnabled = false; Save(); }
                    changed = true;
                    Start(q.Id);
                }
            }
            else if (running && run != null && s.StopAtEnabled && TryTime(s.StopTime, out var stop))
            {
                var stopAt = run.StartedAt.Date + stop;
                if (stopAt <= run.StartedAt) stopAt = stopAt.AddDays(1);
                if (now >= stopAt) { Stop(q.Id); changed = true; }
            }
        }
        if (changed) Changed?.Invoke();
    }

    // ---------------------------------------------------------------- persistence

    sealed class State { public int NextId { get; set; } = 2; public List<DownloadQueue> Queues { get; set; } = new(); }

    void Load()
    {
        State? state = null;
        try { var json = _store.Load(); if (!string.IsNullOrWhiteSpace(json)) state = JsonSerializer.Deserialize<State>(json); }
        catch (Exception ex) when (ex is JsonException or IOException) { /* start fresh */ }
        lock (_gate)
        {
            if (state != null) { _queues.AddRange(state.Queues); _nextId = Math.Max(2, state.NextId); }
            if (!_queues.Any(q => q.IsMain)) _queues.Insert(0, new DownloadQueue { Id = 1, Name = "Main download queue", IsMain = true });
            _nextId = Math.Max(_nextId, _queues.Max(q => q.Id) + 1);
        }
        // forget downloads that no longer exist, label the ones that do
        var byId = _manager.Items.ToDictionary(i => i.Id);
        lock (_gate)
            foreach (var q in _queues)
            {
                q.ItemIds.RemoveAll(id => !byId.ContainsKey(id));
                foreach (var id in q.ItemIds) byId[id].QueueName = q.Name;
            }
    }

    void Save()
    {
        try { _store.Save(JsonSerializer.Serialize(new State { NextId = _nextId, Queues = _queues })); }
        catch (Exception) { /* best effort */ }
    }

    void RefreshNames()
    {
        var byId = _manager.Items.ToDictionary(i => i.Id);
        List<(long Id, string Name)> named;
        lock (_gate) named = _queues.SelectMany(q => q.ItemIds.Select(id => (id, q.Name))).ToList();
        var inQueue = named.Select(n => n.Id).ToHashSet();
        foreach (var (id, name) in named) if (byId.TryGetValue(id, out var item)) item.QueueName = name;
        foreach (var item in byId.Values.Where(i => !inQueue.Contains(i.Id))) item.QueueName = null;
    }

    string UniqueName(string name, DownloadQueue? self = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? "New queue" : name.Trim();
        var candidate = name; var n = 2;
        while (_queues.Any(q => q != self && string.Equals(q.Name, candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{name} ({n++})";
        return candidate;
    }

    /// <summary>A download added as "stopped" (Download later, Download all, a batch...) waits in the main queue unless someone puts it elsewhere.</summary>
    void OnItemAdded(DownloadItem item)
    {
        if (item.Id != 0 && item.Status == nameof(DownloadStatus.Paused) && QueueOf(item.Id) == null) AddItem(Main.Id, item);
    }

    void OnItemRemoved(DownloadItem item)
    {
        var touched = false;
        lock (_gate) { foreach (var q in _queues) touched |= q.ItemIds.Remove(item.Id); if (touched) Save(); }
        if (touched) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pumpTimer?.Dispose(); _scheduleTimer?.Dispose();
        _manager.ItemRemoved -= OnItemRemoved;
        _manager.ItemAdded -= OnItemAdded;
    }
}
