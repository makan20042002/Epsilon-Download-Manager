using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>One internet connection of this computer that can carry downloads on its own (Wi-Fi, Ethernet, a tethered phone...).</summary>
public sealed record NetworkLink(string Name, string Kind, IPAddress Address, long SpeedBps = 0, bool IsMetered = false, string Color = "#7568DF", int InterfaceIndex = 0)
{
    public override string ToString() => $"{Kind} \"{Name}\" ({Address})";
}

public sealed record NetworkProbeResult(NetworkLink Link, bool Success, long BytesPerSecond, string PublicAddress, string Error);

/// <summary>
/// Finds the network connections that currently have their own route to the internet. Multi-Network (off by default)
/// spreads the connections of one large download over all of them, so their speeds add up.
/// </summary>
public static class NetworkLinks
{
    public static IReadOnlyList<NetworkLink> Detect()
    {
        var links = new List<NetworkLink>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties properties;
                try { properties = nic.GetIPProperties(); } catch (Exception) { continue; }
                // Only an adapter with its own gateway can reach the internet by itself; this also leaves out the
                // host-only adapters that virtual machines add.
                if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                var address = properties.UnicastAddresses.Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !IsLinkLocal(a));
                if (address == null) continue;
                long speed; try { speed = Math.Max(0, nic.Speed); } catch (Exception) { speed = 0; }
                var kind = KindOf(nic);
                var metered = kind is "Mobile" or "USB tethering";
                int interfaceIndex; try { interfaceIndex = properties.GetIPv4Properties()?.Index ?? 0; } catch (Exception) { interfaceIndex = 0; }
                links.Add(new NetworkLink(nic.Name, kind, address, speed, metered, "#7568DF", interfaceIndex));
            }
        }
        catch (Exception) { /* the list of adapters could not be read: behave as if there is one ordinary connection */ }
        return links;
    }

    static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    static string KindOf(NetworkInterface nic)
    {
        var description = nic.Description ?? "";
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Wman) return "Mobile";
        if (description.Contains("NDIS", StringComparison.OrdinalIgnoreCase) || description.Contains("tether", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Apple Mobile", StringComparison.OrdinalIgnoreCase)) return "USB tethering";
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx) return "Ethernet";
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp) return "Dial-up / VPN";
        return "Network";
    }
}

public sealed partial class DownloadManager
{
    /// <summary>Multi-Network: when on, a segmented download spreads its connections over every network that is connected
    /// (Wi-Fi + Ethernet + tethered phone...). Off by default; has no effect while only one network is connected.</summary>
    public bool MultiNetworkEnabled { get; set; }

