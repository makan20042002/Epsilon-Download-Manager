using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MakanDownloadManager.Services;

public sealed record SpeedTestProgress(string Stage, double Percent);
public sealed record SpeedTestResult(double DownloadMbps, double UploadMbps, double PingMs, double JitterMs, long BytesUsed);
public sealed record LinkSpeedResult(double Mbps, long BytesRead, string Host);
public sealed record IranRouteResult(string Classification, string Address, string Host);

/// <summary>Small, bounded network-quality test plus an optional comparison against one download host.</summary>
public sealed class SpeedTestService
{
    const string DownUrl = "https://speed.cloudflare.com/__down";
    const string UpUrl = "https://speed.cloudflare.com/__up";
    const string ApnicUrl = "https://ftp.apnic.net/stats/apnic/delegated-apnic-latest";
    const int LinkProbeBytes = 8 * 1024 * 1024;
    const int UploadProbeBytes = 32 * 1024;
    const int MaxUploadStreamBytes = 2 * 1024 * 1024;
    static readonly HttpClient Shared = CreateClient();
    static readonly SemaphoreSlim RegistryLock = new(1, 1);
    static string? _apnicRegistry;
    static DateTime _apnicRegistryExpiresUtc;
    readonly HttpClient _http;

    public SpeedTestService(HttpClient? http = null) => _http = http ?? Shared;

