using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using MakanDownloadManager.Services;

namespace MakanDownloadManager.Models;

public enum DownloadStatus
{
    Queued, Downloading, Paused, Complete, Failed, Cancelled
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string? Cookie { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public int Priority { get; set; } = 5;
    public DateTime? ScheduledAt { get; set; }
    long? _total;
    public long? TotalBytes { get => _total; set { _total = value; Changed(); Changed(nameof(SizeDisplay)); Changed(nameof(StatusText)); } }
    public long DoneBytes { get; set; }
    long _diskLoadedBytes;
    /// <summary>In-memory only: bytes recovered from existing partial files when this run resumed.</summary>
    public long DiskLoadedBytes { get => _diskLoadedBytes; set { _diskLoadedBytes = Math.Max(0, value); Changed(); Changed(nameof(DiskLoadPercent)); Changed(nameof(DiskLoadText)); } }
    public double DiskLoadPercent => TotalBytes is > 0 ? Math.Clamp(DiskLoadedBytes * 100.0 / TotalBytes.Value, 0, 100) : 0;
    public string DiskLoadText => DiskLoadedBytes > 0 ? Loc.F("Loaded {0} from disk", FormatBytes(DiskLoadedBytes)) : Loc.T("No saved data loaded from disk");
    /// <summary>Configured (maximum) connections for this download.</summary>
    public int Connections { get; set; } = 4;
    public long SpeedLimitBytesPerSec { get; set; }
    public int StatusCode { get; set; }
    DateTime? _started, _finished;
    public DateTime? StartedAt { get => _started; set { _started = value; Changed(); Changed(nameof(LastTry)); } }
    public DateTime? FinishedAt { get => _finished; set { _finished = value; Changed(); Changed(nameof(LastTry)); } }
    int _retryCount;
    public int RetryCount { get => _retryCount; set { _retryCount = value; Changed(); } }

    /// <summary>In-memory only: the file name was guessed, so the server's Content-Disposition may replace it on first start.</summary>
    public bool AutoName { get; set; }

    /// <summary>In-memory only: current transfer rate, for dashboards (avoids parsing <see cref="SpeedText"/>).</summary>
    public long SpeedBytesPerSec { get; set; }

    /// <summary>In-memory only: an informational message to show once a download completes (e.g. "saved as .ts, FFmpeg not found").</summary>
    public string? CompletionNote { get; set; }

    /// <summary>In-memory only: replace an existing file of the same name instead of choosing a new name ("Overwrite" for duplicate links).</summary>
    public bool Overwrite { get; set; }

    // ---- IDM's "Options on completion" and "Speed Limiter" tabs of the progress window (kept in memory while Makan runs)
    /// <summary>Overrides Options > Downloads > "download complete window" for this download (null = follow the option).</summary>
    public bool? ShowCompleteDialog { get; set; }
    /// <summary>Close Makan when this download is finished.</summary>
    public bool ExitWhenDone { get; set; }
    /// <summary>Turn the computer off (with the 60-second warning) when this download is finished.</summary>
    public Services.PowerAction? PowerWhenDone { get; set; }
    public bool ForcePowerAction { get; set; }
    /// <summary>Torrents: upload limit for this torrent in bytes per second (0 = the global limit only). Kept while Makan runs.</summary>
    public long UploadLimitBytesPerSec { get; set; }
    /// <summary>The speed limit was set for this run only: it is dropped when the download is stopped.</summary>
    public bool LimitIsTemporary { get; set; }

    string _filePath = "";
    string _category = "General";
    string? _lastError;
    double _progress;
    string _status = DownloadStatus.Queued.ToString();
    string _speed = "0 B/s";
    string _size = "Unknown";
    string _eta = "—";
    string _upSpeed = "";
    string _peers = "";
    int _activeConnections;

    public string FilePath { get => _filePath; set { _filePath = value; Changed(); Changed(nameof(FileName)); Changed(nameof(CategoryName)); Changed(nameof(TypeLabel)); Changed(nameof(TypeBadge)); Changed(nameof(TypeKind)); Changed(nameof(Glyph)); } }
    public string FileName => Path.GetFileName(_filePath);
    public string Category { get => _category; set { _category = value; Changed(); } }
    public string? LastError { get => _lastError; set { _lastError = value; Changed(); Changed(nameof(Description)); } }
    /// <summary>Connections actually in use right now (0 when idle).</summary>
    public int ActiveConnections { get => _activeConnections; set { _activeConnections = value; Changed(); } }

    public double Progress { get => _progress; set { _progress = Math.Clamp(value, 0, 100); Changed(); Changed(nameof(StatusText)); Changed(nameof(RowStatusText)); } }
    public string Status { get => _status; set { _status = value; Changed(); Changed(nameof(StatusText)); Changed(nameof(RowStatusText)); Changed(nameof(TimeLeft)); Changed(nameof(Rate)); } }
    public string SpeedText { get => _speed; set { _speed = value; Changed(); Changed(nameof(Rate)); } }
    public string SizeText { get => _size; set { _size = value; Changed(); Changed(nameof(SizeDisplay)); } }
    public string EtaText { get => _eta; set { _eta = value; Changed(); Changed(nameof(TimeLeft)); } }
    /// <summary>Torrents only - blank for an ordinary HTTP download. "0 B/s" while seeding/downloading with nothing
    /// going out yet, blank (not zero) once a torrent finishes and stops seeding entirely.</summary>
    public string UpSpeedText { get => _upSpeed; set { _upSpeed = value; Changed(); } }
    /// <summary>Torrents only - "connected seeds (known peers)", the same format uTorrent/qBittorrent use. Blank for
    /// an ordinary HTTP download, which has no concept of peers.</summary>
    public string PeersText { get => _peers; set { _peers = value; Changed(); } }

    // ---- IDM-style list columns (read-only, derived) -------------------------------------------------------------
    public string CategoryName => CategoryService.For(FileName);
    /// <summary>The row badge (FILE / VIDEO / TORRENT) and a language-independent kind for its colour.</summary>
    public string TypeBadge => TypeLabel.ToUpperInvariant();
    public string TypeKind => DownloadManager.IsTorrentUrl(Url) ? "torrent" : CategoryName == "Video" ? "video" : "file";
    public string TypeLabel => DownloadManager.IsTorrentUrl(Url) ? Loc.T("Torrent") : CategoryName == "Video" ? Loc.T("Video") : Loc.T("File");
    public string Glyph => CategoryName switch { "Video" => "🎞", "Music" => "🎵", "Documents" => "📄", "Programs" => "💿", "Compressed" => "🗜", "General" => "📁", _ => "📂" };
    string? _queueName;
    /// <summary>Name of the queue (scheduler) this download belongs to, if any. Set by <see cref="Services.QueueService"/>.</summary>
    public string? QueueName { get => _queueName; set { _queueName = value; Changed(); Changed(nameof(QueueMark)); } }
    public string QueueMark => QueueName ?? "";
    string? _note;
    /// <summary>A line of live information shown in the Description column while there is no error (torrents: speeds, peers, ratio).</summary>
    public string? Note { get => _note; set { _note = value; Changed(); Changed(nameof(Description)); Changed(nameof(RowStatusText)); } }
    public string Description => LastError is { Length: > 0 } ? Loc.T(LastError) : Note ?? "";
    public string TimeLeft => Status == nameof(DownloadStatus.Downloading) && EtaText != "—" ? EtaText : "";
    public string Rate => Status == nameof(DownloadStatus.Downloading) ? SpeedText : "";
    public string LastTry => (FinishedAt ?? StartedAt)?.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    public string SizeDisplay => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : (DoneBytes > 0 ? FormatBytes(DoneBytes) : "");
    public string StatusText
    {
        get
        {
            var percent = Progress.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            return Status switch
            {
                nameof(DownloadStatus.Downloading) => TotalBytes is > 0 ? percent : Loc.T("Downloading"),
                nameof(DownloadStatus.Complete) => Loc.T("Complete"),
                nameof(DownloadStatus.Paused) => Progress > 0 ? Loc.F("Stopped {0}", percent) : Loc.T("Stopped"),
                nameof(DownloadStatus.Queued) => Progress > 0 ? Loc.F("Queued {0}", percent) : Loc.T("Queued"),
                nameof(DownloadStatus.Failed) => Loc.T("Error"),
                nameof(DownloadStatus.Cancelled) => Loc.T("Cancelled"),
                _ => Status
            };
        }
    }
    public string RowStatusText
    {
        get
        {
            if (Note?.StartsWith("Seeding", StringComparison.OrdinalIgnoreCase) == true)
            {
                var ratio = Note.IndexOf("ratio ", StringComparison.OrdinalIgnoreCase);
                return ratio >= 0 ? Loc.T("Seeding") + " · " + Note[(ratio + 6)..] : Loc.T("Seeding");
            }
            return StatusText;
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double value = bytes / 1024.0; var unit = "KB";
        if (value >= 1024) { value /= 1024; unit = "MB"; }
        if (value >= 1024) { value /= 1024; unit = "GB"; }
        if (value >= 1024) { value /= 1024; unit = "TB"; }
        return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