    /// <summary>Adapters (by name) the user unticked in the Multi-Network panel: never used for Multi-Network.</summary>
    public IReadOnlySet<string> MultiNetworkExcluded { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files smaller than this are not worth spreading over several networks.</summary>
    public long MultiNetworkMinBytes { get; set; } = 32L * 1024 * 1024;
    /// <summary>True: the faster adapter gets more of the connections. False: every adapter gets the same number.</summary>
    public bool MultiNetworkBalanceBySpeed { get; set; } = true;
    public bool MultiNetworkAvoidMetered { get; set; } = true;
    public bool MultiNetworkKeepOneFree { get; set; }
    public bool MultiNetworkAskNewNetwork { get; set; }
    public IReadOnlySet<string> MultiNetworkKnown { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> MultiNetworkColors { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int MultiNetworkRetryMinutes { get; set; } = 5;
    public long MultiNetworkDailyLimitBytes { get; set; }
    public long MultiNetworkTodayBytes => Interlocked.Read(ref _multiNetworkTodayBytes);
    public Action<DateOnly, long>? MultiNetworkUsageChanged { get; set; }
    long _multiNetworkTodayBytes;
    DateOnly _multiNetworkUsageDay = DateOnly.FromDateTime(DateTime.Now);
    long _lastPersistedMultiNetworkBytes;

    public void RestoreMultiNetworkUsage(DateOnly day, long bytes)
    {
        _multiNetworkUsageDay = day;
        Interlocked.Exchange(ref _multiNetworkTodayBytes, day == DateOnly.FromDateTime(DateTime.Now) ? Math.Max(0, bytes) : 0);
    }

    void ResetUsageDayIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _multiNetworkUsageDay) return;
        _multiNetworkUsageDay = today;
        Interlocked.Exchange(ref _multiNetworkTodayBytes, 0);
        Interlocked.Exchange(ref _lastPersistedMultiNetworkBytes, 0);
        MultiNetworkUsageChanged?.Invoke(today, 0);
    }

    void RecordMultiNetworkBytes(long bytes)
    {
        if (bytes <= 0) return;
        ResetUsageDayIfNeeded();
        var total = Interlocked.Add(ref _multiNetworkTodayBytes, bytes);
        var previous = Interlocked.Read(ref _lastPersistedMultiNetworkBytes);
        if (total - previous < 4L * 1024 * 1024 || Interlocked.CompareExchange(ref _lastPersistedMultiNetworkBytes, total, previous) != previous) return;
        MultiNetworkUsageChanged?.Invoke(_multiNetworkUsageDay, total);
    }

    bool MultiNetworkBudgetAvailable()
    {
        ResetUsageDayIfNeeded();
        return MultiNetworkDailyLimitBytes <= 0 || MultiNetworkTodayBytes < MultiNetworkDailyLimitBytes;
    }

    /// <summary>The networks Multi-Network would use right now: connected and not unticked by the user.</summary>
    public IReadOnlyList<NetworkLink> UsableLinks()
    {
        if (!MultiNetworkBudgetAvailable()) return Array.Empty<NetworkLink>();
        var links = CurrentLinks().Where(l => !MultiNetworkExcluded.Contains(l.Name));
        if (MultiNetworkAvoidMetered) links = links.Where(l => !l.IsMetered);
        if (MultiNetworkAskNewNetwork) links = links.Where(l => MultiNetworkKnown.Contains(l.Name));
        var list = links.Select(l => l with { Color = MultiNetworkColors.TryGetValue(l.Name, out var color) ? color : l.Color }).ToList();
        if (MultiNetworkKeepOneFree && list.Count > 1)
        {
            var keep = list.OrderByDescending(l => l.SpeedBps).First();
            list.Remove(keep);
        }
        return list;
    }

    /// <summary>Which network each connection of a download uses. Every network gets at least one connection; with
    /// "balance by speed" the rest are shared in proportion to the adapters' link speeds.</summary>
    internal static int[] AssignWorkers(IReadOnlyList<NetworkLink> links, int workers, bool bySpeed)
    {
        var map = new int[Math.Max(1, workers)];
        if (links.Count == 0) return map;
        var total = links.Sum(l => (double)Math.Max(0, l.SpeedBps));
        if (!bySpeed || total <= 0 || links.Any(l => l.SpeedBps <= 0) || map.Length <= links.Count)
        {
            for (var i = 0; i < map.Length; i++) map[i] = i % links.Count;
            return map;
        }
        var counts = new int[links.Count];
        for (var i = 0; i < counts.Length; i++) counts[i] = 1;
        for (var left = map.Length - links.Count; left > 0; left--)
        {
            // give the next connection to the network that is furthest below its fair share
            var best = 0; var bestGap = double.MinValue;
            for (var i = 0; i < counts.Length; i++)
            {
                var gap = links[i].SpeedBps / total - counts[i] / (double)map.Length;
                if (gap > bestGap) { bestGap = gap; best = i; }
            }
            counts[best]++;
        }
        // interleave, so that when throttling retires the highest-numbered connections every network keeps some
        var k = 0;
        while (k < map.Length)
            for (var i = 0; i < counts.Length && k < map.Length; i++)
                if (counts[i] > 0) { counts[i]--; map[k++] = i; }
        return map;
    }

    /// <summary>Replaces the adapter scan (tests).</summary>
    public Func<IReadOnlyList<NetworkLink>>? LinkProbe { get; set; }

    /// <summary>After this many failures on one network, a download stops using that network and carries on over the others.</summary>
    const int LinkFailureLimit = 3;

    readonly ConcurrentDictionary<string, HttpClient> _linkClients = new(StringComparer.Ordinal);

    /// <summary>The networks that could carry a download right now.</summary>
    public IReadOnlyList<NetworkLink> CurrentLinks()
    {
        try { return (LinkProbe ?? NetworkLinks.Detect)(); }
        catch (Exception) { return Array.Empty<NetworkLink>(); }
    }

    /// <summary>How much each network has carried for this download (empty unless Multi-Network is in use for it).</summary>
    public IReadOnlyList<(NetworkLink Link, long Bytes, bool Active)> GetLinkUsage(DownloadItem item)
    {
        if (!_live.TryGetValue(item.Id, out var live) || live.Links is not { } links) return Array.Empty<(NetworkLink, long, bool)>();
        var rows = new List<(NetworkLink, long, bool)>();
        for (var i = 0; i < links.Count; i++) rows.Add((links[i], Interlocked.Read(ref live.LinkBytes[i]), Volatile.Read(ref live.LinkFailures[i]) < LinkFailureLimit));
        return rows;
    }

    /// <summary>An HTTP client whose every connection leaves through the given network. Same redirect, proxy and time-out rules as the normal one.</summary>
    HttpClient ClientFor(NetworkLink link) => _linkClients.GetOrAdd(link.Address.ToString(), _ => new HttpClient(new SafeRedirectHandler(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 64,
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
        UseProxy = true, Proxy = new LiveProxy(this),
        ConnectCallback = (context, ct) => ConnectFromAsync(link, context.DnsEndPoint, ct)
    }), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Performs a small real transfer through every selected adapter. This verifies the route binding rather
    /// than merely reporting that Windows lists the adapter as connected.</summary>
    public async Task<IReadOnlyList<NetworkProbeResult>> ProbeNetworkLinksAsync(CancellationToken ct)
    {
        var links = CurrentLinks().Where(l => !MultiNetworkExcluded.Contains(l.Name)).ToList();
        var tasks = links.Select(async link =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://speed.cloudflare.com/__down?bytes=1048576") { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                using var response = await ClientFor(link).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024]; long bytes = 0;
                while (bytes < 1024 * 1024) { var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 1024 * 1024 - bytes)), timeout.Token).ConfigureAwait(false); if (read == 0) break; bytes += read; }
                clock.Stop();
                var rate = clock.Elapsed.TotalSeconds > 0 ? (long)(bytes / clock.Elapsed.TotalSeconds) : 0;
                return new NetworkProbeResult(link, bytes > 0, rate, "", bytes > 0 ? "" : "No data was received.");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException)
            {
                return new NetworkProbeResult(link, false, 0, "", ex is OperationCanceledException ? "Timed out." : ex.Message);
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Opens a TCP connection whose local end is bound to one adapter's address, which is what makes Windows send it out through that adapter.</summary>
    static async ValueTask<Stream> ConnectFromAsync(NetworkLink link, DnsEndPoint target, CancellationToken ct)
    {
        var local = link.Address;
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(target.Host, local.AddressFamily, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses.Where(a => a.AddressFamily == local.AddressFamily))
        {
            var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Binding selects the source address; IP_UNICAST_IF also forces Windows to use this interface when
                // several default routes/VPNs exist. Without both, Windows may silently send every worker over the
                // lowest-metric route even though two adapters were displayed in the UI.
                if (link.InterfaceIndex > 0)
                {
                    var index = local.AddressFamily == AddressFamily.InterNetwork ? IPAddress.HostToNetworkOrder(link.InterfaceIndex) : link.InterfaceIndex;
                    const int IpUnicastIf = 31; // Windows IP_UNICAST_IF / IPV6_UNICAST_IF
                    socket.SetSocketOption(local.AddressFamily == AddressFamily.InterNetwork ? SocketOptionLevel.IP : SocketOptionLevel.IPv6, (SocketOptionName)IpUnicastIf, index);
                }
                socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex) { socket.Dispose(); last = ex; }
            catch (Exception) { socket.Dispose(); throw; }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    void DisposeLinkClients()
    {
        foreach (var client in _linkClients.Values) { try { client.Dispose(); } catch (Exception) { } }
        _linkClients.Clear();
    }

    /// <summary>A request failed on one particular network; the chunk is retried at once without counting as a download error.</summary>
    sealed class LinkFailedException : IOException
    {
        public LinkFailedException(string message, Exception inner) : base(message, inner) { }
    }
}
