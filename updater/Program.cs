using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MakanUpdater;

// Secure staged updater for Epsilon Download Manager.
// Manifest: { "version":"15.2.0", "packageUrl":"https://.../MakanDownloadManager.zip", "sha256":"..." }
// The package is downloaded (HTTPS only, redirects followed by hand and checked) and verified BEFORE the installed application is
// stopped; files are then swapped with rollback (see FileSwap). Exit code 0 = updated, 1 = failed (the installation is unchanged).

const string ProductVersion = "1.4.2";

static string Arg(string name, string fallback = "")
{
    var a = Environment.GetCommandLineArgs();
    for (var i = 0; i < a.Length - 1; i++)
        if (string.Equals(a[i], name, StringComparison.OrdinalIgnoreCase)) return a[i + 1];
    return fallback;
}

static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps;
static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

/// <summary>GET that follows redirects itself, and refuses any hop that is not HTTPS.</summary>
static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri)
{
    for (var hop = 0; hop < 6; hop++)
    {
        var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
        {
            response.Dispose();
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            if (!IsHttps(uri)) throw new InvalidOperationException("Unsafe update redirect detected (not HTTPS).");
            continue;
        }
        response.EnsureSuccessStatusCode();
        return response;
    }
    throw new InvalidOperationException("Too many redirects while downloading the update.");
}

var tempRoot = "";
try
{
    var manifestUrl = Arg("--manifest");
    var installDir = Arg("--install-dir");
    var processName = Arg("--process", "MakanDownloadManager");

    if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri) || !IsHttps(manifestUri))
        throw new ArgumentException("Update metadata must use HTTPS (--manifest https://...).");
    if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        throw new DirectoryNotFoundException("Install folder not found (--install-dir): " + installDir);
    if (!string.Equals(processName, "MakanDownloadManager", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Only the MakanDownloadManager process may be updated.");
    installDir = Path.GetFullPath(installDir);

    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("MakanDownloadManager-Updater/" + ProductVersion);

    using var manifestResponse = await GetAsync(http, manifestUri);
    var manifest = JsonSerializer.Deserialize<UpdateManifest>(await manifestResponse.Content.ReadAsStringAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("Invalid update manifest.");

    if (!Version.TryParse(manifest.Version, out var newVersion)) throw new InvalidDataException("Invalid update version.");
    if (!Version.TryParse(ProductVersion, out var currentVersion)) throw new InvalidDataException("Invalid installed version.");
    if (newVersion <= currentVersion) { Console.WriteLine($"Already up to date ({currentVersion}); the manifest offers {newVersion}."); return 0; }
    if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri) || !IsHttps(packageUri)) throw new InvalidDataException("Update package must use HTTPS.");
    if (!IsSha256(manifest.Sha256)) throw new InvalidDataException("Invalid SHA-256 value in the manifest.");

    tempRoot = Path.Combine(Path.GetTempPath(), "MakanDownloadManager", "update-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempRoot);
    var archive = Path.Combine(tempRoot, "package.zip");
    var stage = Path.Combine(tempRoot, "stage");

    // Download and verify first. A bad update must never leave the installed app stopped.
    using (var packageResponse = await GetAsync(http, packageUri))
    {
        await using var source = await packageResponse.Content.ReadAsStreamAsync();
        await using var target = File.Create(archive);
        await source.CopyToAsync(target);
    }
    await using (var f = File.OpenRead(archive))
    {
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(f));
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Update SHA-256 verification failed; nothing was changed.");
    }

    Directory.CreateDirectory(stage);
    ZipFile.ExtractToDirectory(archive, stage, overwriteFiles: true);
    if (!File.Exists(Path.Combine(stage, "MakanDownloadManager.exe"))) throw new InvalidDataException("The update package does not contain MakanDownloadManager.exe.");

    // Only now stop the running app (it keeps unfinished downloads and resumes them when it starts again).
    foreach (var p in Process.GetProcessesByName(processName)) { try { p.CloseMainWindow(); } catch (InvalidOperationException) { } }
    await Task.Delay(1500);
    foreach (var p in Process.GetProcessesByName(processName)) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    await Task.Delay(500);

    FileSwap.Apply(stage, installDir);          // throws after undoing everything when a file cannot be replaced
    Process.Start(new ProcessStartInfo(Path.Combine(installDir, "MakanDownloadManager.exe")) { UseShellExecute = true });
    Console.WriteLine($"Updated to {newVersion}.");
    _ = Task.Run(() => FileSwap.CleanOld(installDir));
    await Task.Delay(1000);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Update failed: " + ex.Message);
    try
    {
        var installDir = Arg("--install-dir");
        var exe = Path.Combine(installDir, "MakanDownloadManager.exe");
        if (File.Exists(exe) && Process.GetProcessesByName("MakanDownloadManager").Length == 0) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
    catch (Exception) { /* best effort: the installation itself is unchanged */ }
    return 1;
}
finally
{
    try { if (tempRoot.Length > 0 && Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

sealed record UpdateManifest(string Version, string PackageUrl, string Sha256);
