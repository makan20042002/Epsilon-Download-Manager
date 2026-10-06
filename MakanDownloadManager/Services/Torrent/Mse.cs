using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>How peer connections are protected with Message Stream Encryption (the "protocol encryption" of other clients).</summary>
public enum EncryptionMode
{
    /// <summary>Never encrypt; encrypted incoming connections are refused.</summary>
    Off,
    /// <summary>Try encrypted first and fall back to a plain connection; accept both kinds.</summary>
    Prefer,
    /// <summary>Only encrypted connections, in and out.</summary>
    Require
}

/// <summary>
/// Message Stream Encryption / Protocol Encryption: a Diffie-Hellman key exchange followed by RC4 over the whole peer
/// stream. It is what µTorrent, BitTorrent, qBittorrent and Transmission speak, so peers that insist on it become
/// reachable, and the traffic no longer looks like BitTorrent to equipment that slows BitTorrent down.
/// </summary>
public sealed partial class PeerConnection
{
    static readonly BigInteger DhPrime = BigInteger.Parse(
        "00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563",
        NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    const int DhLength = 96;
    const int MaxPad = 512;
    const byte CryptoPlain = 0x01, CryptoRc4 = 0x02;

    Rc4? _encrypt, _decrypt;
    byte[] _prefix = Array.Empty<byte>();
    int _prefixPosition;

    /// <summary>True when this connection's traffic is RC4-encrypted.</summary>
    public bool Encrypted => _encrypt != null;

    sealed class Rc4
    {
        readonly byte[] _s = new byte[256];
        int _i, _j;
        public Rc4(byte[] key)
        {
            for (var k = 0; k < 256; k++) _s[k] = (byte)k;
            for (int k = 0, j = 0; k < 256; k++) { j = (j + _s[k] + key[k % key.Length]) & 255; (_s[k], _s[j]) = (_s[j], _s[k]); }
            Process(new byte[1024]);   // the first kilobyte of RC4 output is weak: the protocol throws it away
        }
        public void Process(Span<byte> data)
        {
            for (var k = 0; k < data.Length; k++)
            {
                _i = (_i + 1) & 255; _j = (_j + _s[_i]) & 255;
                (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
                data[k] ^= _s[(_s[_i] + _s[_j]) & 255];
            }
        }
    }

    static byte[] Sha1(params byte[][] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var part in parts) sha.AppendData(part);
        return sha.GetHashAndReset();
    }

    static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    static byte[] Fixed(BigInteger value)
    {
        var raw = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == DhLength) return raw;
        var padded = new byte[DhLength];
        raw.AsSpan(Math.Max(0, raw.Length - DhLength)).CopyTo(padded.AsSpan(Math.Max(0, DhLength - raw.Length)));
        return padded;
    }

    static (BigInteger Secret, byte[] Public) NewKeyPair()
    {
        var secret = new BigInteger(RandomNumberGenerator.GetBytes(20), isUnsigned: true, isBigEndian: true);
        return (secret, Fixed(BigInteger.ModPow(2, secret, DhPrime)));
    }

    static byte[] Shared(byte[] theirPublic, BigInteger secret)
    {
        var their = new BigInteger(theirPublic, isUnsigned: true, isBigEndian: true);
        if (their <= BigInteger.One || their >= DhPrime - 1) throw new IOException("The peer sent an invalid encryption key.");
        return Fixed(BigInteger.ModPow(their, secret, DhPrime));
    }

