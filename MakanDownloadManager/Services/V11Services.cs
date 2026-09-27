using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>V11 duplicate analysis used by the add-download UI and diagnostics.</summary>
public static class DuplicateDetector
{
    public enum MatchKind { None, SameUrl, SamePath, SameHash }
    public sealed record Match(MatchKind Kind, DownloadItem Item, string Reason);

    public static Match? Find(IEnumerable<DownloadItem> items, DownloadItem candidate)
    {
        var url = items.FirstOrDefault(x => x.Id != candidate.Id && !string.IsNullOrWhiteSpace(candidate.Url) &&
            string.Equals(x.Url, candidate.Url, StringComparison.OrdinalIgnoreCase) && x.Status != nameof(DownloadStatus.Cancelled));
        if (url != null) return new(MatchKind.SameUrl, url, "The same URL already exists in Makan.");
        var path = items.FirstOrDefault(x => x.Id != candidate.Id && !string.IsNullOrWhiteSpace(candidate.FilePath) &&
            string.Equals(Path.GetFullPath(x.FilePath), Path.GetFullPath(candidate.FilePath), StringComparison.OrdinalIgnoreCase) && x.Status != nameof(DownloadStatus.Cancelled));
        if (path != null) return new(MatchKind.SamePath, path, "Another download uses the same destination file.");
        return null;
    }

    public static async Task<string?> Sha256Async(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Searches all download metadata, including queue/category/status, for the global search box.</summary>
public static class DownloadSearchService
{
    public static IEnumerable<DownloadItem> Search(IEnumerable<DownloadItem> items, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return items;
        return items.Where(x =>
            x.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.CategoryName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            x.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (x.QueueName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (x.LastError?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}

/// <summary>Lightweight rolling statistics for the dashboard and diagnostics.</summary>
public sealed class DownloadStatisticsService
{
    readonly object _gate = new();
    readonly Queue<(DateTime Time, long Bytes)> _samples = new();
    public long TotalBytes { get; private set; }
    public long CompletedBytes { get; private set; }
    public long FailedCount { get; private set; }
    public double AverageSpeedBytesPerSec { get; private set; }
    public long PeakSpeedBytesPerSec { get; private set; }

    public void Observe(IEnumerable<DownloadItem> items)
    {
        var now = DateTime.UtcNow;
        var speed = items.Where(x => x.Status == nameof(DownloadStatus.Downloading)).Sum(x => Math.Max(0, x.SpeedBytesPerSec));
        lock (_gate)
        {
            TotalBytes = items.Sum(x => Math.Max(0, x.DoneBytes));
            CompletedBytes = items.Where(x => x.Status == nameof(DownloadStatus.Complete)).Sum(x => Math.Max(0, x.DoneBytes));
            FailedCount = items.LongCount(x => x.Status == nameof(DownloadStatus.Failed));
            PeakSpeedBytesPerSec = Math.Max(PeakSpeedBytesPerSec, speed);
            _samples.Enqueue((now, speed));
            while (_samples.Count > 1 && now - _samples.Peek().Time > TimeSpan.FromSeconds(60)) _samples.Dequeue();
            AverageSpeedBytesPerSec = _samples.Count == 0 ? 0 : _samples.Average(x => (double)x.Bytes);
        }
    }
}

/// <summary>
/// Adaptive per-domain connection hints. A server starts with the connection count the user chose; when it throttles (HTTP 429 / 503)
/// the count is halved for that host, and it grows back one connection at a time after two calm minutes.
/// </summary>
public sealed class AdaptiveConnectionService
{
    sealed class Profile { public int Connections = 8; public int Max = 8; public DateTime LastChange = DateTime.UtcNow; public int Throttles; }
    readonly object _gate = new();
    readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public int Suggest(Uri uri, int configured)
    {
        configured = Math.Clamp(configured, 1, DownloadManager.MaxConnectionsPerDownload);
        lock (_gate)
        {
            if (!_profiles.TryGetValue(uri.Host, out var p)) { p = new Profile { Connections = configured, Max = configured }; _profiles[uri.Host] = p; }
            // the user changed the setting: follow it unless this server has been throttling
            if (p.Max != configured)
            {
                if (p.Throttles == 0 || configured < p.Max) p.Connections = configured;
                p.Max = configured;
            }
            p.Connections = Math.Clamp(p.Connections, 1, p.Max);
            return p.Connections;
        }
    }

    public void ReportSuccess(Uri uri)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(uri.Host, out var p)) return;
            p.Throttles = Math.Max(0, p.Throttles - 1);
            if (DateTime.UtcNow - p.LastChange > TimeSpan.FromMinutes(2) && p.Connections < p.Max) { p.Connections++; p.LastChange = DateTime.UtcNow; }
        }
    }

    public void ReportThrottle(Uri uri)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(uri.Host, out var p)) p = _profiles[uri.Host] = new Profile();
            p.Throttles++; p.Connections = Math.Max(1, p.Connections / 2); p.LastChange = DateTime.UtcNow;
        }
    }
}

/// <summary>Bandwidth schedule used by V11 to compute the current global limit.</summary>
public sealed class BandwidthSchedule
{
    public sealed record Rule(string Start, string End, long Kbps, bool Daily = true);
    public List<Rule> Rules { get; } = new();

    public long? CurrentLimit(DateTime now)
    {
        foreach (var r in Rules)
        {
            if (!TimeSpan.TryParse(r.Start, out var start) || !TimeSpan.TryParse(r.End, out var end)) continue;
            var t = now.TimeOfDay;
            var hit = start <= end ? t >= start && t < end : t >= start || t < end;
            if (hit) return Math.Max(0, r.Kbps) * 1024;
        }
        return null;
    }
}

/// <summary>Small self-contained update manifest client. The application can later point this at its signed release feed.</summary>
public sealed class UpdateService
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    public sealed record UpdateInfo(string Version, string DownloadUrl, string Sha256, string? NotesUrl);

    public async Task<UpdateInfo?> CheckAsync(Uri manifestUri, Version current, CancellationToken ct = default)
    {
        if (!manifestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var json = await _http.GetStringAsync(manifestUri, ct);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json);
            return info != null && Version.TryParse(info.Version, out var v) && v > current ? info : null;
        }
        catch { return null; }
    }

    public static async Task<bool> VerifyFileAsync(string path, string expectedSha256, CancellationToken ct = default)
    {
        var actual = await DuplicateDetector.Sha256Async(path, ct);
        return actual != null && string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public static class SmartDownloadService
{
    public static string SuggestCategory(string fileName) => CategoryService.For(fileName);

    public static string SuggestFolder(string fileName, string defaultFolder) => CategoryService.FolderFor(SuggestCategory(fileName), defaultFolder);

    public static string Describe(DownloadItem item, ProbeResult? probe = null)
    {
        var size = probe?.Length ?? item.TotalBytes;
        var sizeText = size.HasValue ? DownloadItem.FormatBytes(size.Value) : "unknown size";
        var resume = probe?.AcceptRanges == true ? "resumable" : "single-stream";
        return $"{Path.GetFileName(item.FilePath)} · {sizeText} · {resume}";
    }
}
