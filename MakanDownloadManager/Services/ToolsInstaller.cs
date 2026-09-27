using System.IO.Compression;
using System.Security.Cryptography;

namespace MakanDownloadManager.Services;

/// <summary>
/// Downloads the helper tools for YouTube and other protected video sites into Makan's own folder (no admin rights needed):
/// yt-dlp (the extractor), Deno (runs YouTube's player code) and FFmpeg (merges video and audio). Files are checked against the
/// SHA-256 sums published next to them when those can be read; a wrong sum is refused.
/// </summary>
public sealed class ToolsInstaller
{
    public string ToolsDirectory { get; }
    public string YtDlpUrl { get; set; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public string YtDlpSumsUrl { get; set; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public string DenoUrl { get; set; } = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    public string DenoSumsUrl { get; set; } = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum";
    public string FfmpegUrl { get; set; } = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";
    public string FfmpegSumsUrl { get; set; } = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/checksums.sha256";

    readonly HttpClient _http;

    public ToolsInstaller(string toolsDirectory, HttpClient? http = null)
    {
        ToolsDirectory = toolsDirectory;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (http == null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("MakanDownloadManager");
    }

    public string YtDlpPath => Path.Combine(ToolsDirectory, "yt-dlp.exe");
    public string DenoPath => Path.Combine(ToolsDirectory, "deno.exe");
    public string FfmpegPath => Path.Combine(ToolsDirectory, "ffmpeg.exe");

    public async Task InstallYtDlpAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var temp = YtDlpPath + ".download";
        try
        {
            await DownloadAsync(YtDlpUrl, temp, "yt-dlp", progress, ct);
            await VerifyAsync(temp, YtDlpSumsUrl, "yt-dlp.exe", ct);
            File.Move(temp, YtDlpPath, true);
        }
        finally { TryDelete(temp); }
        progress?.Report("yt-dlp is ready.");
    }

    public async Task InstallDenoAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var zip = Path.Combine(ToolsDirectory, "deno.zip.download");
        try
        {
            await DownloadAsync(DenoUrl, zip, "Deno", progress, ct);
            await VerifyAsync(zip, DenoSumsUrl, "deno-x86_64-pc-windows-msvc.zip", ct);
            ExtractOne(zip, "deno.exe", DenoPath);
        }
        finally { TryDelete(zip); }
        progress?.Report("Deno is ready.");
    }

    public async Task InstallFfmpegAsync(IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDirectory);
        var zip = Path.Combine(ToolsDirectory, "ffmpeg.zip.download");
        try
        {
            await DownloadAsync(FfmpegUrl, zip, "FFmpeg", progress, ct);
            await VerifyAsync(zip, FfmpegSumsUrl, Path.GetFileName(new Uri(FfmpegUrl).AbsolutePath), ct);
            ExtractOne(zip, "ffmpeg.exe", FfmpegPath);
            try { ExtractOne(zip, "ffprobe.exe", Path.Combine(ToolsDirectory, "ffprobe.exe")); } catch (FileNotFoundException) { /* optional */ }
        }
        finally { TryDelete(zip); }
        progress?.Report("FFmpeg is ready.");
    }

    async Task DownloadAsync(string url, string destination, string label, IProgress<string>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920]; long done = 0; var last = Environment.TickCount64;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (Environment.TickCount64 - last > 300)
            {
                last = Environment.TickCount64;
                progress?.Report(total is > 0 ? $"Downloading {label}… {done * 100 / total.Value}%" : $"Downloading {label}… {done / 1024 / 1024} MB");
            }
        }
    }

    /// <summary>Checks the file against the published sums. A missing sums file is tolerated (offline mirror, renamed asset); a wrong sum is not.</summary>
    async Task VerifyAsync(string file, string sumsUrl, string assetName, CancellationToken ct)
    {
        string sums;
        try { sums = await _http.GetStringAsync(sumsUrl, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return; }
        var expected = sums.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => l.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 1 && p[0].Length == 64)
            .FirstOrDefault(p => p.Length == 1 || string.Equals(Path.GetFileName(p[^1]), assetName, StringComparison.OrdinalIgnoreCase))?[0];
        if (expected == null) return;
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded {assetName} does not match its published SHA-256 sum. It was not installed.");
    }

    static void ExtractOne(string zipPath, string fileName, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), fileName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException(fileName + " is not inside the downloaded archive.");
        var temp = destination + ".tmp";
        entry.ExtractToFile(temp, true);
        File.Move(temp, destination, true);
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
