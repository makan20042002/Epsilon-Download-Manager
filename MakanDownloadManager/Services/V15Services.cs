using System.Net.NetworkInformation;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

// V15 product services: local-only statistics, bandwidth profiles, health checks and recovery discovery.
// They are deterministic, work without any cloud service, and know nothing about WPF: everything they need is passed in,
// so the same code runs (and is tested) on any operating system.

/// <summary>Per-server learning shown in the Intelligent Center, with a plain-words recommendation.</summary>
public sealed class V15DownloadIntelligence
{
    public sealed record ServerSnapshot(string Host, int Connections, double AverageMbps, int Samples, int Throttles);

    readonly Func<SmartDownloadController?> _controller;
    public V15DownloadIntelligence(Func<SmartDownloadController?> controller) => _controller = controller;

    public IReadOnlyList<ServerSnapshot> Servers()
    {
        var controller = _controller();
        if (controller is null) return Array.Empty<ServerSnapshot>();
        return controller.Snapshot()
            .OrderByDescending(x => x.Value.AverageMbps)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ServerSnapshot(x.Key, x.Value.Connections, x.Value.AverageMbps, x.Value.Samples, x.Value.Throttles))
            .ToList();
    }

    public string Recommend(DownloadItem item)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return "Invalid URL";
        var profile = Servers().FirstOrDefault(x => string.Equals(x.Host, uri.Host, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return "No history yet — Makan starts with your normal connection count and learns from this server.";
        if (profile.Throttles >= 3) return $"This server has throttled {profile.Throttles} times; keep connections near {Math.Max(1, profile.Connections)}.";
        if (profile.Samples >= 4 && profile.AverageMbps >= 80) return $"Strong history ({profile.AverageMbps:0.0} Mbps); {profile.Connections} connections is a good starting point.";
        return $"Learned profile: {profile.Connections} connections, {profile.AverageMbps:0.0} Mbps average over {profile.Samples} samples.";
    }
}

/// <summary>One-click global speed limits. "Apply" hands the limit (KB/s, 0 = unlimited) to the application.</summary>
public sealed class V15BandwidthProfiles
{
    public sealed record Profile(string Name, long LimitBytesPerSec, string Description);
    const string Key = "v15_bandwidth_profiles";

    readonly ISettingsStore _store;
    readonly Action<long>? _applyKbps;

    public V15BandwidthProfiles(ISettingsStore store, Action<long>? applyKbps = null)
    {
        _store = store;
        _applyKbps = applyKbps;
    }

    public IReadOnlyList<Profile> Load()
    {
        try
        {
            var json = _store.Get(Key);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var value = JsonSerializer.Deserialize<List<Profile>>(json);
                if (value is { Count: > 0 } && value.All(p => !string.IsNullOrWhiteSpace(p.Name))) return value;
            }
        }
        catch (JsonException) { /* damaged setting: use the defaults */ }
        return Defaults;
    }

    public void Save(IEnumerable<Profile> profiles) => _store.Set(Key, JsonSerializer.Serialize(profiles));

    /// <summary>Applies the named profile. False when there is no such profile.</summary>
    public bool Apply(string name)
    {
        var p = Load().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (p is null) return false;
        _applyKbps?.Invoke(p.LimitBytesPerSec <= 0 ? 0 : Math.Max(1, p.LimitBytesPerSec / 1024));
        return true;
    }

    /// <summary>The profile that matches the current global limit (KB/s), if any: unlimited matches the first unlimited profile.</summary>
    public string? ActiveName(long currentKbps) =>
        Load().FirstOrDefault(p => (p.LimitBytesPerSec <= 0 ? 0 : Math.Max(1, p.LimitBytesPerSec / 1024)) == Math.Max(0, currentKbps))?.Name;

    public static IReadOnlyList<Profile> Defaults { get; } = new[]
    {
        new Profile("Unlimited", 0, "No bandwidth limit"),
        new Profile("Gaming", 2L * 1024 * 1024, "Keep downloads near 2 MB/s so online games stay smooth"),
        new Profile("Work", 5L * 1024 * 1024, "Keep downloads near 5 MB/s so calls and browsing stay fast"),
        new Profile("Night", 0, "Full speed while you sleep (combine with a queue schedule)")
    };
}

/// <summary>Numbers for the statistics cards, computed from the current list of downloads.</summary>
public sealed class V15StatisticsService
{
    public sealed record Snapshot(int Total, int Completed, int Active, int Failed, int Paused, int Queued,
        long DownloadedBytes, long CompletedBytes, double AverageMbps, long FastestBytesPerSec, int Retries);

    readonly Func<IReadOnlyList<DownloadItem>> _items;
    public V15StatisticsService(Func<IReadOnlyList<DownloadItem>> items) => _items = items;

    public Snapshot GetSnapshot() => Build(_items());

    public static Snapshot Build(IReadOnlyList<DownloadItem> items)
    {
        int Count(DownloadStatus s) => items.Count(x => x.Status == s.ToString());
        var rates = items.Where(x => x.Status == nameof(DownloadStatus.Downloading)).Select(x => Math.Max(0L, x.SpeedBytesPerSec)).Where(x => x > 0).ToList();
        return new Snapshot(
            Total: items.Count,
            Completed: Count(DownloadStatus.Complete),
            Active: Count(DownloadStatus.Downloading),
            Failed: Count(DownloadStatus.Failed),
            Paused: Count(DownloadStatus.Paused),
            Queued: Count(DownloadStatus.Queued),
            DownloadedBytes: items.Sum(x => Math.Max(0L, x.DoneBytes)),
            CompletedBytes: items.Where(x => x.Status == nameof(DownloadStatus.Complete)).Sum(x => Math.Max(0L, x.DoneBytes)),
            AverageMbps: rates.Count == 0 ? 0 : rates.Average() * 8d / 1_000_000d,
            FastestBytesPerSec: rates.Count == 0 ? 0 : rates.Max(),
            Retries: items.Sum(x => Math.Max(0, x.RetryCount)));
    }
}