    static HttpClient CreateClient()
    {
        // Low upload speeds can legitimately need much longer than the old 35-second limit.
        // Each transfer is still bounded by its payload and the caller's cancellation token.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EpsilonDownloadManager/1.7.1 SpeedTest");
        return client;
    }

    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new("Measuring latency…", 5));
        var latency = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                using var response = await _http.GetAsync($"{DownUrl}?bytes=0&r={Guid.NewGuid():N}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await response.Content.CopyToAsync(Stream.Null, cancellationToken);
                watch.Stop();
                if (i > 0) latency.Add(watch.Elapsed.TotalMilliseconds); // discard the connection warm-up
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One dropped latency sample must not discard the download/upload measurements.
            }
            progress?.Report(new("Measuring latency…", 5 + (i + 1) * 3));
        }

        progress?.Report(new("Measuring download speed…", 28));
        // Four parallel streams are close to how Epsilon itself uses a connection, while the 32 MB cap keeps the test modest.
        const int streamBytes = 8 * 1024 * 1024;
        var downloadWatch = Stopwatch.StartNew();
        var downloadTasks = Enumerable.Range(0, 4).Select(i => TryReadBytesAsync(
            $"{DownUrl}?bytes={streamBytes}&r={Guid.NewGuid():N}", streamBytes, 35 + i * 8, progress, cancellationToken)).ToArray();
        var downloaded = (await Task.WhenAll(downloadTasks)).Sum();
        downloadWatch.Stop();
        if (downloaded == 0) throw new IOException("The speed-test server did not return download data. Please try again.");
        var downloadMbps = MegabitsPerSecond(downloaded, downloadWatch.Elapsed);

        progress?.Report(new("Measuring upload speed…", 72));
        // First send only 32 KB. The result chooses a payload that takes roughly six seconds,
        // instead of forcing every connection to upload 4 MB. This makes sub-Mbps links finish
        // reliably while fast links still get a large enough sample for an honest measurement.
        var probe = await UploadWithRetryAsync(UploadProbeBytes, cancellationToken);
        var probeMbps = MegabitsPerSecond(probe.Bytes, probe.Elapsed);
        var uploadBytes = UploadPayloadBytesFor(probeMbps);
        var streamCount = UploadStreamCountFor(probeMbps);
        double uploadMbps;
        long uploaded;
        if (probe.Elapsed >= TimeSpan.FromSeconds(8) || probeMbps < 0.05)
        {
            // On extremely slow connections the probe itself is already a useful long sample.
            uploadMbps = probeMbps;
            uploaded = probe.Bytes;
        }
        else
        {
            var uploadTasks = Enumerable.Range(0, streamCount)
                .Select(_ => TryUploadWithRetryAsync(uploadBytes, cancellationToken)).ToArray();
            var measurements = (await Task.WhenAll(uploadTasks)).Where(x => x != null).Select(x => x!.Value).ToArray();
            if (measurements.Length == 0)
            {
                // A valid probe is better than failing the whole test because the larger second
                // request hit a temporary CDN/network problem.
                uploadMbps = probeMbps;
                uploaded = probe.Bytes;
            }
            else
            {
                uploaded = measurements.Sum(x => x.Bytes);
                uploadMbps = MegabitsPerSecond(uploaded, measurements.Max(x => x.Elapsed));
            }
        }

        var ping = latency.Count == 0 ? 0 : latency.Average();
        var jitter = latency.Count < 2 ? 0 : latency.Zip(latency.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        progress?.Report(new("Finished", 100));
        return new(downloadMbps, uploadMbps, ping, jitter, downloaded + uploaded);
    }

    public async Task<LinkSpeedResult> TestLinkAsync(string address, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a valid HTTP or HTTPS download link.");

        progress?.Report(new("Testing the download server…", 5));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, LinkProbeBytes - 1);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var read = await CopyLimitedAsync(stream, LinkProbeBytes, progress, cancellationToken);
        watch.Stop();
        if (read == 0) throw new IOException("The server returned no downloadable data.");
        progress?.Report(new("Finished", 100));
        return new(MegabitsPerSecond(read, watch.Elapsed), read, uri.Host);
    }

    /// <summary>Persian UI only: checks current APNIC country allocations. It is an estimate, never a billing promise.</summary>
    public async Task<IranRouteResult> ClassifyIranAsync(string address, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new("unknown", "", "");
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken); }
        catch { return new("unknown", "", uri.Host); }
        var ip = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (ip == null) return new("unknown", "", uri.Host);
        if (IPAddress.IsLoopback(ip) || IsPrivate(ip)) return new("domestic", ip.ToString(), uri.Host);

        try
        {
            var registry = await GetApnicRegistryAsync(cancellationToken);
            return new(IsIranAllocation(ip, registry) ? "domestic" : "international", ip.ToString(), uri.Host);
        }
        catch { return new("unknown", ip.ToString(), uri.Host); }
    }

    async Task<string> GetApnicRegistryAsync(CancellationToken cancellationToken)
    {
        if (_apnicRegistry != null && DateTime.UtcNow < _apnicRegistryExpiresUtc) return _apnicRegistry;
        await RegistryLock.WaitAsync(cancellationToken);
        try
        {
            if (_apnicRegistry != null && DateTime.UtcNow < _apnicRegistryExpiresUtc) return _apnicRegistry;
            _apnicRegistry = await _http.GetStringAsync(ApnicUrl, cancellationToken);
            _apnicRegistryExpiresUtc = DateTime.UtcNow.AddHours(24);
            return _apnicRegistry;
        }
        finally { RegistryLock.Release(); }
    }

    async Task<long> ReadBytesAsync(string url, int limit, double basePercent, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await CopyLimitedAsync(stream, limit, progress, cancellationToken, basePercent);
    }

    async Task<long> TryReadBytesAsync(string url, int limit, double basePercent, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { return await ReadBytesAsync(url, limit, basePercent, progress, cancellationToken); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 0) await Task.Delay(250, cancellationToken);
            }
        }
        return 0;
    }

    readonly record struct UploadMeasurement(long Bytes, TimeSpan Elapsed);

    async Task<UploadMeasurement> UploadWithRetryAsync(int bytes, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var payload = new byte[bytes];
                using var content = new ByteArrayContent(payload);
                var watch = Stopwatch.StartNew();
                using var response = await _http.PostAsync($"{UpUrl}?bytes={bytes}&r={Guid.NewGuid():N}", content, cancellationToken);
                response.EnsureSuccessStatusCode();
                watch.Stop();
                return new(bytes, watch.Elapsed);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
                if (attempt < 2) await Task.Delay(300 * (attempt + 1), cancellationToken);
            }
        }
        throw new IOException("The speed-test server could not receive the upload sample. Please try again.", last);
    }

    async Task<UploadMeasurement?> TryUploadWithRetryAsync(int bytes, CancellationToken cancellationToken)
    {
        try { return await UploadWithRetryAsync(bytes, cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    internal static int UploadPayloadBytesFor(double probeMbps)
    {
        if (!double.IsFinite(probeMbps) || probeMbps <= 0) return UploadProbeBytes;
        var sixSecondSample = probeMbps * 1_000_000d / 8d * 6d;
        return (int)Math.Clamp(sixSecondSample, UploadProbeBytes, MaxUploadStreamBytes);
    }

    internal static int UploadStreamCountFor(double probeMbps) => probeMbps >= 5 ? 2 : 1;

    static async Task<long> CopyLimitedAsync(Stream source, int limit, IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken, double basePercent = 10)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (total < limit)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - total)), cancellationToken);
            if (count == 0) break;
            total += count;
            progress?.Report(new("Transferring test data…", Math.Min(96, basePercent + total * 20d / limit)));
        }
        return total;
    }

    static double MegabitsPerSecond(long bytes, TimeSpan elapsed) => elapsed.TotalSeconds <= 0 ? 0 : bytes * 8d / elapsed.TotalSeconds / 1_000_000d;

    static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    internal static bool IsIranAllocation(IPAddress address, string registry)
    {
        foreach (var raw in registry.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var fields = line.Split('|');
            if (fields.Length < 7 || !fields[1].Equals("IR", StringComparison.OrdinalIgnoreCase) || fields[6].StartsWith("available", StringComparison.OrdinalIgnoreCase)) continue;
            if (fields[2] == "ipv4" && address.AddressFamily == AddressFamily.InterNetwork && IPAddress.TryParse(fields[3], out var start) && uint.TryParse(fields[4], out var count))
            {
                var value = Ipv4Number(address); var first = Ipv4Number(start);
                if (value >= first && value - first < count) return true;
            }
            if (fields[2] == "ipv6" && address.AddressFamily == AddressFamily.InterNetworkV6 && IPAddress.TryParse(fields[3], out var prefix) && int.TryParse(fields[4], out var bits) && PrefixMatches(address, prefix, bits)) return true;
        }
        return false;
    }

    static uint Ipv4Number(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    static bool PrefixMatches(IPAddress address, IPAddress prefix, int bits)
    {
        var a = address.GetAddressBytes(); var p = prefix.GetAddressBytes();
        for (var i = 0; i < bits / 8; i++) if (a[i] != p[i]) return false;
        var remainder = bits % 8;
        if (remainder == 0) return true;
        var mask = (byte)(0xFF << (8 - remainder));
        return (a[bits / 8] & mask) == (p[bits / 8] & mask);
    }
}
