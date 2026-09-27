using System.Text;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

public sealed class DiagnosticsService
{
    public string DirectoryPath { get; }

    public DiagnosticsService()
    {
        DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakanDownloadManager", "logs");
        Directory.CreateDirectory(DirectoryPath);
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message} | {ex}");

    public string CreateReport(string? databasePath = null)
    {
        var report = Path.Combine(DirectoryPath, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine("Epsilon Download Manager diagnostics");
        sb.AppendLine($"Created: {DateTime.Now:O}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"CPU count: {Environment.ProcessorCount}");
        sb.AppendLine($"Database: {databasePath ?? "not supplied"}");
        sb.AppendLine($"Log directory: {DirectoryPath}");
        File.WriteAllText(report, sb.ToString());
        return report;
    }

    public string ExportReport(string? databasePath = null)
    {
        var txt = CreateReport(databasePath);
        var zip = Path.Combine(DirectoryPath, Path.GetFileNameWithoutExtension(txt) + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(txt, Path.GetFileName(txt));
        foreach (var log in Directory.EnumerateFiles(DirectoryPath, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(7))
            archive.CreateEntryFromFile(log, Path.GetFileName(log));
        return zip;
    }

    void Write(string level, string message)
    {
        try
        {
            var file = Path.Combine(DirectoryPath, $"makan-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(file, $"{DateTime.Now:O} [{level}] {Redact(message)}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Hides secrets in text that is written to logs and diagnostic reports.</summary>
    internal static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        // whole cookie headers (several "a=1; b=2" pairs), up to the end of the line
        var value = Regex.Replace(message, @"(?i)\b(set-cookie|cookie)\s*[:=]\s*[^\r\n]+", "$1=[REDACTED]");
        // Authorization: Bearer xyz / Basic xyz
        value = Regex.Replace(value, @"(?i)\b(proxy-authorization|authorization)\s*[:=]\s*(?:(?:bearer|basic|digest|negotiate|ntlm)\s+)?[^\s;|]+", "$1=[REDACTED]");
        // token / key / password style values
        value = Regex.Replace(value, @"(?i)\b(access_token|refresh_token|id_token|token|api[_-]?key|apikey|password|passwd|secret)\s*[:=]\s*[^\s;&|]+", "$1=[REDACTED]");
        // signed URLs
        return Regex.Replace(value, @"(?i)\b(x-amz-signature|x-amz-credential|x-amz-security-token|signature|sig)=[^&\s]+", "$1=[REDACTED]");
    }
}
