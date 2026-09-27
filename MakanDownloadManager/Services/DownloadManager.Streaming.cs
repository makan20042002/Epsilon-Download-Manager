using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>
/// A multi-connection download pre-allocates the whole file up front and fills it in out of order (whichever chunk a
/// worker happens to finish first), so the file on disk is full-size from the very start but not safe to open directly -
/// a media player could seek past what has actually arrived and hit blank space. This looks at the same chunk map the
/// download itself checkpoints every few seconds and works out how many bytes, counted from the very start of the file,
/// are verified complete with nothing missing in between - the only portion safe to hand to a player. It then copies
/// just that portion into a small temporary file, which is what actually gets opened.
/// </summary>
public sealed partial class DownloadManager
{
    static readonly HashSet<string> StreamableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".aac" };

    /// <summary>Whether this item is a plain, non-segmented-manifest HTTP file (not HLS/DASH, not a torrent) whose
    /// extension looks like something a media player can open.</summary>
    public static bool LooksStreamable(DownloadItem item) =>
        !IsTorrentUrl(item.Url) && StreamableExtensions.Contains(Path.GetExtension(item.FilePath));

    /// <summary>How many bytes, from the start of the file, are confirmed fully downloaded with no gap - the part that
    /// is safe to open in a player. 0 if nothing is safely playable yet (including once the file is already complete,
    /// where the real file itself should be opened directly instead of this).</summary>
    public long SafeStreamablePrefixBytes(DownloadItem item)
    {
        if (Is(item, DownloadStatus.Complete)) return 0;   // the finished file is already safe to open as itself
        try
        {
            var mapPath = SegMapPath(item);
            var dataPath = SegPath(item);
            if (File.Exists(mapPath) && File.Exists(dataPath))
            {
                var map = LoadMap(mapPath, item.TotalBytes ?? new FileInfo(dataPath).Length);
                if (map == null) return 0;
                long safe = 0;
                for (var i = 0; i < map.Count; i++)
                {
                    var length = map.LengthOf(i);
                    if (map.Done[i] < length) break;   // the first gap ends the safe prefix, regardless of what finished after it
                    safe += length;
                }
                return safe;
            }
            var partPath = PartBase(item) + ".part";
            if (File.Exists(partPath)) return new FileInfo(partPath).Length;   // a single connection always writes in order from byte 0
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _diagnostics.Error("Could not read how much of a download is safely playable", ex); }
        return 0;
    }

    /// <summary>Copies the safe portion into a small temporary file and returns its path, ready to hand to a media
    /// player - or null if there is not enough of it yet, or the copy failed. The caller deletes it once done; a stale
    /// leftover from an earlier attempt at the same item's path is replaced each time so play always shows the latest.</summary>
    public string? PreparePlayableCopy(DownloadItem item, long minimumBytes = 512 * 1024)
    {
        var safe = SafeStreamablePrefixBytes(item);
        if (safe < minimumBytes) return null;
        var source = File.Exists(SegPath(item)) ? SegPath(item) : PartBase(item) + ".part";
        if (!File.Exists(source)) return null;

        var dir = Path.Combine(Path.GetTempPath(), "MakanDownloadManager", "preview");
        try { Directory.CreateDirectory(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var target = Path.Combine(dir, item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(item.FilePath));
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[1024 * 1024];
            long remaining = safe;
            while (remaining > 0)
            {
                var n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n == 0) break;
                dst.Write(buffer, 0, n);
                remaining -= n;
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics.Error("Could not prepare the downloaded-so-far copy for playback", ex);
            try { if (File.Exists(target)) File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }
}
