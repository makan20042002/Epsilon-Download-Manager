namespace MakanDownloadManager.Services;

public sealed class HistoryService
{
    readonly DownloadDb _db;
    public HistoryService(DownloadDb db) => _db = db;
    public List<HistoryRow> Load(int limit = 500) => _db.LoadHistory(limit);
    public void Clear() => _db.ClearHistory();
}
