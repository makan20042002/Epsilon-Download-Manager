using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>In-memory stand-in for the SQLite store. Like the real one it hands out *copies* so restarts are realistic.</summary>
sealed class MemoryStore : IDownloadStore
{
    readonly object _gate = new();
    readonly Dictionary<long, DownloadItem> _rows = new();
    long _next = 1;
    public readonly List<string> History = new();

    static DownloadItem Copy(DownloadItem x) => new()
    {
        Id = x.Id, Url = x.Url, FilePath = x.FilePath, Category = x.Category, Priority = x.Priority, Status = x.Status,
        ScheduledAt = x.ScheduledAt, TotalBytes = x.TotalBytes, DoneBytes = x.DoneBytes, Cookie = x.Cookie, Referrer = x.Referrer,
        UserAgent = x.UserAgent, ExpectedSha256 = x.ExpectedSha256, ETag = x.ETag, LastModified = x.LastModified,
        Connections = x.Connections, SpeedLimitBytesPerSec = x.SpeedLimitBytesPerSec, StatusCode = x.StatusCode,
        StartedAt = x.StartedAt, FinishedAt = x.FinishedAt, RetryCount = x.RetryCount, LastError = x.LastError
    };

    public List<DownloadItem> Load() { lock (_gate) return _rows.Values.Select(Copy).OrderBy(x => x.Id).ToList(); }
    public long Add(DownloadItem item) { lock (_gate) { var id = _next++; item.Id = id; _rows[id] = Copy(item); return id; } }
    public void Save(DownloadItem item) { lock (_gate) _rows[item.Id] = Copy(item); }
    public void Delete(long id) { lock (_gate) _rows.Remove(id); }
    public void AddHistory(DownloadItem item, string status, string? error = null) { lock (_gate) History.Add($"{item.Id}:{status}"); }
    public DownloadItem? Row(long id) { lock (_gate) return _rows.TryGetValue(id, out var r) ? Copy(r) : null; }
}