    static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts) { part.CopyTo(all, at); at += part.Length; }
        return all;
    }

    /// <summary>Does the 20-byte block a connecting peer sent identify this torrent? (HASH('req2', infohash) xor HASH('req3', S))</summary>
    public static bool MatchesTorrent(byte[] block, byte[] shared, byte[] infoHash)
    {
        var a = Sha1(Ascii("req2"), infoHash); var b = Sha1(Ascii("req3"), shared);
        var same = 0;
        for (var i = 0; i < 20; i++) same |= block[i] ^ a[i] ^ b[i];
        return same == 0;
    }

    async Task RawReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("The peer closed the connection.");
            read += n;
        }
    }

    /// <summary>Reads until the last bytes received equal <paramref name="marker"/> (the padding before it has no length field).</summary>
    async Task SyncAsync(byte[] marker, int maxSkipped, CancellationToken ct)
    {
        var window = new byte[marker.Length];
        await RawReadAsync(window, 0, window.Length, ct).ConfigureAwait(false);
        var one = new byte[1];
        for (var skipped = 0; !window.AsSpan().SequenceEqual(marker); skipped++)
        {
            if (skipped >= maxSkipped) throw new IOException("The encrypted handshake did not arrive.");
            await RawReadAsync(one, 0, 1, ct).ConfigureAwait(false);
            Buffer.BlockCopy(window, 1, window, 0, window.Length - 1);
            window[^1] = one[0];
        }
    }

    /// <summary>Our side of the handshake when we connect to a peer. Afterwards the ordinary BitTorrent handshake follows on the (now encrypted) stream.</summary>
    public async Task NegotiateOutgoingAsync(byte[] infoHash, bool allowPlaintext, CancellationToken ct)
    {
        var (secret, publicKey) = NewKeyPair();
        await _stream.WriteAsync(Concat(publicKey, RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, MaxPad + 1))), ct).ConfigureAwait(false);
        var theirs = new byte[DhLength];
        await RawReadAsync(theirs, 0, DhLength, ct).ConfigureAwait(false);
        var shared = Shared(theirs, secret);

        var encrypt = new Rc4(Sha1(Ascii("keyA"), shared, infoHash));
        var decrypt = new Rc4(Sha1(Ascii("keyB"), shared, infoHash));
        var block = Sha1(Ascii("req2"), infoHash); var mask = Sha1(Ascii("req3"), shared);
        for (var i = 0; i < 20; i++) block[i] ^= mask[i];
        // VC (8 zero bytes), crypto_provide, len(PadC) = 0, len(IA) = 0 - all encrypted
        var offer = new byte[16];
        offer[11] = (byte)(CryptoRc4 | (allowPlaintext ? CryptoPlain : 0));
        encrypt.Process(offer);
        await _stream.WriteAsync(Concat(Sha1(Ascii("req1"), shared), block, offer), ct).ConfigureAwait(false);

        // the peer answers ENCRYPT(VC, crypto_select, len(PadD), PadD) after up to 512 bytes of padding
        var marker = new byte[8];
        decrypt.Process(marker);
        await SyncAsync(marker, MaxPad, ct).ConfigureAwait(false);
        var answer = new byte[6];
        await RawReadAsync(answer, 0, 6, ct).ConfigureAwait(false);
        decrypt.Process(answer);
        var select = answer[3];
        var padLength = (answer[4] << 8) | answer[5];
        if (padLength > MaxPad) throw new IOException("The encrypted handshake is malformed.");
        if (padLength > 0) { var pad = new byte[padLength]; await RawReadAsync(pad, 0, padLength, ct).ConfigureAwait(false); decrypt.Process(pad); }
        if ((select & CryptoRc4) != 0) { _encrypt = encrypt; _decrypt = decrypt; }
        else if ((select & CryptoPlain) == 0 || !allowPlaintext) throw new IOException("The peer chose an encryption method that was not offered.");
    }

    /// <summary>
    /// The first step for a connection somebody else opened: tells an ordinary BitTorrent handshake from an encrypted one
    /// and, for the latter, completes the key exchange. <paramref name="findTorrent"/> gets the peer's 20-byte torrent
    /// marker and the shared secret and returns the info-hash it belongs to (see <see cref="MatchesTorrent"/>).
    /// </summary>
    public async Task BeginIncomingAsync(EncryptionMode mode, Func<byte[], byte[], byte[]?> findTorrent, CancellationToken ct)
    {
        var first = new byte[20];
        await RawReadAsync(first, 0, 20, ct).ConfigureAwait(false);
        if (first[0] == 19 && first.AsSpan(1, 19).SequenceEqual(Protocol))
        {
            if (mode == EncryptionMode.Require) throw new IOException("Unencrypted peers are not accepted.");
            _prefix = first; _prefixPosition = 0;   // hand the bytes back to the ordinary handshake reader
            return;
        }
        if (mode == EncryptionMode.Off) throw new IOException("Encrypted peers are not accepted.");

        var theirs = new byte[DhLength];
        first.CopyTo(theirs, 0);
        await RawReadAsync(theirs, 20, DhLength - 20, ct).ConfigureAwait(false);
        var (secret, publicKey) = NewKeyPair();
        await _stream.WriteAsync(Concat(publicKey, RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, MaxPad + 1))), ct).ConfigureAwait(false);
        var shared = Shared(theirs, secret);

        await SyncAsync(Sha1(Ascii("req1"), shared), MaxPad, ct).ConfigureAwait(false);
        var block = new byte[20];
        await RawReadAsync(block, 0, 20, ct).ConfigureAwait(false);
        var infoHash = findTorrent(block, shared) ?? throw new IOException("The peer asked for a torrent that is not here.");

        var decrypt = new Rc4(Sha1(Ascii("keyA"), shared, infoHash));
        var encrypt = new Rc4(Sha1(Ascii("keyB"), shared, infoHash));
        var offer = new byte[14];
        await RawReadAsync(offer, 0, 14, ct).ConfigureAwait(false);
        decrypt.Process(offer);
        if (offer.AsSpan(0, 8).IndexOfAnyExcept((byte)0) >= 0) throw new IOException("The encrypted handshake is malformed.");
        var provided = offer[11];
        var padLength = (offer[12] << 8) | offer[13];
        if (padLength > MaxPad) throw new IOException("The encrypted handshake is malformed.");
        var rest = new byte[padLength + 2];
        await RawReadAsync(rest, 0, rest.Length, ct).ConfigureAwait(false);
        decrypt.Process(rest);
        var initialLength = (rest[^2] << 8) | rest[^1];
        var initial = new byte[initialLength];
        if (initialLength > 0) { await RawReadAsync(initial, 0, initialLength, ct).ConfigureAwait(false); decrypt.Process(initial); }

        byte select;
        if ((provided & CryptoRc4) != 0) select = CryptoRc4;
        else if ((provided & CryptoPlain) != 0 && mode != EncryptionMode.Require) select = CryptoPlain;
        else throw new IOException("No acceptable encryption method was offered.");
        var answer = new byte[14];
        answer[11] = select;
        encrypt.Process(answer);
        await _stream.WriteAsync(answer, ct).ConfigureAwait(false);

        _prefix = initial; _prefixPosition = 0;   // the start of the BitTorrent handshake may have come inside the exchange
        if (select == CryptoRc4) { _encrypt = encrypt; _decrypt = decrypt; }
    }
}
