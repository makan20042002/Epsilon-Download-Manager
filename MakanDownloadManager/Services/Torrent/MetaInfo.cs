using System.Security.Cryptography;
using System.Text;

namespace MakanDownloadManager.Services.Torrent;

public sealed class TorrentFile
{
    public int Index { get; init; }
    /// <summary>Path below the torrent's folder, with '/' separators (already checked: no "..", no drive letters).</summary>
    public string Path { get; init; } = "";
    public long Length { get; init; }
    /// <summary>Where this file starts in the torrent's one long byte stream.</summary>
    public long Offset { get; init; }
}

/// <summary>The content description of a torrent (the "info" dictionary) plus its trackers.</summary>
public sealed class MetaInfo
{
    public byte[] InfoHash { get; private init; } = Array.Empty<byte>();
    public byte[] RawInfo { get; private init; } = Array.Empty<byte>();
    public string Name { get; private init; } = "";
    public int PieceLength { get; private init; }
    public byte[] PieceHashes { get; private init; } = Array.Empty<byte>();
    public long TotalLength { get; private init; }
    public bool IsMultiFile { get; private init; }
    public bool IsPrivate { get; private init; }
    public IReadOnlyList<TorrentFile> Files { get; private init; } = Array.Empty<TorrentFile>();
    public IReadOnlyList<IReadOnlyList<string>> TrackerTiers { get; init; } = Array.Empty<IReadOnlyList<string>>();
    /// <summary>HTTP(S) sources from BEP 19 url-list (and the older httpseeds key).</summary>
    public IReadOnlyList<string> WebSeeds { get; init; } = Array.Empty<string>();
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();
    public int PieceCount => PieceHashes.Length / 20;

    public int PieceSize(int piece) => piece == PieceCount - 1 ? (int)(TotalLength - (long)PieceLength * (PieceCount - 1)) : PieceLength;
    public long PieceOffset(int piece) => (long)piece * PieceLength;
    public ReadOnlySpan<byte> PieceHash(int piece) => PieceHashes.AsSpan(piece * 20, 20);

    const int MaxPieces = 4_000_000;
    const int MaxFiles = 100_000;

    /// <summary>Reads a .torrent file.</summary>
    public static MetaInfo Parse(byte[] torrentFile)
    {
        object root;
        (int Start, int Length)? span;
        try { root = Bencode.Parse(torrentFile, out span); }
        catch (FormatException ex) { throw new InvalidDataException("This is not a valid .torrent file: " + ex.Message, ex); }
        if (span is null || root is not Dictionary<string, object> top) throw new InvalidDataException("This .torrent file has no content description (info).");
        var raw = torrentFile.AsSpan(span.Value.Start, span.Value.Length).ToArray();
        return FromInfo(raw, ReadTrackers(top), ReadWebSeeds(top));
    }

