using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>
/// A small Kademlia node (BEP 5): finds peers for an info hash without any tracker, answers ping / find_node / get_peers / announce_peer.
/// Runs on a UDP port and needs at least one known node to start (the well-known routers, or nodes handed over by the caller).
/// </summary>
public sealed class DhtNode : IDisposable
{
    public static readonly string[] Routers = { "router.bittorrent.com:6881", "dht.transmissionbt.com:6881", "router.utorrent.com:6881", "dht.libtorrent.org:25401" };

    sealed record Contact(byte[] Id, IPEndPoint EndPoint);

    readonly UdpClient _udp;
    readonly byte[] _id = RandomNumberGenerator.GetBytes(20);
    readonly ConcurrentDictionary<string, Contact> _nodes = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<Dictionary<string, object>>> _pending = new();
    readonly ConcurrentDictionary<string, List<IPEndPoint>> _announced = new();
    readonly CancellationTokenSource _cts = new();
    readonly byte[] _secret = RandomNumberGenerator.GetBytes(16);
    int _transaction;
    public int Port { get; }
    public int KnownNodes => _nodes.Count;
    /// <summary>Contact this node before anything else (tests use it to point at a local node).</summary>
    public List<IPEndPoint> Bootstrap { get; } = new();

    public DhtNode(int port)
    {
        try { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)); }
        catch (SocketException) { _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)); }
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    // ---------------------------------------------------------------- lookup

    /// <summary>Asks the closest nodes we know (and the ones they name) who has the torrent. Takes a few seconds.</summary>
    public async Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] infoHash, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(timeout);
        var token = linked.Token;
        var peers = new HashSet<IPEndPoint>();
        var asked = new HashSet<string>();
        var shortlist = new List<Contact>(_nodes.Values.OrderBy(c => Distance(c.Id, infoHash), Comparer<byte[]>.Create(CompareBytes)).Take(16));
        foreach (var ep in Bootstrap) shortlist.Add(new Contact(new byte[20], ep));
        if (shortlist.Count == 0) foreach (var router in Routers) { var ep = await ResolveAsync(router).ConfigureAwait(false); if (ep != null) shortlist.Add(new Contact(new byte[20], ep)); }

        try
        {
            for (var round = 0; round < 8 && !token.IsCancellationRequested; round++)
            {
                var batch = shortlist.Where(c => asked.Add(c.EndPoint.ToString())).Take(8).ToList();
                if (batch.Count == 0) break;
                var replies = await Task.WhenAll(batch.Select(c => QueryAsync(c.EndPoint, "get_peers", new Dictionary<string, object> { ["info_hash"] = infoHash }, TimeSpan.FromSeconds(3), token))).ConfigureAwait(false);
                foreach (var reply in replies)
                {
                    if (reply is null) continue;
                    var r = Bencode.Dict(reply, "r");
                    if (r is null) continue;
                    if (Bencode.List(r, "values") is { } values)
                        foreach (var v in values.OfType<byte[]>()) foreach (var ep in TrackerClient.ParseCompact(v)) peers.Add(ep);
                    if (Bencode.Bytes(r, "nodes") is { } nodes) foreach (var c in ParseNodes(nodes)) { Remember(c); shortlist.Add(c); }
                }
                if (peers.Count >= 20) break;
                shortlist = shortlist.OrderBy(c => Distance(c.Id, infoHash), Comparer<byte[]>.Create(CompareBytes)).Take(24).ToList();
            }
        }
        catch (OperationCanceledException) { /* what was found so far counts */ }
        return peers.ToList();
    }

    static async Task<IPEndPoint?> ResolveAsync(string hostAndPort)
    {
        try
        {
            var parts = hostAndPort.Split(':');
            var addresses = await Dns.GetHostAddressesAsync(parts[0]).ConfigureAwait(false);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return v4 is null ? null : new IPEndPoint(v4, int.Parse(parts[1]));
        }
        catch (Exception) { return null; }
    }

    // ---------------------------------------------------------------- queries

    async Task<Dictionary<string, object>?> QueryAsync(IPEndPoint ep, string method, Dictionary<string, object> args, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _transaction) & 0xFFFF;
        var tcs = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            args["id"] = _id;
            var packet = Bencode.Encode(new Dictionary<string, object> { ["t"] = new[] { (byte)(id >> 8), (byte)id }, ["y"] = "q", ["q"] = method, ["a"] = args });
            await _udp.SendAsync(packet, packet.Length, ep).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout);
            await using var reg = wait.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }
        finally { _pending.TryRemove(id, out _); }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }      // ICMP "port unreachable" from a dead node
            try { Handle(received.Buffer, received.RemoteEndPoint); } catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or InvalidCastException) { /* garbage from the network */ }
        }
    }

    void Handle(byte[] data, IPEndPoint from)
    {
        var msg = (Dictionary<string, object>)Bencode.Parse(data);
        var type = Bencode.Text(msg, "y");
        var tBytes = Bencode.Bytes(msg, "t") ?? Array.Empty<byte>();
        if (type == "r")
        {
            if (tBytes.Length == 2 && _pending.TryGetValue((tBytes[0] << 8) | tBytes[1], out var tcs)) tcs.TrySetResult(msg);
            if (Bencode.Bytes(Bencode.Dict(msg, "r"), "id") is { Length: 20 } senderId) Remember(new Contact(senderId, from));
            return;
        }
        if (type != "q") return;
        var q = Bencode.Text(msg, "q");
        var a = Bencode.Dict(msg, "a");
        if (a is null || Bencode.Bytes(a, "id") is not { Length: 20 } nodeId) return;
        Remember(new Contact(nodeId, from));
        var reply = new Dictionary<string, object> { ["id"] = _id };
        switch (q)
        {
            case "ping": break;
            case "find_node":
                reply["nodes"] = CompactNodes(Bencode.Bytes(a, "target") ?? nodeId);
                break;
            case "get_peers":
                if (Bencode.Bytes(a, "info_hash") is not { Length: 20 } hash) return;
                reply["token"] = Token(from);
                if (_announced.TryGetValue(Convert.ToHexString(hash), out var known) && known.Count > 0)
                    reply["values"] = known.Take(50).Select(ep => (object)CompactPeer(ep)).ToList();
                else reply["nodes"] = CompactNodes(hash);
                break;
            case "announce_peer":
                if (Bencode.Bytes(a, "info_hash") is not { Length: 20 } ih || Bencode.Bytes(a, "token") is not { } token || !token.AsSpan().SequenceEqual(Token(from))) return;
                var port = (Bencode.Long(a, "implied_port") ?? 0) != 0 ? from.Port : (int)(Bencode.Long(a, "port") ?? 0);
                if (port is > 0 and < 65536)
                {
                    var list = _announced.GetOrAdd(Convert.ToHexString(ih), _ => new List<IPEndPoint>());
                    lock (list) { var ep = new IPEndPoint(from.Address, port); if (!list.Contains(ep) && list.Count < 200) list.Add(ep); }
                }
                break;
            default: return;
        }
        var packet = Bencode.Encode(new Dictionary<string, object> { ["t"] = tBytes, ["y"] = "r", ["r"] = reply });
        _ = _udp.SendAsync(packet, packet.Length, from).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
    }

    // ---------------------------------------------------------------- routing table (a flat, bounded list: enough for one client)

    void Remember(Contact c)
    {
        if (c.Id.Length != 20 || c.Id.All(b => b == 0)) return;
        if (_nodes.Count >= 600 && !_nodes.ContainsKey(Convert.ToHexString(c.Id))) return;
        _nodes[Convert.ToHexString(c.Id)] = c;
    }

    byte[] CompactNodes(byte[] target)
    {
        var closest = _nodes.Values.OrderBy(c => Distance(c.Id, target), Comparer<byte[]>.Create(CompareBytes)).Take(8).Where(c => c.EndPoint.AddressFamily == AddressFamily.InterNetwork).ToList();
        var bytes = new byte[closest.Count * 26];
        for (var i = 0; i < closest.Count; i++)
        {
            closest[i].Id.CopyTo(bytes, i * 26);
            closest[i].EndPoint.Address.GetAddressBytes().CopyTo(bytes, i * 26 + 20);
            bytes[i * 26 + 24] = (byte)(closest[i].EndPoint.Port >> 8); bytes[i * 26 + 25] = (byte)closest[i].EndPoint.Port;
        }
        return bytes;
    }

    static IEnumerable<Contact> ParseNodes(byte[] data)
    {
        for (var i = 0; i + 26 <= data.Length; i += 26)
        {
            var port = (data[i + 24] << 8) | data[i + 25];
            if (port == 0) continue;
            yield return new Contact(data.AsSpan(i, 20).ToArray(), new IPEndPoint(new IPAddress(data.AsSpan(i + 20, 4)), port));
        }
    }

    static byte[] CompactPeer(IPEndPoint ep)
    {
        var b = new byte[6];
        ep.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 0);
        b[4] = (byte)(ep.Port >> 8); b[5] = (byte)ep.Port;
        return b;
    }

    byte[] Token(IPEndPoint from) => HMACSHA1.HashData(_secret, from.Address.GetAddressBytes()).AsSpan(0, 8).ToArray();
    static byte[] Distance(byte[] a, byte[] b) { var d = new byte[20]; for (var i = 0; i < 20; i++) d[i] = (byte)(a[i] ^ b[i]); return d; }
    static int CompareBytes(byte[]? a, byte[]? b) => a.AsSpan().SequenceCompareTo(b.AsSpan());

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch (Exception) { }
    }
}
