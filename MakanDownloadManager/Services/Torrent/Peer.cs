using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>One TCP connection speaking the BitTorrent peer wire protocol (BEP 3) with the extension protocol (BEP 10).</summary>
public sealed partial class PeerConnection : IDisposable
{
    public const byte Choke = 0, Unchoke = 1, Interested = 2, NotInterested = 3, Have = 4, BitfieldId = 5, Request = 6, Piece = 7, Cancel = 8, Port = 9, Extended = 20;
    const int MaxMessage = 2 * 1024 * 1024;
    static readonly byte[] Protocol = Encoding.ASCII.GetBytes("BitTorrent protocol");

    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly SemaphoreSlim _send = new(1, 1);
    public IPEndPoint EndPoint { get; }
    public bool Incoming { get; }

    public PeerConnection(TcpClient client, IPEndPoint endPoint, bool incoming)
    {
        _client = client; _stream = client.GetStream(); EndPoint = endPoint; Incoming = incoming;
        client.NoDelay = true;
    }

    public static async Task<PeerConnection> ConnectAsync(IPEndPoint endPoint, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient(endPoint.AddressFamily);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(endPoint.Address, endPoint.Port, cts.Token).ConfigureAwait(false);
            return new PeerConnection(client, endPoint, false);
        }
        catch { client.Dispose(); throw; }
    }

    // ---------------------------------------------------------------- handshake

    public sealed record Handshake(byte[] InfoHash, byte[] PeerId, bool SupportsExtensions, bool SupportsDht);

    public async Task SendHandshakeAsync(byte[] infoHash, byte[] peerId, CancellationToken ct)
    {
        var packet = new byte[68];
        packet[0] = 19;
        Protocol.CopyTo(packet, 1);
        packet[25] = 0x10;                      // extension protocol
        packet[27] = 0x01;                      // DHT
        infoHash.CopyTo(packet, 28);
        peerId.CopyTo(packet, 48);
        await WriteAsync(packet, ct).ConfigureAwait(false);
    }

    public async Task<Handshake> ReadHandshakeAsync(CancellationToken ct)
    {
        var packet = new byte[68];
        await ReadExactlyAsync(packet, ct).ConfigureAwait(false);
        if (packet[0] != 19 || !packet.AsSpan(1, 19).SequenceEqual(Protocol)) throw new IOException("The peer does not speak BitTorrent.");
        return new Handshake(packet.AsSpan(28, 20).ToArray(), packet.AsSpan(48, 20).ToArray(), (packet[25] & 0x10) != 0, (packet[27] & 0x01) != 0);
    }

    // ---------------------------------------------------------------- messages

    /// <summary>Reads the next message. A keep-alive is returned as id 255 with an empty payload.</summary>
    public async Task<(byte Id, byte[] Payload)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (length == 0) return (255, Array.Empty<byte>());
        if (length < 0 || length > MaxMessage) throw new IOException("A peer message is too large.");
        var body = new byte[length];
        await ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return (body[0], body.AsSpan(1).ToArray());
    }

    public Task SendAsync(byte id, ReadOnlySpan<byte> payload, CancellationToken ct)
    {
        var packet = new byte[5 + payload.Length];
        var length = payload.Length + 1;
        packet[0] = (byte)(length >> 24); packet[1] = (byte)(length >> 16); packet[2] = (byte)(length >> 8); packet[3] = (byte)length;
        packet[4] = id;
        payload.CopyTo(packet.AsSpan(5));
        return WriteAsync(packet, ct);
    }

    public Task SendKeepAliveAsync(CancellationToken ct) => WriteAsync(new byte[4], ct);

    async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _encrypt?.Process(packet);   // under the send lock, so the cipher stream stays in the order the bytes go out
            await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }

    async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        if (_prefixPosition < _prefix.Length)
        {
            // bytes that arrived during the encryption handshake (already decrypted)
            read = Math.Min(buffer.Length, _prefix.Length - _prefixPosition);
            Buffer.BlockCopy(_prefix, _prefixPosition, buffer, 0, read);
            _prefixPosition += read;
        }
        while (read < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("The peer closed the connection.");
            _decrypt?.Process(buffer.AsSpan(read, n));
            read += n;
        }
    }

    public static byte[] U32(int a) => new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a };

    public static byte[] RequestPayload(int piece, int offset, int length)
    {
        var p = new byte[12];
        U32(piece).CopyTo(p, 0); U32(offset).CopyTo(p, 4); U32(length).CopyTo(p, 8);
        return p;
    }

    public static int ReadInt(ReadOnlySpan<byte> b, int offset) => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];

    public void Dispose()
    {
        try { _client.Close(); } catch (Exception) { }
        _send.Dispose();
    }
}

/// <summary>What the session knows about one connected peer.</summary>
public sealed class PeerState
{
    public PeerConnection Connection { get; }
    public IPEndPoint EndPoint => Connection.EndPoint;
    public byte[] PeerId { get; set; } = Array.Empty<byte>();
    public string Client { get; set; } = "";
    public Bitfield? Have { get; set; }
    /// <summary>A bitfield that arrived before the torrent's metadata (magnet links): applied when the piece count is known.</summary>
    public byte[]? RawBitfield { get; set; }
    /// <summary>Upload requests from this peer that are being served or waiting for the upload limit.</summary>
    public int PendingUploads;
    public readonly object UploadGate = new();
    public Task UploadTail = Task.CompletedTask;
    public bool AmChoking { get; set; } = true;
    public bool AmInterested { get; set; }
    public bool PeerChoking { get; set; } = true;
    public bool PeerInterested { get; set; }
    public bool SupportsExtensions { get; set; }
    public bool SupportsDht { get; set; }
    public int UtMetadataId { get; set; }
    public int UtPexId { get; set; }
    public int MetadataSize { get; set; }
    public int Reqq { get; set; } = 250;
    public HashSet<(int Piece, int Offset)> Outstanding { get; } = new();
    public DateTime LastReceived { get; set; } = DateTime.UtcNow;
    public DateTime Connected { get; } = DateTime.UtcNow;
    public RateMeter Down { get; } = new();
    public RateMeter Up { get; } = new();
    public AdaptivePipeline Pipeline { get; } = new();
    public int Strikes { get; set; }
    public PeerState(PeerConnection connection) => Connection = connection;
    public bool IsSeed => Have is { IsComplete: true };
    public double Progress => Have is null || Have.Length == 0 ? 0 : 100.0 * Have.Count / Have.Length;
    public string Flags => (PeerChoking ? "" : "D") + (AmInterested ? "d" : "") + (AmChoking ? "" : "U") + (PeerInterested ? "u" : "") + (Connection.Incoming ? "I" : "O") + (Connection.Encrypted ? "E" : "");

    /// <summary>"-UT3550-" style ids: the client's name and version, when it says so.</summary>
    public static string ClientFromPeerId(byte[] id)
    {
        if (id.Length == 20 && id[0] == '-' && id[7] == '-')
        {
            var code = Encoding.ASCII.GetString(id, 1, 2);
            var version = Encoding.ASCII.GetString(id, 3, 4).TrimStart('0');
            var name = code switch { "UT" => "µTorrent", "BT" => "BitTorrent", "qB" => "qBittorrent", "TR" => "Transmission", "LT" => "libtorrent", "lt" => "libTorrent", "DE" => "Deluge", "AZ" => "Vuze", "MK" => "Epsilon", "FS" => "Test seeder", "FL" => "Test leecher", "WW" => "WebTorrent", "KT" => "KTorrent", _ => code };
            return name + " " + version;
        }
        return "";
    }
}
