using System.Globalization;

namespace MakanDownloadManager.Services;

public sealed record HlsKey(string Method, Uri? KeyUri, byte[]? Iv);
public sealed record HlsMap(Uri Uri, long? RangeOffset, long? RangeLength);
public sealed record HlsSegment(int Index, Uri Uri, double Duration, long? RangeOffset, long? RangeLength, HlsKey? Key, HlsMap? Map, long Sequence);
public sealed record HlsVariant(Uri Uri, long Bandwidth, string? Resolution, string? Codecs, Uri? AudioUri);

/// <summary>Parsed HLS (.m3u8) playlist: either a master playlist (variants) or a media playlist (segments).</summary>
public sealed class HlsPlaylist
{
    public bool IsMaster { get; private init; }
    public bool IsEndList { get; private init; }
    public IReadOnlyList<HlsSegment> Segments { get; private init; } = Array.Empty<HlsSegment>();
    public IReadOnlyList<HlsVariant> Variants { get; private init; } = Array.Empty<HlsVariant>();
    public double TotalDuration => Segments.Sum(s => s.Duration);

    /// <summary>A finished (VOD) playlist built from an already-known segment list (used for DASH).</summary>
    public static HlsPlaylist ForSegments(IReadOnlyList<HlsSegment> segments) => new() { IsEndList = true, Segments = segments };

    public static bool LooksLikePlaylist(string text) => text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

    public static HlsPlaylist Parse(Uri baseUri, string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            ? ParseMaster(baseUri, lines) : ParseMedia(baseUri, lines);
    }

    static HlsPlaylist ParseMaster(Uri baseUri, List<string> lines)
    {
        // Separate audio renditions, preferring the DEFAULT one of each group.
        var audio = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)))
        {
            var a = MediaService.ParseAttrs(line[13..]);
            if (!string.Equals(a.GetValueOrDefault("TYPE"), "AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
            var group = a.GetValueOrDefault("GROUP-ID"); var uri = a.GetValueOrDefault("URI");
            if (group == null || string.IsNullOrWhiteSpace(uri)) continue;
            var isDefault = string.Equals(a.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase);
            if (isDefault || !audio.ContainsKey(group)) audio[group] = new Uri(baseUri, uri);
        }
        var variants = new List<HlsVariant>();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)) continue;
            var a = MediaService.ParseAttrs(lines[i][18..]);
            var j = i + 1; while (j < lines.Count && lines[j].StartsWith('#')) j++;
            if (j >= lines.Count) break;
            long.TryParse(a.GetValueOrDefault("BANDWIDTH"), out var bw);
            var group = a.GetValueOrDefault("AUDIO");
            variants.Add(new HlsVariant(new Uri(baseUri, lines[j]), bw, a.GetValueOrDefault("RESOLUTION"), a.GetValueOrDefault("CODECS"),
                group != null && audio.TryGetValue(group, out var au) ? au : null));
            i = j;
        }
        return new HlsPlaylist { IsMaster = true, Variants = variants };
    }

    static HlsPlaylist ParseMedia(Uri baseUri, List<string> lines)
    {
        var segments = new List<HlsSegment>();
        long sequence = 0; var endList = false;
        HlsKey? key = null; HlsMap? map = null;
        double? duration = null; (long Length, long? Offset)? pendingRange = null;
        long nextOffset = 0; Uri? lastRangeUri = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase)) long.TryParse(line[22..].Trim(), out sequence);
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) endList = true;
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var a = MediaService.ParseAttrs(line[11..]);
                var method = (a.GetValueOrDefault("METHOD") ?? "NONE").ToUpperInvariant();
                key = method == "NONE" ? null
                    : new HlsKey(method, a.TryGetValue("URI", out var ku) && !string.IsNullOrWhiteSpace(ku) ? new Uri(baseUri, ku) : null, ParseIv(a.GetValueOrDefault("IV")));
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var a = MediaService.ParseAttrs(line[11..]);
                if (a.TryGetValue("URI", out var mu))
                {
                    var range = ParseRange(a.GetValueOrDefault("BYTERANGE"));
                    map = new HlsMap(new Uri(baseUri, mu), range?.Offset ?? (range.HasValue ? 0 : null), range?.Length);
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[8..].Split(',')[0].Trim();
                duration = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase)) pendingRange = ParseRange(line[17..]);
            else if (!line.StartsWith('#'))
            {
                var uri = new Uri(baseUri, line);
                long? offset = null, length = null;
                if (pendingRange is { } r)
                {
                    // Without "@offset" the range continues where the previous one on the same resource ended.
                    offset = r.Offset ?? (lastRangeUri == uri ? nextOffset : 0);
                    length = r.Length; nextOffset = offset.Value + r.Length; lastRangeUri = uri;
                }
                segments.Add(new HlsSegment(segments.Count, uri, duration ?? 0, offset, length, key, map, sequence + segments.Count));
                duration = null; pendingRange = null;
            }
        }
        return new HlsPlaylist { IsEndList = endList, Segments = segments };
    }

    static (long Length, long? Offset)? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Trim('"').Split('@');
        if (!long.TryParse(parts[0], out var length)) return null;
        return (length, parts.Length > 1 && long.TryParse(parts[1], out var o) ? o : null);
    }

    static byte[]? ParseIv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length > 32 || hex.Length == 0) return null;
        try { return Convert.FromHexString(hex.PadLeft(32, '0')); } catch (FormatException) { return null; }
    }
}
