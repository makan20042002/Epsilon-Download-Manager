using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace MakanDownloadManager.Services.Torrent;

public sealed record UpnpDevice(Uri ControlUrl, string ServiceType);
public sealed record UpnpResult(bool Success, string? Error);

/// <summary>
/// UPnP Internet Gateway Device (IGD) v1/v2: the port-forwarding protocol most home routers speak. Three steps -
/// SSDP multicast discovery finds the router, an HTTP GET of its device description finds the right SOAP endpoint,
/// then plain SOAP-over-HTTP requests add or remove the mapping. Each step takes its input as a parameter (a
/// gateway response, a parsed location, a control URL) rather than doing discovery internally, so the HTTP/SOAP
/// steps can be tested against a local mock server without needing real UPnP multicast traffic.
/// </summary>
public static class UpnpClient
{
    static readonly string[] ServiceTypesToTry =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1"
    };
    static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1"
    };

    /// <summary>SSDP M-SEARCH on the local network; returns every distinct LOCATION a gateway answered with.</summary>
    public static async Task<IReadOnlyList<Uri>> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new List<Uri>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        try
        {
            foreach (var st in SearchTargets)
            {
                var message = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(message);
                await udp.SendAsync(bytes, bytes.Length, multicast).ConfigureAwait(false);
            }
        }
        catch (SocketException) { return found; }   // no route to the multicast group (no network, or it's blocked): nothing to discover

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                var location = ParseLocation(Encoding.ASCII.GetString(result.Buffer));
                if (location != null && !found.Contains(location)) found.Add(location);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* MX window closed: whatever answered, answered */ }
        return found;
    }

    public static Uri? ParseLocation(string ssdpResponse)
    {
        foreach (var line in ssdpResponse.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri) ? uri : null;
        }
        return null;
    }

    /// <summary>Fetches the device description at <paramref name="location"/> and finds the WAN connection service
    /// to send port-mapping requests to.</summary>
    public static async Task<UpnpDevice?> GetControlUrlAsync(Uri location, HttpClient http, CancellationToken ct)
    {
        string xml;
        try { xml = await http.GetStringAsync(location, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
        return ParseDeviceDescription(xml, location);
    }

    public static UpnpDevice? ParseDeviceDescription(string xml, Uri location)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch (System.Xml.XmlException) { return null; }
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        foreach (var wantedType in ServiceTypesToTry)
            foreach (var service in doc.Descendants(ns + "service"))
            {
                var type = service.Element(ns + "serviceType")?.Value?.Trim();
                var controlPath = service.Element(ns + "controlURL")?.Value?.Trim();
                if (type != wantedType || string.IsNullOrEmpty(controlPath)) continue;
                if (!Uri.TryCreate(location, controlPath, out var controlUrl)) continue;
                return new UpnpDevice(controlUrl, type);
            }
        return null;
    }

    public static Task<UpnpResult> AddPortMappingAsync(UpnpDevice device, HttpClient http, int port, bool tcp, string localIp, TimeSpan lease, CancellationToken ct) =>
        SendAsync(device, http, "AddPortMapping",
            $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{(tcp ? "TCP" : "UDP")}</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{localIp}</NewInternalClient><NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>Epsilon Download Manager</NewPortMappingDescription><NewLeaseDuration>{(int)lease.TotalSeconds}</NewLeaseDuration>", ct);

    public static Task<UpnpResult> DeletePortMappingAsync(UpnpDevice device, HttpClient http, int port, bool tcp, CancellationToken ct) =>
        SendAsync(device, http, "DeletePortMapping", $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>{(tcp ? "TCP" : "UDP")}</NewProtocol>", ct);

    static async Task<UpnpResult> SendAsync(UpnpDevice device, HttpClient http, string action, string argumentsXml, CancellationToken ct)
    {
        var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                   $"<s:Body><u:{action} xmlns:u=\"{device.ServiceType}\">{argumentsXml}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, device.ControlUrl) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{device.ServiceType}#{action}\"");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new UpnpResult(true, null);
            return new UpnpResult(false, ExtractFaultString(text) ?? $"The router answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return new UpnpResult(false, ex.Message); }
    }

    static string? ExtractFaultString(string soapFaultXml)
    {
        try
        {
            var doc = XDocument.Parse(soapFaultXml);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
        }
        catch (System.Xml.XmlException) { return null; }
    }
}
