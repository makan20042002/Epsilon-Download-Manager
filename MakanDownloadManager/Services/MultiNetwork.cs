using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>One internet connection of this computer that can carry downloads on its own (Wi-Fi, Ethernet, a tethered phone...).</summary>
public sealed record NetworkLink(string Name, string Kind, IPAddress Address)
{
    public override string ToString() => $"{Kind} \"{Name}\" ({Address})";
}

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
                links.Add(new NetworkLink(nic.Name, KindOf(nic), address));
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
        ConnectCallback = (context, ct) => ConnectFromAsync(link.Address, context.DnsEndPoint, ct)
    }), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Opens a TCP connection whose local end is bound to one adapter's address, which is what makes Windows send it out through that adapter.</summary>
    static async ValueTask<Stream> ConnectFromAsync(IPAddress local, DnsEndPoint target, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(target.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(target.Host, local.AddressFamily, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses.Where(a => a.AddressFamily == local.AddressFamily))
        {
            var socket = new Socket(local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
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
