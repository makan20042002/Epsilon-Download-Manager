using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MakanDownloadManager.Services.Torrent;

public sealed class TorrentEngineOptions
{
    /// <summary>First port to try for incoming peers (TCP) and the DHT (UDP). 0 = any free port.</summary>
    public int ListenPort { get; set; } = 6881;
    public bool EnableDht { get; set; } = true;
    public bool EnablePex { get; set; } = true;
    /// <summary>Ask the router to forward the listening port automatically (UPnP, then NAT-PMP). Torrents work
    /// without this - it only affects how many peers can connect in rather than only out.</summary>
    public bool EnablePortMapping { get; set; } = true;
    public int MaxPeersPerTorrent { get; set; } = 60;
    public int MaxConnections { get; set; } = 200;
    public int UploadSlots { get; set; } = 4;
    /// <summary>Keep uploading after the download is complete.</summary>
    public bool SeedAfterCompletion { get; set; } = true;
    /// <summary>Stop seeding at this share ratio (uploaded / downloaded); 0 = never.</summary>
    public double SeedRatioLimit { get; set; } = 1.0;
    /// <summary>Stop seeding this long after completion; zero = never.</summary>
    public TimeSpan SeedTimeLimit { get; set; } = TimeSpan.Zero;
    public TimeSpan MinAnnounceInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UdpTrackerTimeout { get; set; } = TimeSpan.FromSeconds(6);
    /// <summary>Where resume data and a copy of every .torrent are kept.</summary>
    public string StateDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "makan-torrents");
}

/// <summary>
/// The BitTorrent client: one listening port, an optional DHT node, global speed limits and all torrents. Written from the protocol
/// specifications (BEP 3, 5, 9, 10, 11, 12, 15, 23); it does not implement protocol encryption, uTP or web seeds.
/// </summary>
public sealed class TorrentEngine : IDisposable
{
    readonly ConcurrentDictionary<string, TorrentSession> _sessions = new();
    readonly CancellationTokenSource _cts = new();
    TcpListener? _listener;
    PortMapper? _portMapper;    int _connections;

    public TorrentEngineOptions Options { get; }
    public byte[] PeerId { get; } = MakePeerId();
    public int ListenPort { get; private set; }
    public DhtNode? Dht { get; private set; }
    /// <summary>Whether the router opened the listening port for us, and how (for a status line in the UI - "Port 6881 open (UPnP)" vs nothing yet).</summary>
    public bool PortMapped => _portMapper?.IsMapped ?? false;
    public PortMapMethod PortMapMethod => _portMapper?.MethodInUse ?? PortMapMethod.None;
    public string? PortMapStatus => _portMapper?.StatusText;
    public TokenBucket DownloadLimit { get; } = new();
    public TokenBucket UploadLimit { get; } = new();
    /// <summary>Tests connect to peers on 127.0.0.1 even when the port equals ours.</summary>
    public bool AllowLocalPeers { get; set; }
    public IReadOnlyList<TorrentSession> Sessions => _sessions.Values.ToList();

    public TorrentEngine(TorrentEngineOptions options) => Options = options;

    static byte[] MakePeerId()
    {
        var id = new byte[20];
        Encoding.ASCII.GetBytes("-MK0160-").CopyTo(id, 0);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        for (var i = 8; i < 20; i++) id[i] = (byte)alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return id;
    }

