using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

// Services that need the desktop application (the database, the App class). Not part of the cross-platform test build.

public static class BackupService
{
    public static string ExportDatabase(DownloadDb db, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (!db.TryCreateBackup()) throw new IOException("Could not create a consistent SQLite backup.");
        File.Copy(db.BackupPath, destination, true);
        return destination;
    }
}

public static class V13Diagnostics
{
    public static string BuildReport(IEnumerable<DownloadItem> items, DownloadDb db)
    {
        var list = items.ToList();
        var s = StatisticsSnapshotBuilder.Build(list);
        var sb = new StringBuilder();
        sb.AppendLine($"Epsilon Download Manager {NativeBridge.Version} diagnostic report");
        sb.AppendLine("Generated: " + DateTime.Now.ToString("O"));
        sb.AppendLine("OS: " + Environment.OSVersion);
        sb.AppendLine("Runtime: " + Environment.Version);
        sb.AppendLine("Portable: " + PortableModeService.IsPortable);
        sb.AppendLine("Database: " + db.DatabasePath);
        sb.AppendLine("Database integrity: " + (db.IntegrityCheck() ? "OK" : "FAILED"));
        var net = new NetworkProfileService().GetSnapshot();
        sb.AppendLine($"Network: {(net.Connected ? "connected" : "offline")} | Interfaces: {net.Interfaces} | Wireless: {net.IsLikelyWireless}");
        sb.AppendLine($"Items: {list.Count} | Active: {s.ActiveCount} | Queued: {s.QueuedCount} | Failed: {s.Failed}");
        sb.AppendLine($"Downloaded: {DownloadItem.FormatBytes(s.TotalBytes)} | Peak speed: {DownloadItem.FormatBytes(s.PeakSpeed)}/s");
        return sb.ToString();
    }
}

/// <summary>Builds the inputs of the health checks from the running application.</summary>
public static class V15Context
{
    public static V15HealthInputs Health() => new(
        DataDirectory: PortableModeService.DataDirectory,
        BaseDirectory: AppContext.BaseDirectory,
        DefaultFolder: App.Settings?.DefaultFolder ?? "",
        EngineReady: App.Manager is not null,
        YtDlpPath: App.Manager?.YtDlp?.ExePath,
        FfmpegPath: YtDlpTools.FindExecutable("ffmpeg.exe", App.Settings?.FfmpegPath, YtDlpTools.DefaultToolsDirectory),
        Browsers: WindowsIntegration.Browsers().Select(b => (b.Name, b.Registered)).ToList(),
        DatabaseIntegrity: () => App.Db?.IntegrityCheck() ?? false);
}

/// <summary>The "Export diagnostic report" zip: the normal report and logs, plus a health snapshot and small JSON status files. Secrets are never included.</summary>
public static class V15DiagnosticReport
{
    public static string Export()
    {
        var service = new DiagnosticsService();
        var zip = service.ExportReport(Path.Combine(PortableModeService.DataDirectory, "downloads.db"));
        var options = new JsonSerializerOptions { WriteIndented = true };
        var checks = new V15HealthService(V15Context.Health).Run();
        var stats = V15StatisticsService.Build(App.Manager?.Items ?? Array.Empty<DownloadItem>());
        var dbPath = Path.Combine(PortableModeService.DataDirectory, "downloads.db");

        var entries = new Dictionary<string, string>
        {
            ["v15-health.txt"] = BuildHealthText(checks, stats),
            ["system-info.json"] = JsonSerializer.Serialize(new
            {
                version = NativeBridge.Version,
                created = DateTimeOffset.Now,
                os = Environment.OSVersion.ToString(),
                dotnet = Environment.Version.ToString(),
                process64 = Environment.Is64BitProcess,
                os64 = Environment.Is64BitOperatingSystem,
                cpu = Environment.ProcessorCount,
                portable = PortableModeService.IsPortable
            }, options),
            ["engine-status.json"] = JsonSerializer.Serialize(new
            {
                initialized = App.Manager is not null,
                downloads = App.Manager?.Items.Count ?? 0,
                maxActive = App.Manager?.MaxActive ?? 0,
                defaultConnections = App.Manager?.DefaultConnections ?? 0,
                globalLimitBytesPerSec = App.Manager?.GlobalLimitBytesPerSec ?? 0,
                adaptiveConnections = App.Manager?.AdaptiveConnectionsEnabled ?? false
            }, options),
            ["browser-status.json"] = JsonSerializer.Serialize(new
            {
                nativeHostPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "MakanNativeHost.exe")),
                browsers = WindowsIntegration.Browsers().Select(b => new { b.Name, b.Registered })
            }, options),
            ["database-status.json"] = JsonSerializer.Serialize(new
            {
                path = dbPath,
                exists = File.Exists(dbPath),
                sizeBytes = File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
                integrityOk = App.Db?.IntegrityCheck() ?? false
            }, options)
        };

        try
        {
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Update);
            foreach (var (name, text) in entries)
            {
                archive.GetEntry(name)?.Delete();
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        catch (Exception ex) { service.Error("Adding the V15 status files to the diagnostic report failed", ex); /* the base report stays usable */ }
        return zip;
    }

    static string BuildHealthText(IReadOnlyList<V15HealthService.Check> checks, V15StatisticsService.Snapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Epsilon Download Manager {NativeBridge.Version} health snapshot");
        sb.AppendLine($"Created: {DateTimeOffset.Now:O}");
        foreach (var c in checks) sb.AppendLine($"{(c.Healthy ? "PASS" : c.Optional ? "INFO" : "FAIL")} | {c.Name} | {c.Detail}");
        sb.AppendLine($"Downloads={s.Total}; Completed={s.Completed}; Active={s.Active}; Failed={s.Failed}; Paused={s.Paused}; Queued={s.Queued}; Bytes={s.DownloadedBytes}; AvgMbps={s.AverageMbps:0.00}; FastestBps={s.FastestBytesPerSec}; Retries={s.Retries}");
        return sb.ToString();
    }
}
