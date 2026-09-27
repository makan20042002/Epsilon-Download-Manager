using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MakanDownloadManager.Services.Torrent;

public enum PortMapMethod { None, Upnp, NatPmp }

/// <summary>
/// Opens the torrent listening port on the router automatically, so peers behind the same kind of NAT most home
/// connections use can actually reach this computer instead of only ever connecting outward. Tries UPnP first
/// (most routers from the last ~15 years), NAT-PMP second (older Apple gear, some ISP boxes); if neither answers,
/// torrents keep working exactly as before - outbound connections and any peer that can reach us regardless.
/// Leases are short (30 minutes) and renew themselves at the two-thirds mark, so a crashed process does not leave
/// a stale forward sitting on someone's router forever.
/// </summary>
public sealed class PortMapper : IAsyncDisposable
{
    static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    readonly object _gate = new();
    CancellationTokenSource? _cts;
    Task? _loop;
    bool _disposed;
    int _port;
    IPEndPoint? _natPmpGateway;
    UpnpDevice? _upnpDevice;

    public bool IsMapped { get; private set; }
    public PortMapMethod MethodInUse { get; private set; } = PortMapMethod.None;
    /// <summary>What actually happened, for a status line in the UI (a successful map, or why it didn't).</summary>
    public string? StatusText { get; private set; }

    /// <summary>Starts trying in the background; safe to call once per port. Torrents work fine while this is still
    /// discovering, or if it never succeeds at all.</summary>
    public void Start(int port)
    {
        lock (_gate)
        {
            if (_loop != null) return;
            _port = port;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!IsMapped)
                {
                    if (await TryUpnpAsync(ct).ConfigureAwait(false)) { }
                    else await TryNatPmpAsync(ct).ConfigureAwait(false);
                }
                var wait = IsMapped ? TimeSpan.FromTicks(Lease.Ticks * 2 / 3) : TimeSpan.FromMinutes(2);
                try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                if (IsMapped) await RenewAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task<bool> TryUpnpAsync(CancellationToken ct)
    {
        try
        {
            var locations = await UpnpClient.DiscoverAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            foreach (var location in locations)
            {
                var device = await UpnpClient.GetControlUrlAsync(location, Http, ct).ConfigureAwait(false);
                if (device == null) continue;
                var localIp = GetLocalAddress();
                if (localIp == null) continue;
                var tcp = await UpnpClient.AddPortMappingAsync(device, Http, _port, true, localIp, Lease, ct).ConfigureAwait(false);
                var udp = await UpnpClient.AddPortMappingAsync(device, Http, _port, false, localIp, Lease, ct).ConfigureAwait(false);
                if (tcp.Success && udp.Success)
                {
                    _upnpDevice = device; IsMapped = true; MethodInUse = PortMapMethod.Upnp;
                    StatusText = $"Port {_port} opened on the router (UPnP).";
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException) { }
        return false;
    }

    async Task TryNatPmpAsync(CancellationToken ct)
    {
        var gateway = GetGateway();
        if (gateway == null) { StatusText = "No router was found to ask for port forwarding."; return; }
        try
        {
            var tcp = await NatPmpClient.RequestMappingAsync(gateway, _port, _port, Lease, true, ct).ConfigureAwait(false);
            var udp = await NatPmpClient.RequestMappingAsync(gateway, _port, _port, Lease, false, ct).ConfigureAwait(false);
            if (tcp.Success && udp.Success)
            {
                _natPmpGateway = gateway; IsMapped = true; MethodInUse = PortMapMethod.NatPmp;
                StatusText = $"Port {_port} opened on the router (NAT-PMP).";
            }
            else StatusText = "The router did not open the port (UPnP and NAT-PMP both failed - this is normal on some routers).";
        }
        catch (SocketException) { StatusText = "The router did not answer (UPnP and NAT-PMP both failed - this is normal on some routers)."; }
    }

    async Task RenewAsync(CancellationToken ct)
    {
        try
        {
            if (MethodInUse == PortMapMethod.Upnp && _upnpDevice != null)
            {
                var localIp = GetLocalAddress();
                if (localIp == null) return;
                await UpnpClient.AddPortMappingAsync(_upnpDevice, Http, _port, true, localIp, Lease, ct).ConfigureAwait(false);
                await UpnpClient.AddPortMappingAsync(_upnpDevice, Http, _port, false, localIp, Lease, ct).ConfigureAwait(false);
            }
            else if (MethodInUse == PortMapMethod.NatPmp && _natPmpGateway != null)
            {
                await NatPmpClient.RequestMappingAsync(_natPmpGateway, _port, _port, Lease, true, ct).ConfigureAwait(false);
                await NatPmpClient.RequestMappingAsync(_natPmpGateway, _port, _port, Lease, false, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException) { IsMapped = false; MethodInUse = PortMapMethod.None; }   // the router went away or changed its mind: try discovery again next round
    }

    /// <summary>The address this computer would use to reach the internet - not necessarily the only local address,
    /// but the one the router's NAT table needs for "forward to this machine".</summary>
    internal static string? GetLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);   // UDP "connect" only picks a route; nothing is actually sent
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException) { return null; }
    }

    internal static IPEndPoint? GetGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                        return new IPEndPoint(gateway.Address, 5351);
            }
        }
        catch (NetworkInformationException) { }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts; Task? loop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _cts; loop = _loop;
        }
        if (cts == null) return;
        cts.Cancel();
        try { if (loop != null) await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
        if (!IsMapped) return;
        try
        {
            using var teardown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (MethodInUse == PortMapMethod.Upnp && _upnpDevice != null)
            {
                await UpnpClient.DeletePortMappingAsync(_upnpDevice, Http, _port, true, teardown.Token).ConfigureAwait(false);
                await UpnpClient.DeletePortMappingAsync(_upnpDevice, Http, _port, false, teardown.Token).ConfigureAwait(false);
            }
            else if (MethodInUse == PortMapMethod.NatPmp && _natPmpGateway != null)
            {
                await NatPmpClient.DeleteMappingAsync(_natPmpGateway, _port, true, teardown.Token).ConfigureAwait(false);
                await NatPmpClient.DeleteMappingAsync(_natPmpGateway, _port, false, teardown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* the lease will simply expire on its own in the meantime */ }
        finally { IsMapped = false; MethodInUse = PortMapMethod.None; }
    }
}
