namespace MakanDownloadManager.Services;

/// <summary>Keeps the queues (names, schedules, which downloads belong to which) as one JSON setting in the existing database: no schema change.</summary>
public sealed class DbQueueStore : IQueueStore
{
    readonly DownloadDb _db;
    public DbQueueStore(DownloadDb db) => _db = db;
    public string? Load() => _db.Get("queues_json");
    public void Save(string json) => _db.Set("queues_json", json);
}
