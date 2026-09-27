namespace MakanDownloadManager.Services.Torrent;

public enum PerformanceMode
{
    Balanced,
    Performance,
    MaximumPerformance
}

public sealed class TorrentPerformanceMetrics
{
    public long DownloadBytesPerSec { get; set; }
    public long UploadBytesPerSec { get; set; }
    public long PeakDownloadBytesPerSec { get; set; }
    public long PeakUploadBytesPerSec { get; set; }
    public double AverageRttMs { get; set; }
    public int ConnectedPeers { get; set; }
    public int UsefulPeers { get; set; }
    public int StalledPeers { get; set; }
    public int Seeds { get; set; }
    public int TotalCandidates { get; set; }
    public int RequestsInFlight { get; set; }
    public int RequestsFulfilled { get; set; }
    public int RequestsTimedOut { get; set; }
    public long DiskWriteBytesPerSec { get; set; }
    public long HashingBytesPerSec { get; set; }
    public int DiskQueueDepth { get; set; }
    public string DiagnosticMessage { get; set; } = "Network & storage performing normally.";
    public PerformanceMode ActiveMode { get; set; } = PerformanceMode.Performance;

    public string FormattedDownloadSpeed => FormatSpeed(DownloadBytesPerSec);
    public string FormattedUploadSpeed => FormatSpeed(UploadBytesPerSec);
    public string FormattedWriteSpeed => FormatSpeed(DiskWriteBytesPerSec);
    public string FormattedHashingSpeed => FormatSpeed(HashingBytesPerSec);

    public static string FormatSpeed(long bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 B/s";
        if (bytesPerSec < 1024) return $"{bytesPerSec} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s";
        return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F2} GB/s";
    }

    /// <summary>
    /// Computes empirical bottleneck diagnosis based strictly on measured metric data.
    /// (Section 33: Smart Bottleneck Diagnostics)
    /// </summary>
    public void EvaluateDiagnostics()
    {
        if (DiskQueueDepth >= 25 || (DiskWriteBytesPerSec > 0 && DownloadBytesPerSec > DiskWriteBytesPerSec * 1.5))
        {
            DiagnosticMessage = "Possible limitation: Disk write throughput is throttling progress.";
        }
        else if (ConnectedPeers > 0 && UsefulPeers == 0)
        {
            DiagnosticMessage = "Possible limitation: Most connected peers are currently choked or stalled.";
        }
        else if (ConnectedPeers < 3 && TotalCandidates < 5)
        {
            DiagnosticMessage = "Possible limitation: Peer discovery availability is currently limited.";
        }
        else if (RequestsTimedOut > 20 && AverageRttMs > 600)
        {
            DiagnosticMessage = "Possible limitation: High network request latency / timeout rate.";
        }
        else if (DownloadBytesPerSec > 20_000_000)
        {
            DiagnosticMessage = "Network capacity operating at high throughput.";
        }
        else
        {
            DiagnosticMessage = "Transfer network & storage operating normally.";
        }
    }
}
