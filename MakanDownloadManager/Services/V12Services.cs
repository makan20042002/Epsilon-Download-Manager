using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

public enum DownloadPerformanceMode { MaximumSpeed, Balanced, ServerFriendly, Custom }

public sealed record SmartDownloadPlan(
    string Category,
    string Folder,
    int Connections,
    DownloadPerformanceMode Mode,
    bool Resumable,
    string Summary,
    string? SuggestedFileName = null);

/// <summary>V13 smart planner. Keeps the normal downloader simple while making sensible choices before a job starts.</summary>
public sealed class SmartDownloadAnalyzer
{
    public SmartDownloadPlan Plan(string url, string? fileName, string defaultFolder, int configuredConnections, DownloadPerformanceMode mode = DownloadPerformanceMode.Balanced)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? GuessName(url) : DownloadFileNamer.Sanitize(fileName, "download.bin");
        var category = CategoryService.For(name);
        var folder = CategoryService.FolderFor(category, defaultFolder);
        var connections = mode switch
        {
            DownloadPerformanceMode.MaximumSpeed => Math.Clamp(Math.Max(configuredConnections, 8), 1, DownloadManager.MaxConnectionsPerDownload),
            DownloadPerformanceMode.ServerFriendly => Math.Min(configuredConnections, 2),
            DownloadPerformanceMode.Custom => Math.Clamp(configuredConnections, 1, DownloadManager.MaxConnectionsPerDownload),
            _ => Math.Clamp(configuredConnections, 1, 8)
        };
        return new SmartDownloadPlan(category, folder, connections, mode, true,
            $"{category} · {mode} · up to {connections} connections", name);
    }

    static string GuessName(string url)
    {
        try { var n = Path.GetFileName(new Uri(url).LocalPath); return string.IsNullOrWhiteSpace(n) ? "download.bin" : Uri.UnescapeDataString(n); }
        catch { return "download.bin"; }
    }
}

public sealed class DownloadRule
{
    public string Name { get; set; } = "Rule";
    public string Pattern { get; set; } = "*";
    public string? Category { get; set; }
    public string? Folder { get; set; }
    public int? Connections { get; set; }
    /// <summary>Null = leave the download's own priority alone.</summary>
    public int? Priority { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Simple wildcard rules for smart folders, priorities and connection profiles.</summary>
public sealed class DownloadRuleEngine
{
    readonly List<DownloadRule> _rules;
    public DownloadRuleEngine(IEnumerable<DownloadRule>? rules = null) => _rules = rules?.ToList() ?? new();
    public IReadOnlyList<DownloadRule> Rules => _rules;
    public DownloadRuleEngine Add(DownloadRule rule) { _rules.Add(rule); return this; }

    public void Apply(DownloadItem item)
    {
        foreach (var rule in _rules.Where(x => x.Enabled))
        {
            if (!Wildcard(item.FileName, rule.Pattern) && !Wildcard(item.Url, rule.Pattern)) continue;
            if (!string.IsNullOrWhiteSpace(rule.Category)) item.Category = rule.Category!;
            if (!string.IsNullOrWhiteSpace(rule.Folder)) item.FilePath = Path.Combine(rule.Folder!, item.FileName);
            if (rule.Connections.HasValue) item.Connections = Math.Clamp(rule.Connections.Value, 1, DownloadManager.MaxConnectionsPerDownload);
            if (rule.Priority.HasValue) item.Priority = Math.Clamp(rule.Priority.Value, 0, 10);
            break;
        }
    }

    static bool Wildcard(string value, string pattern)
    {
        var p = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value ?? "", p, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

public sealed class DownloadBasket
{
    readonly object _gate = new();
    readonly List<string> _urls = new();
    public IReadOnlyList<string> Items { get { lock (_gate) return _urls.ToList(); } }
    public void Add(string url) { if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) lock (_gate) if (!_urls.Contains(url, StringComparer.OrdinalIgnoreCase)) _urls.Add(url); }
    public void Remove(string url) { lock (_gate) _urls.RemoveAll(x => string.Equals(x, url, StringComparison.OrdinalIgnoreCase)); }
    public void Clear() { lock (_gate) _urls.Clear(); }
    public void AddRange(IEnumerable<string> urls) { foreach (var u in urls) Add(u); }
    public string ExportJson() => JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true });
    public void ImportJson(string json) { var urls = JsonSerializer.Deserialize<List<string>>(json); if (urls != null) AddRange(urls); }
}

public sealed class StatisticsSnapshot
{
    public long TotalBytes { get; init; }
    public long CompletedBytes { get; init; }
    public long Failed { get; init; }
    public long CompletedCount { get; init; }
    public long ActiveCount { get; init; }
    public long QueuedCount { get; init; }
    public long PeakSpeed { get; init; }
    public double AverageSpeed { get; init; }
    public IReadOnlyDictionary<string, long> ByCategory { get; init; } = new Dictionary<string, long>();
}

public static class StatisticsSnapshotBuilder
{
    public static StatisticsSnapshot Build(IEnumerable<DownloadItem> items)
    {
        var list = items.ToList();
        var active = list.Where(x => x.Status == nameof(DownloadStatus.Downloading)).ToList();
        return new StatisticsSnapshot
        {
            TotalBytes = list.Sum(x => Math.Max(0, x.DoneBytes)),
            CompletedBytes = list.Where(x => x.Status == nameof(DownloadStatus.Complete)).Sum(x => Math.Max(0, x.DoneBytes)),
            Failed = list.LongCount(x => x.Status == nameof(DownloadStatus.Failed)),
            CompletedCount = list.LongCount(x => x.Status == nameof(DownloadStatus.Complete)),
            ActiveCount = active.Count,
            QueuedCount = list.LongCount(x => x.Status == nameof(DownloadStatus.Queued)),
            PeakSpeed = active.Sum(x => Math.Max(0, x.SpeedBytesPerSec)),
            AverageSpeed = active.Count == 0 ? 0 : active.Average(x => (double)Math.Max(0, x.SpeedBytesPerSec)),
            ByCategory = list.GroupBy(x => x.CategoryName).ToDictionary(g => g.Key, g => (long)g.Count())
        };
    }
}

public static class DownloadSecurityService
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default) => await DuplicateDetector.Sha256Async(path, ct) ?? "";
    public static bool VerifySha256(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        var normalized = expected.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (normalized.Length != 64) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static bool IsHttps(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
    public static string Describe(DownloadItem item) => $"{(IsHttps(item.Url) ? "HTTPS" : "HTTP")}; SHA-256: {(string.IsNullOrWhiteSpace(item.ExpectedSha256) ? "not configured" : "configured")}; status: {item.Status}";
}

public interface IMakanProvider { string Id { get; } string Name { get; } bool CanHandle(Uri uri); }
public interface IMakanDownloadProvider : IMakanProvider { Task<DownloadItem?> CreateAsync(Uri uri, CancellationToken ct = default); }