    /// <summary>Opens the listening port (and the DHT). Safe to call once.</summary>
    public void Start()
    {
        if (_listener != null) return;
        var first = Options.ListenPort;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var port = first == 0 ? 0 : first + attempt;
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(64);
                _listener = listener;
                ListenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                break;
            }
            catch (SocketException) when (first != 0 && attempt < 29) { }
        }
        if (_listener == null) throw new IOException("No port is free for incoming BitTorrent connections.");
        Directory.CreateDirectory(Options.StateDirectory);
        if (Options.EnableDht) Dht = new DhtNode(ListenPort);
        if (Options.EnablePortMapping) { _portMapper = new PortMapper(); _portMapper.Start(ListenPort); }   // best-effort; torrents work fine while it's still trying, or if it never succeeds
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleIncomingAsync(client, ct), ct);
        }
    }

    async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        if (!TryReserveConnection()) { client.Dispose(); return; }
        try
        {
            var conn = new PeerConnection(client, (IPEndPoint)client.Client.RemoteEndPoint!, true);
            PeerConnection.Handshake hs;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                hs = await conn.ReadHandshakeAsync(timeout.Token).ConfigureAwait(false);
            }
            if (!_sessions.TryGetValue(Convert.ToHexString(hs.InfoHash).ToLowerInvariant(), out var session)) { conn.Dispose(); return; }
            await session.AcceptAsync(conn, hs).ConfigureAwait(false);
        }
        catch (Exception) { client.Dispose(); }
        finally { ReleaseConnection(); }
    }

    internal bool TryReserveConnection()
    {
        if (Interlocked.Increment(ref _connections) <= Options.MaxConnections) return true;
        Interlocked.Decrement(ref _connections);
        return false;
    }

    internal void ReleaseConnection() => Interlocked.Decrement(ref _connections);

    // ---------------------------------------------------------------- torrents

    public TorrentSession Find(byte[] infoHash) => _sessions.TryGetValue(Convert.ToHexString(infoHash).ToLowerInvariant(), out var s) ? s : null!;

    /// <summary>Adds a torrent from a magnet link. The session is not started.</summary>
    public TorrentSession AddMagnet(MagnetLink link, string saveDirectory)
    {
        var key = link.InfoHashHex;
        if (_sessions.TryGetValue(key, out var existing)) return existing;
        var stored = LoadTorrentFile(key);        // metadata of an earlier run: no need to fetch it again
        var session = new TorrentSession(this, link.InfoHash, link, stored, saveDirectory);
        return _sessions.GetOrAdd(key, session);
    }

    public TorrentSession AddTorrent(MetaInfo meta, string saveDirectory)
    {
        var key = meta.InfoHashHex;
        if (_sessions.TryGetValue(key, out var existing)) return existing;
        SaveTorrentFile(meta);
        return _sessions.GetOrAdd(key, new TorrentSession(this, meta.InfoHash, null, meta, saveDirectory));
    }

    public async Task RemoveAsync(TorrentSession session, bool deleteData)
    {
        _sessions.TryRemove(session.InfoHashHex, out _);
        await session.StopAsync(deleteData).ConfigureAwait(false);
        if (deleteData) DeleteTorrentFile(session.InfoHashHex);
    }

    // ---------------------------------------------------------------- state on disk

    string ResumePath(string hash) => Path.Combine(Options.StateDirectory, hash + ".resume.json");
    string TorrentPath(string hash) => Path.Combine(Options.StateDirectory, hash + ".torrent");

    internal TorrentResume? LoadResume(string hash)
    {
        try { var p = ResumePath(hash); return File.Exists(p) ? JsonSerializer.Deserialize<TorrentResume>(File.ReadAllText(p)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    internal void SaveResume(string hash, TorrentResume resume)
    {
        Directory.CreateDirectory(Options.StateDirectory);
        var temp = ResumePath(hash) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(resume));
        File.Move(temp, ResumePath(hash), true);
    }

    internal void DeleteResume(string hash) { try { File.Delete(ResumePath(hash)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    void DeleteTorrentFile(string hash) { try { File.Delete(TorrentPath(hash)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    internal void SaveTorrentFile(MetaInfo meta)
    {
        try
        {
            Directory.CreateDirectory(Options.StateDirectory);
            var path = TorrentPath(meta.InfoHashHex);
            if (!File.Exists(path)) File.WriteAllBytes(path, meta.ToTorrentFile());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    MetaInfo? LoadTorrentFile(string hash)
    {
        try { var p = TorrentPath(hash); return File.Exists(p) ? MetaInfo.Parse(File.ReadAllBytes(p)) : null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Pauses every torrent (saving its position) and closes the ports; waits at most <paramref name="timeout"/>.</summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        try { await Task.WhenAll(_sessions.Values.Select(s => s.PauseAsync())).WaitAsync(timeout).ConfigureAwait(false); } catch (Exception) { }
        if (_portMapper != null) { try { await _portMapper.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { } _portMapper = null; }
        Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        Dht?.Dispose();
        if (_portMapper != null) _ = _portMapper.DisposeAsync().AsTask();   // best-effort, fire-and-forget: Dispose() itself cannot await
    }
}
