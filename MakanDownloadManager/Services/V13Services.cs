using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>
/// Persistent per-server learning. It remembers how fast each server was and how many connections it tolerates: a server that answers
/// 429 / 503 gets a lower limit that survives restarts, and the limit grows back by one connection after each clean download.
/// A server nobody complained about is never restricted (the limit stays at the maximum).
/// </summary>
public sealed class SmartDownloadController
{
    public const int NoLimit = 16;

    sealed class Profile
    {
        /// <summary>Most connections this server has tolerated so far (16 = no restriction learned).</summary>
        public int Connections { get; set; } = NoLimit;
        public double AverageMbps { get; set; }
        public int Samples { get; set; }
        public int Throttles { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    readonly ISettingsStore _store;
    readonly object _gate = new();
    readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    bool _loaded;
    const string Key = "v13_smart_profiles";

    public SmartDownloadController(ISettingsStore store) => _store = store;

    void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var json = _store.Get(Key);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, Profile>>(json);
                if (data != null) foreach (var pair in data) _profiles[pair.Key] = pair.Value;
            }
        }
        catch (JsonException) { _profiles.Clear(); }
    }

    /// <summary>How many connections to use for this server: the user's number, unless the server has taught us it cannot take that many.</summary>
    public int Suggest(Uri uri, int configured, long? sizeBytes = null)
    {
        configured = Math.Clamp(configured, 1, DownloadManager.MaxConnectionsPerDownload);
        lock (_gate)
        {
            EnsureLoaded();
            if (!_profiles.TryGetValue(uri.Host, out var p)) return configured;
            var target = Math.Min(configured, Math.Clamp(p.Connections, 1, NoLimit));
            if (p.Throttles >= 3) target = Math.Min(target, 2);      // a server that keeps throttling gets a careful start
            return Math.Clamp(target, 1, configured);
        }
    }

    public void ReportSuccess(DownloadItem item, TimeSpan elapsed)
    {
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return;
        var seconds = Math.Max(0.25, elapsed.TotalSeconds);
        var mbps = Math.Max(0, item.DoneBytes) * 8d / seconds / 1_000_000d;
        lock (_gate)
        {
            EnsureLoaded();
            if (!_profiles.TryGetValue(uri.Host, out var p)) p = _profiles[uri.Host] = new Profile();
            p.AverageMbps = p.Samples == 0 ? mbps : (p.AverageMbps * 0.75) + (mbps * 0.25);
            p.Samples = Math.Min(1000, p.Samples + 1);
            p.Throttles = Math.Max(0, p.Throttles - 1);
            if (p.Connections < NoLimit) p.Connections++;             // recover slowly after a throttled period
            p.LastSeenUtc = DateTime.UtcNow;
            SaveUnsafe();
        }
    }

    /// <param name="connectionsUsed">How many connections the failing download used (halved for the next start).</param>
    public void ReportThrottle(Uri uri, int connectionsUsed = 0)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_profiles.TryGetValue(uri.Host, out var p)) p = _profiles[uri.Host] = new Profile();
            var basis = connectionsUsed > 0 ? Math.Min(connectionsUsed, p.Connections) : p.Connections;
            p.Throttles = Math.Min(100, p.Throttles + 1);
            p.Connections = Math.Max(1, basis / 2);
            p.LastSeenUtc = DateTime.UtcNow;
            SaveUnsafe();
        }
    }

    public IReadOnlyDictionary<string, (int Connections, double AverageMbps, int Samples, int Throttles)> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _profiles.ToDictionary(x => x.Key, x => (x.Value.Connections, x.Value.AverageMbps, x.Value.Samples, x.Value.Throttles), StringComparer.OrdinalIgnoreCase);
        }
    }

    void SaveUnsafe()
    {
        try { _store.Set(Key, JsonSerializer.Serialize(_profiles)); } catch (Exception) { /* learning is a bonus: never fail a download over it */ }
    }
}

/// <summary>Small Windows network snapshot used by the scheduler/diagnostics. It never changes user bandwidth limits by itself.</summary>
public sealed class NetworkProfileService
{
    public sealed record Snapshot(bool Connected, string Interfaces, long FastestLinkBitsPerSecond, bool IsLikelyWireless);

    public Snapshot GetSnapshot()
    {
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();
            var fastest = active.Select(n => Math.Max(0, n.Speed)).DefaultIfEmpty(0).Max();
            var wireless = active.Any(n => n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211);
            var names = string.Join(", ", active.Select(n => n.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Take(8));
            return new Snapshot(active.Count > 0, names, fastest, wireless);
        }
        catch { return new Snapshot(false, "", 0, false); }
    }
}

/// <summary>Crash-safe metadata beside an incomplete download. The DB remains authoritative; this is a recovery aid.</summary>
public static class DownloadStateManifest
{
    sealed record State(string Url, string FilePath, long? TotalBytes, long DoneBytes, string? ETag, string? LastModified, int Connections, DateTime UpdatedUtc);

    public static string PathFor(DownloadItem item) => item.FilePath + ".makan-state.json";

    static bool _loggedFailure;   // this runs on every progress update of every active download, so a repeated failure (e.g. disk full) is logged once per session, not flooded

    public static void Write(DownloadItem item)
    {
        try
        {
            var state = new State(item.Url, item.FilePath, item.TotalBytes, item.DoneBytes, item.ETag, item.LastModified, item.Connections, DateTime.UtcNow);
            var path = PathFor(item);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, path, true);
            _loggedFailure = false;
        }
        catch (Exception ex)
        {
            if (!_loggedFailure) { _loggedFailure = true; new DiagnosticsService().Error("Could not save resume state next to a download (further failures this session are not logged individually to avoid flooding the log)", ex); }
        }
    }

    public static void Delete(DownloadItem item)
    {
        try { File.Delete(PathFor(item)); } catch { }
        try { File.Delete(PathFor(item) + ".tmp"); } catch { }
    }
}

/// <summary>Verifies release files before an installer is allowed to use them. Signature verification can be enabled by supplying a trusted RSA public key.</summary>
public static class SecureUpdateVerifier
{
    public static async Task<bool> VerifySha256Async(string path, string expected, CancellationToken ct = default)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(expected)) return false;
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(actual).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool VerifySignature(byte[] data, byte[] signature, string base64RsaPublicKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(base64RsaPublicKey), out _);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { return false; }
    }
}
