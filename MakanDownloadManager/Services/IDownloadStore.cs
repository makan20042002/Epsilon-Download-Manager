using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>Persistence used by <see cref="DownloadManager"/>. Implemented by <see cref="DownloadDb"/> (SQLite).</summary>
public interface IDownloadStore
{
    List<DownloadItem> Load();
    long Add(DownloadItem item);
    void Save(DownloadItem item);
    void Delete(long id);
    void AddHistory(DownloadItem item, string status, string? error = null);
}