    /// <summary>Builds the description from the raw info dictionary (what a magnet link's metadata exchange delivers).</summary>
    public static MetaInfo FromInfo(byte[] rawInfo, IReadOnlyList<IReadOnlyList<string>>? trackers = null, IReadOnlyList<string>? webSeeds = null)
    {
        object parsed;
        try { parsed = Bencode.Parse(rawInfo); }
        catch (FormatException ex) { throw new InvalidDataException("The torrent's content description is damaged: " + ex.Message, ex); }
        if (parsed is not Dictionary<string, object> info) throw new InvalidDataException("The torrent's content description is damaged.");

        var pieceLength = Bencode.Long(info, "piece length") ?? 0;
        var pieces = Bencode.Bytes(info, "pieces");
        if (pieceLength < 1024 || pieceLength > 256 * 1024 * 1024 || pieces is null || pieces.Length == 0 || pieces.Length % 20 != 0 || pieces.Length / 20 > MaxPieces)
            throw new InvalidDataException("The torrent has invalid piece information.");

        var name = SafeName(Bencode.Text(info, "name.utf-8") ?? Bencode.Text(info, "name") ?? "torrent");
        var files = new List<TorrentFile>();
        long offset = 0;
        var multi = Bencode.List(info, "files") is not null;
        if (multi)
        {
            var list = Bencode.List(info, "files")!;
            if (list.Count == 0 || list.Count > MaxFiles) throw new InvalidDataException("The torrent has an invalid file list.");
            foreach (var entry in list)
            {
                var length = Bencode.Long(entry, "length") ?? -1;
                var parts = (Bencode.List(entry, "path.utf-8") ?? Bencode.List(entry, "path"))?.OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)).ToList();
                if (length < 0 || parts is null || parts.Count == 0) throw new InvalidDataException("The torrent has an invalid file entry.");
                files.Add(new TorrentFile { Index = files.Count, Path = SafePath(parts), Length = length, Offset = offset });
                offset += length;
            }
        }
        else
        {
            var length = Bencode.Long(info, "length") ?? -1;
            if (length < 0) throw new InvalidDataException("The torrent has no length.");
            files.Add(new TorrentFile { Index = 0, Path = name, Length = length, Offset = 0 });
            offset = length;
        }
        var pieceCount = pieces.Length / 20;
        if (offset <= 0 || offset > (long)pieceCount * pieceLength || offset <= (long)(pieceCount - 1) * pieceLength)
            throw new InvalidDataException("The torrent's size does not match its pieces.");

        using var sha = SHA1.Create();
        return new MetaInfo
        {
            InfoHash = sha.ComputeHash(rawInfo), RawInfo = rawInfo, Name = name, PieceLength = (int)pieceLength, PieceHashes = pieces,
            TotalLength = offset, IsMultiFile = multi, IsPrivate = (Bencode.Long(info, "private") ?? 0) == 1, Files = files,
            TrackerTiers = trackers ?? Array.Empty<IReadOnlyList<string>>(), WebSeeds = webSeeds ?? Array.Empty<string>()
        };
    }

    static IReadOnlyList<string> ReadWebSeeds(Dictionary<string, object> top)
    {
        var result = new List<string>();
        void Add(string? value)
        {
            if (value != null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") result.Add(value);
        }
        Add(Bencode.Text(top, "url-list"));
        if (Bencode.List(top, "url-list") is { } urls) foreach (var value in urls.OfType<byte[]>()) Add(Encoding.UTF8.GetString(value));
        Add(Bencode.Text(top, "httpseeds"));
        if (Bencode.List(top, "httpseeds") is { } old) foreach (var value in old.OfType<byte[]>()) Add(Encoding.UTF8.GetString(value));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
    }

    static IReadOnlyList<IReadOnlyList<string>> ReadTrackers(Dictionary<string, object> top)
    {
        var tiers = new List<IReadOnlyList<string>>();
        if (Bencode.List(top, "announce-list") is { } lists)
            foreach (var tier in lists.OfType<List<object>>())
            {
                var urls = tier.OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)).Where(IsTrackerUrl).ToList();
                if (urls.Count > 0) tiers.Add(urls);
            }
        if (tiers.Count == 0 && Bencode.Text(top, "announce") is { } single && IsTrackerUrl(single)) tiers.Add(new[] { single });
        return tiers;
    }

    public static bool IsTrackerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "udp");

    /// <summary>A .torrent file for this content (used to keep the metadata that a magnet link delivered).</summary>
    public byte[] ToTorrentFile()
    {
        var parsed = (Dictionary<string, object>)Bencode.Parse(RawInfo);
        var root = new Dictionary<string, object> { ["info"] = parsed };
        if (TrackerTiers.Count > 0) root["announce-list"] = TrackerTiers.Select(t => (object)t.Select(u => (object)u).ToList()).ToList();
        if (WebSeeds.Count == 1) root["url-list"] = WebSeeds[0];
        else if (WebSeeds.Count > 1) root["url-list"] = WebSeeds.Select(u => (object)u).ToList();
        return Bencode.Encode(root);
    }

    // ---------------------------------------------------------------- names: nothing a torrent says may leave its folder

    static string SafeName(string name)
    {
        var clean = new string(name.Select(c => c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return clean.Length == 0 ? "torrent" : clean.Length > 150 ? clean[..150] : clean;
    }

    static string SafePath(IReadOnlyList<string> parts)
    {
        var safe = new List<string>();
        foreach (var part in parts)
        {
            if (part is ".." or "." or "" ) throw new InvalidDataException("The torrent contains an unsafe file path (\"" + part + "\").");
            safe.Add(SafeName(part));
        }
        return string.Join("/", safe);
    }
}

/// <summary>magnet:?xt=urn:btih:&lt;hash&gt;&amp;dn=name&amp;tr=tracker&amp;x.pe=host:port</summary>
public sealed class MagnetLink
{
    public byte[] InfoHash { get; private init; } = Array.Empty<byte>();
    public string? Name { get; private init; }
    public IReadOnlyList<string> Trackers { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> Peers { get; private init; } = Array.Empty<string>();
    public string InfoHashHex => Convert.ToHexString(InfoHash).ToLowerInvariant();

    public static bool IsMagnet(string? text) => text != null && text.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out MagnetLink? link)
    {
        link = null;
        if (!IsMagnet(text)) return false;
        var query = text!.Trim()[(text.Trim().IndexOf('?') + 1)..];
        byte[]? hash = null; string? name = null; var trackers = new List<string>(); var peers = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair[..eq].ToLowerInvariant();
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            switch (key)
            {
                case "xt" when value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase): hash ??= DecodeHash(value[9..]); break;
                case "dn": name = value; break;
                case "tr" when MetaInfo.IsTrackerUrl(value): trackers.Add(value); break;
                case "x.pe": peers.Add(value); break;
            }
        }
        if (hash is null) return false;
        link = new MagnetLink { InfoHash = hash, Name = name, Trackers = trackers.Distinct().ToList(), Peers = peers };
        return true;
    }

    /// <summary>40 hex characters, or 32 base32 characters.</summary>
    static byte[]? DecodeHash(string text)
    {
        if (text.Length == 40 && text.All(Uri.IsHexDigit)) return Convert.FromHexString(text);
        if (text.Length == 32)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bits = 0; var value = 0; var result = new List<byte>();
            foreach (var c in text.ToUpperInvariant())
            {
                var index = alphabet.IndexOf(c);
                if (index < 0) return null;
                value = (value << 5) | index; bits += 5;
                if (bits >= 8) { result.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
            }
            return result.Count == 20 ? result.ToArray() : null;
        }
        return null;
    }

    public static string Build(byte[] infoHash, string? name, IEnumerable<string>? trackers)
    {
        var sb = new StringBuilder("magnet:?xt=urn:btih:" + Convert.ToHexString(infoHash).ToLowerInvariant());
        if (!string.IsNullOrEmpty(name)) sb.Append("&dn=").Append(Uri.EscapeDataString(name));
        foreach (var t in trackers ?? Array.Empty<string>()) sb.Append("&tr=").Append(Uri.EscapeDataString(t));
        return sb.ToString();
    }
}