/// <summary>What the health checks look at (filled by the application, so the checks themselves stay testable).</summary>
public sealed record V15HealthInputs(
    string DataDirectory,
    string BaseDirectory,
    string DefaultFolder,
    bool EngineReady,
    string? YtDlpPath,
    string? FfmpegPath,
    IReadOnlyList<(string Browser, bool Registered)> Browsers,
    Func<bool>? DatabaseIntegrity = null);

public sealed class V15HealthService
{
    /// <param name="Optional">A missing optional tool is not a problem: it is shown as a hint, not as a warning.</param>
    public sealed record Check(string Name, bool Healthy, string Detail, bool Optional = false);

    readonly Func<V15HealthInputs> _inputs;
    public V15HealthService(Func<V15HealthInputs> inputs) => _inputs = inputs;

    public IReadOnlyList<Check> Run()
    {
        var i = _inputs();
        var checks = new List<Check>();

        try
        {
            var db = Path.Combine(i.DataDirectory, "downloads.db");
            if (!File.Exists(db)) checks.Add(new("Database", false, "The database file is missing."));
            else
            {
                var integrity = i.DatabaseIntegrity?.Invoke() ?? true;
                checks.Add(new("Database", integrity, integrity ? "SQLite database is present and passes its integrity check." : "SQLite reports a problem: use Backup / Restore in the Security tab."));
            }
        }
        catch (Exception ex) { checks.Add(new("Database", false, ex.Message)); }

        checks.Add(new("Download engine", i.EngineReady, i.EngineReady ? "The engine is running." : "The engine is not initialized."));

        var network = NetworkInterface.GetIsNetworkAvailable();
        checks.Add(new("Network", network, network ? "Windows reports an available network." : "Windows reports no available network."));

        checks.Add(CheckFreeSpace(i.DefaultFolder));
        checks.Add(CheckWritable(i.DefaultFolder));

        var hostExe = Path.Combine(i.BaseDirectory, "MakanNativeHost.exe");
        var hostPresent = File.Exists(hostExe);
        var registered = i.Browsers.Where(b => b.Registered).Select(b => b.Browser).ToList();
        checks.Add(new("Browser integration", hostPresent && registered.Count > 0,
            !hostPresent ? "MakanNativeHost.exe was not found next to the application." :
            registered.Count == 0 ? "Not connected to any browser yet: run install-browser-integration.ps1 (or Options > General > Repair)." :
            "Connected: " + string.Join(", ", registered) + (i.Browsers.Count > registered.Count ? ". Not connected: " + string.Join(", ", i.Browsers.Where(b => !b.Registered).Select(b => b.Browser)) + "." : ".")));

        checks.Add(new("yt-dlp (YouTube and other sites)", !string.IsNullOrWhiteSpace(i.YtDlpPath),
            string.IsNullOrWhiteSpace(i.YtDlpPath) ? "Optional: Options > YouTube & other sites > Download / update tools." : "Found: " + i.YtDlpPath, Optional: true));
        checks.Add(new("FFmpeg (merging video and sound)", !string.IsNullOrWhiteSpace(i.FfmpegPath),
            string.IsNullOrWhiteSpace(i.FfmpegPath) ? "Optional: needed for MP4 output of some streams and for HD YouTube." : "Found: " + i.FfmpegPath, Optional: true));
        return checks;
    }

    static Check CheckFreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? AppContext.BaseDirectory : folder));
            if (string.IsNullOrWhiteSpace(root)) return new("Disk space", false, "The drive of the download folder could not be found.");
            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / 1_073_741_824d;
            return new("Disk space", drive.IsReady && freeGb >= 1, $"{freeGb:0.0} GB free on {root}" + (freeGb < 1 ? " — downloads may fail." : "."));
        }
        catch (Exception ex) { return new("Disk space", false, ex.Message); }
    }

    static Check CheckWritable(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return new("Download folder", false, "No default download folder is set.");
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".makan-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new("Download folder", true, "Writable: " + path);
        }
        catch (Exception ex) { return new("Download folder", false, ex.Message); }
    }
}

/// <summary>Finds downloads that were interrupted (crash, power cut) through the state files Makan keeps beside partial downloads.</summary>
public static class V15RecoveryService
{
    public sealed record Interrupted(string Path, long DoneBytes, long? TotalBytes, DateTime UpdatedUtc);

    public static IReadOnlyList<Interrupted> Scan(IEnumerable<DownloadItem> items)
    {
        var result = new List<Interrupted>();
        foreach (var file in items
                     .Where(x => x.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Paused) or nameof(DownloadStatus.Failed) or nameof(DownloadStatus.Queued))
                     .Select(DownloadStateManifest.PathFor)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                result.Add(new Interrupted(
                    root.TryGetProperty("FilePath", out var fp) ? fp.GetString() ?? file : file,
                    root.TryGetProperty("DoneBytes", out var done) ? done.GetInt64() : 0,
                    root.TryGetProperty("TotalBytes", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt64() : null,
                    root.TryGetProperty("UpdatedUtc", out var updated) && updated.TryGetDateTime(out var when) ? when.ToUniversalTime() : File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) { /* an unreadable state file is simply not listed */ }
        }
        return result.OrderByDescending(x => x.UpdatedUtc).ToList();
    }
}
