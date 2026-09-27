using System.Net;
using System.Net.Sockets;

namespace MakanDownloadManager.Services.Torrent;

public sealed record NatPmpResult(bool Success, int ExternalPort, TimeSpan GrantedLifetime, int ResultCode, string? Error);

/// <summary>
/// NAT-PMP (RFC 6886): a small UDP request/response protocol most non-UPnP home gateways (older Apple AirPort-style
/// routers, some ISP boxes) speak instead. Kept separate from the UPnP client so each is independently testable -
/// this one against a local UDP responder standing in for "the gateway", no multicast or HTTP involved.
/// </summary>
public static class NatPmpClient
{
    const int PublicAddressOpcode = 0, UdpOpcode = 1, TcpOpcode = 2;

    /// <summary>Asks the gateway at <paramref name="gateway"/> (port 5351) to map <paramref name="internalPort"/>.
    /// Retries per RFC 6886's schedule (250ms, doubling, up to ~4 attempts) since NAT-PMP runs over UDP with no
    /// guaranteed delivery.</summary>
    public static async Task<NatPmpResult> RequestMappingAsync(IPEndPoint gateway, int internalPort, int externalPortHint, TimeSpan lifetime, bool tcp, CancellationToken ct)
    {
        var request = new byte[12];
        request[1] = (byte)(tcp ? TcpOpcode : UdpOpcode);
        WriteUInt16(request, 4, (ushort)internalPort);
        WriteUInt16(request, 6, (ushort)externalPortHint);
        WriteUInt32(request, 8, (uint)Math.Clamp(lifetime.TotalSeconds, 0, uint.MaxValue));

        var reply = await ExchangeAsync(gateway, request, ct).ConfigureAwait(false);
        if (reply == null) return new NatPmpResult(false, 0, TimeSpan.Zero, -1, "The gateway did not answer NAT-PMP.");
        if (reply.Length < 16) return new NatPmpResult(false, 0, TimeSpan.Zero, -1, "The gateway's NAT-PMP answer was too short.");
        var resultCode = ReadUInt16(reply, 2);
        if (resultCode != 0) return new NatPmpResult(false, 0, TimeSpan.Zero, resultCode, ResultCodeText(resultCode));
        var externalPort = ReadUInt16(reply, 10);
        var granted = TimeSpan.FromSeconds(ReadUInt32(reply, 12));
        return new NatPmpResult(true, externalPort, granted, 0, null);
    }

    /// <summary>Removes a mapping early (a lifetime of 0 is how NAT-PMP asks for that; the external port field is
    /// zero too - RFC 6886 section 3.4).</summary>
    public static Task<NatPmpResult> DeleteMappingAsync(IPEndPoint gateway, int internalPort, bool tcp, CancellationToken ct) =>
        RequestMappingAsync(gateway, internalPort, 0, TimeSpan.Zero, tcp, ct);

    static async Task<byte[]?> ExchangeAsync(IPEndPoint gateway, byte[] request, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var delay = TimeSpan.FromMilliseconds(250);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { await udp.SendAsync(request, request.Length, gateway).ConfigureAwait(false); }
            catch (SocketException) { return null; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(delay);
            try
            {
                while (true)
                {
                    var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (result.RemoteEndPoint.Address.Equals(gateway.Address) && result.Buffer.Length >= 4 && result.Buffer[0] == 0) return result.Buffer;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { delay += delay; }   // no answer yet: back off and try again
        }
        return null;
    }

    static string ResultCodeText(int code) => code switch
    {
        1 => "Unsupported NAT-PMP version.",
        2 => "The gateway refused (not authorized).",
        3 => "Network failure on the gateway.",
        4 => "The gateway is out of resources.",
        5 => "Unsupported opcode.",
        _ => $"NAT-PMP result code {code}."
    };

    static void WriteUInt16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
    static void WriteUInt32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static ushort ReadUInt16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
    static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
