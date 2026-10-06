using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MakanDownloadManager.Services.Torrent;

public sealed record AnnounceRequest(byte[] InfoHash, byte[] PeerId, int Port, long Uploaded, long Downloaded, long Left, string Event, int NumWant = 50);

public sealed record TrackerResponse(IReadOnlyList<IPEndPoint> Peers, int IntervalSeconds, int? Seeders, int? Leechers, string? Warning);

public sealed class TrackerException : Exception
{
    public TrackerException(string message) : base(message) { }
}

/// <summary>Announces to HTTP(S) trackers (BEP 3, compact peers BEP 23) and UDP trackers (BEP 15).</summary>
public static class TrackerClient
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Makan/16");
        return client;
    }

    public static Task<TrackerResponse> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct, TimeSpan? udpTimeout = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new TrackerException("Bad tracker address.");
        return uri.Scheme switch
        {
            "http" or "https" => AnnounceHttpAsync(uri, request, ct),
            "udp" => AnnounceUdpAsync(uri, request, ct, udpTimeout ?? TimeSpan.FromSeconds(6)),
            _ => throw new TrackerException("Unsupported tracker protocol: " + uri.Scheme)
        };
    }

    // ---------------------------------------------------------------- HTTP

    static string Percent(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append('%').Append(b.ToString("X2"));
        return sb.ToString();
    }

    static async Task<TrackerResponse> AnnounceHttpAsync(Uri uri, AnnounceRequest r, CancellationToken ct)
    {
        var query = $"info_hash={Percent(r.InfoHash)}&peer_id={Percent(r.PeerId)}&port={r.Port}&uploaded={r.Uploaded}&downloaded={r.Downloaded}&left={r.Left}&compact=1&numwant={r.NumWant}";
        if (r.Event.Length > 0) query += "&event=" + r.Event;
        var builder = new UriBuilder(uri) { Query = (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query.TrimStart('?') + "&") + query };
        byte[] body;
        try
        {
            using var response = await Http.GetAsync(builder.Uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TrackerException("The tracker answered HTTP " + (int)response.StatusCode + ".");
            body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { throw new TrackerException(ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TrackerException("The tracker did not answer in time."); }
        return ParseHttp(body);
    }

    public static TrackerResponse ParseHttp(byte[] body)
    {
        object parsed;
        try { parsed = Bencode.Parse(body); } catch (FormatException) { throw new TrackerException("The tracker's answer is not valid."); }
        if (Bencode.Text(parsed, "failure reason") is { } failure) throw new TrackerException(failure);
        var peers = new List<IPEndPoint>();
        if (parsed is Dictionary<string, object> d)
        {
            if (d.TryGetValue("peers", out var p))
            {
                if (p is byte[] compact) peers.AddRange(ParseCompact(compact));
                else if (p is List<object> list)
                    foreach (var entry in list)
                        if (Bencode.Text(entry, "ip") is { } ip && Bencode.Long(entry, "port") is { } port && IPAddress.TryParse(ip, out var address) && port is > 0 and < 65536)
                            peers.Add(new IPEndPoint(address, (int)port));
            }
            if (Bencode.Bytes(d, "peers6") is { } six) peers.AddRange(ParseCompact6(six));
        }
        var interval = (int)Math.Clamp(Bencode.Long(parsed, "interval") ?? 1800, 1, 7200);
        return new TrackerResponse(peers, interval, (int?)Bencode.Long(parsed, "complete"), (int?)Bencode.Long(parsed, "incomplete"), Bencode.Text(parsed, "warning message"));
    }

    public static IEnumerable<IPEndPoint> ParseCompact(byte[] data)
    {
        for (var i = 0; i + 6 <= data.Length; i += 6)
        {
            var port = (data[i + 4] << 8) | data[i + 5];
            if (port == 0) continue;
            yield return new IPEndPoint(new IPAddress(data.AsSpan(i, 4)), port);
        }
    }

    static IEnumerable<IPEndPoint> ParseCompact6(byte[] data)
    {
        for (var i = 0; i + 18 <= data.Length; i += 18)
        {
            var port = (data[i + 16] << 8) | data[i + 17];
            if (port == 0) continue;
            yield return new IPEndPoint(new IPAddress(data.AsSpan(i, 16)), port);
        }
    }

    // ---------------------------------------------------------------- UDP

    static async Task<TrackerResponse> AnnounceUdpAsync(Uri uri, AnnounceRequest r, CancellationToken ct, TimeSpan timeout)
    {
        var address = (await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                      ?? throw new TrackerException("The tracker's address could not be found.");
        using var udp = new UdpClient(address.AddressFamily);
        var endpoint = new IPEndPoint(address, uri.Port > 0 ? uri.Port : 80);
        var random = new Random();

        async Task<byte[]> Exchange(byte[] packet, int expectedAction, int transaction)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await udp.SendAsync(packet, packet.Length, endpoint).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(timeout);
                try
                {
                    while (true)
                    {
                        var result = await udp.ReceiveAsync(wait.Token).ConfigureAwait(false);
                        var b = result.Buffer;
                        if (b.Length < 8 || ReadInt(b, 4) != transaction) continue;
                        var action = ReadInt(b, 0);
                        if (action == 3) throw new TrackerException(Encoding.UTF8.GetString(b, 8, b.Length - 8));
                        if (action == expectedAction) return b;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* timeout: try again */ }
            }
            throw new TrackerException("The UDP tracker did not answer.");
        }

        var tx1 = random.Next();
        var connect = new byte[16];
        WriteLong(connect, 0, 0x41727101980);
        WriteInt(connect, 8, 0); WriteInt(connect, 12, tx1);
        var connectReply = await Exchange(connect, 0, tx1).ConfigureAwait(false);
        if (connectReply.Length < 16) throw new TrackerException("The UDP tracker's answer is too short.");
        var connectionId = ReadLong(connectReply, 8);

        var tx2 = random.Next();
        var announce = new byte[98];
        WriteLong(announce, 0, connectionId); WriteInt(announce, 8, 1); WriteInt(announce, 12, tx2);
        r.InfoHash.CopyTo(announce, 16); r.PeerId.CopyTo(announce, 36);
        WriteLong(announce, 56, r.Downloaded); WriteLong(announce, 64, r.Left); WriteLong(announce, 72, r.Uploaded);
        WriteInt(announce, 80, r.Event switch { "completed" => 1, "started" => 2, "stopped" => 3, _ => 0 });
        WriteInt(announce, 84, 0); WriteInt(announce, 88, random.Next()); WriteInt(announce, 92, r.NumWant);
        announce[96] = (byte)(r.Port >> 8); announce[97] = (byte)r.Port;
        var reply = await Exchange(announce, 1, tx2).ConfigureAwait(false);
        if (reply.Length < 20) throw new TrackerException("The UDP tracker's answer is too short.");
        var interval = Math.Clamp(ReadInt(reply, 8), 1, 7200);
        var peers = address.AddressFamily == AddressFamily.InterNetworkV6 ? ParseCompact6(reply.AsSpan(20).ToArray()) : ParseCompact(reply.AsSpan(20).ToArray());
        return new TrackerResponse(peers.ToList(), interval, ReadInt(reply, 16), ReadInt(reply, 12), null);
    }

    static int ReadInt(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    static long ReadLong(byte[] b, int o) => ((long)ReadInt(b, o) << 32) | (uint)ReadInt(b, o + 4);
    static void WriteInt(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void WriteLong(byte[] b, int o, long v) { WriteInt(b, o, (int)(v >> 32)); WriteInt(b, o + 4, (int)v); }
}
