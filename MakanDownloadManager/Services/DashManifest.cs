using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MakanDownloadManager.Services;

public sealed record DashRepresentation(string Id, string Kind, long Bandwidth, int? Width, int? Height, string? Codecs, string? Lang, string MimeType, bool Encrypted);

/// <summary>
/// MPEG-DASH (.mpd) manifest for on-demand streams: lists the video / audio representations and turns one of them into a segment list
/// (initialization segment + media segments) that the HLS downloader can fetch. Supports SegmentTemplate (number or time based, with or
/// without SegmentTimeline) and SegmentList. Live streams, DRM and single-file (SegmentBase) layouts are reported as not supported.
/// </summary>
public sealed class DashManifest
{
    const string SelectionPrefix = "#makan-v=";

    public bool IsDynamic { get; private init; }
    /// <summary>Length of the main period in seconds, when the manifest states it.</summary>
    public double? DurationSeconds { get; private init; }
    public IReadOnlyList<DashRepresentation> Representations { get; private init; } = Array.Empty<DashRepresentation>();
    readonly Dictionary<string, Func<List<HlsSegment>>> _segments = new();

    public static bool LooksLikeMpd(string text) => text.Contains("<MPD", StringComparison.Ordinal);

    public DashRepresentation? BestVideo() => Representations.Where(r => r.Kind == "video" && !r.Encrypted).OrderByDescending(r => r.Bandwidth).FirstOrDefault();
    public DashRepresentation? BestAudio() => Representations.Where(r => r.Kind == "audio" && !r.Encrypted).OrderByDescending(r => r.Bandwidth).FirstOrDefault();

    /// <summary>manifest url + which representations to download; the fragment is never sent to the server.</summary>
    public static string WithSelection(Uri manifest, string? videoId, string? audioId)
    {
        var url = new UriBuilder(manifest) { Fragment = "" }.Uri.ToString();
        return url + SelectionPrefix + Uri.EscapeDataString(videoId ?? "") + (audioId != null ? "&makan-a=" + Uri.EscapeDataString(audioId) : "");
    }

    public static (string? Video, string? Audio) ParseSelection(string fragment)
    {
        if (!fragment.StartsWith(SelectionPrefix, StringComparison.Ordinal)) return (null, null);
        string? v = null, a = null;
        foreach (var part in fragment[1..].Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "makan-v") v = Uri.UnescapeDataString(kv[1]);
            else if (kv[0] == "makan-a") a = Uri.UnescapeDataString(kv[1]);
        }
        return (string.IsNullOrEmpty(v) ? null : v, string.IsNullOrEmpty(a) ? null : a);
    }

    public HlsPlaylist BuildTrack(string representationId)
    {
        var rep = Representations.FirstOrDefault(r => r.Id == representationId) ?? throw new InvalidDataException($"The stream has no representation '{representationId}'.");
        if (rep.Encrypted) throw new NotSupportedException("This stream is protected with DRM (ContentProtection) and can't be downloaded.");
        return HlsPlaylist.ForSegments(_segments[representationId]());
    }

    public static DashManifest Parse(Uri baseUri, string text)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); } catch (System.Xml.XmlException ex) { throw new InvalidDataException("The manifest is not valid XML: " + ex.Message); }
        var mpd = doc.Root ?? throw new InvalidDataException("Empty manifest.");
        var ns = mpd.Name.Namespace;
        var dynamic = string.Equals((string?)mpd.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase);
        var total = ParseDuration((string?)mpd.Attribute("mediaPresentationDuration"));
        var mpdBase = ResolveBase(baseUri, mpd, ns);

        // With several periods (ads, chapters) take the longest one: that's the main content.
        var periods = mpd.Elements(ns + "Period").ToList();
        if (periods.Count == 0) throw new InvalidDataException("The manifest has no Period.");
        XElement period = periods[0]; double? periodDuration = periods.Count == 1 ? (ParseDuration((string?)period.Attribute("duration")) ?? total) : ParseDuration((string?)period.Attribute("duration"));
        if (periods.Count > 1)
        {
            var best = periods.Select(p => (P: p, D: ParseDuration((string?)p.Attribute("duration")))).OrderByDescending(x => x.D ?? 0).First();
            period = best.P; periodDuration = best.D ?? total;
        }
        var periodBase = ResolveBase(mpdBase, period, ns);

        var manifest = new DashManifest { IsDynamic = dynamic };
        var reps = new List<DashRepresentation>();
        foreach (var aset in period.Elements(ns + "AdaptationSet"))
        {
            var asetBase = ResolveBase(periodBase, aset, ns);
            foreach (var rep in aset.Elements(ns + "Representation"))
            {
                var id = (string?)rep.Attribute("id") ?? $"rep{reps.Count}";
                var mime = (string?)rep.Attribute("mimeType") ?? (string?)aset.Attribute("mimeType") ?? "";
                var kind = ((string?)aset.Attribute("contentType") ?? mime.Split('/')[0]).ToLowerInvariant();
                var codecs = (string?)rep.Attribute("codecs") ?? (string?)aset.Attribute("codecs");
                if (kind is not ("video" or "audio")) kind = codecs != null && Regex.IsMatch(codecs, "^(avc|hvc|hev|vp0|vp9|av01)") ? "video" : codecs != null && Regex.IsMatch(codecs, "^(mp4a|opus|ac-3|ec-3|flac)") ? "audio" : kind;
                if (kind is not ("video" or "audio")) continue;   // subtitles etc.
                if (reps.Any(r => r.Id == id)) continue;
                long.TryParse((string?)rep.Attribute("bandwidth"), out var bandwidth);
                int? width = IntAttr(rep, "width") ?? IntAttr(aset, "width"), height = IntAttr(rep, "height") ?? IntAttr(aset, "height");
                var encrypted = rep.Elements(ns + "ContentProtection").Any() || aset.Elements(ns + "ContentProtection").Any();
                reps.Add(new DashRepresentation(id, kind, bandwidth, width, height, codecs, (string?)aset.Attribute("lang"), mime, encrypted));

                var repBase = ResolveBase(asetBase, rep, ns);
                manifest._segments[id] = () => BuildSegments(repBase, ns, period, aset, rep, id, bandwidth, periodDuration);
            }
        }
        return new DashManifest { IsDynamic = dynamic, DurationSeconds = periodDuration ?? total, Representations = reps }.CopySegmentsFrom(manifest);
    }

    DashManifest CopySegmentsFrom(DashManifest other) { foreach (var kv in other._segments) _segments[kv.Key] = kv.Value; return this; }

    // ---------------------------------------------------------------- segment lists

    static List<HlsSegment> BuildSegments(Uri repBase, XNamespace ns, XElement period, XElement aset, XElement rep, string repId, long bandwidth, double? periodDuration)
    {
        var template = Nearest(ns + "SegmentTemplate", rep, aset, period);
        if (template != null) return FromTemplate(repBase, ns, template, repId, bandwidth, periodDuration);
        var list = Nearest(ns + "SegmentList", rep, aset, period);
        if (list != null) return FromList(repBase, ns, list);
        throw new NotSupportedException("This DASH stream uses a single-file layout (SegmentBase), which isn't supported yet.");
    }

    static XElement? Nearest(XName name, params XElement[] chain) => chain.Select(e => e.Element(name)).FirstOrDefault(e => e != null);

    static List<HlsSegment> FromTemplate(Uri repBase, XNamespace ns, XElement t, string repId, long bandwidth, double? periodDuration)
    {
        var media = (string?)t.Attribute("media") ?? throw new InvalidDataException("SegmentTemplate has no media pattern.");
        var init = (string?)t.Attribute("initialization");
        var timescale = LongAttr(t, "timescale") ?? 1; if (timescale <= 0) timescale = 1;
        var startNumber = LongAttr(t, "startNumber") ?? 1;
        var map = init != null ? new HlsMap(new Uri(repBase, Fill(init, repId, bandwidth, null, null)), null, null) : null;
        var segments = new List<HlsSegment>();

        void Add(long number, long time, double seconds)
        {
            if (segments.Count > 200_000) throw new NotSupportedException("The stream has an unreasonable number of segments.");
            segments.Add(new HlsSegment(segments.Count, new Uri(repBase, Fill(media, repId, bandwidth, number, time)), seconds, null, null, null, map, number));
        }

        var timeline = t.Element(ns + "SegmentTimeline");
        if (timeline != null)
        {
            long number = startNumber, next = 0;
            foreach (var s in timeline.Elements(ns + "S"))
            {
                var time = LongAttr(s, "t") ?? next;
                var d = LongAttr(s, "d") ?? throw new InvalidDataException("A timeline entry has no duration.");
                var r = LongAttr(s, "r") ?? 0;
                if (d <= 0) throw new InvalidDataException("A timeline entry has no duration.");
                if (r < 0)
                {
                    if (periodDuration == null) throw new NotSupportedException("The stream's length is not stated in the manifest.");
                    r = Math.Max(0, ((long)(periodDuration.Value * timescale) - time + d - 1) / d - 1);
                }
                for (long k = 0; k <= r; k++) Add(number++, time + k * d, d / (double)timescale);
                next = time + (r + 1) * d;
            }
        }
        else
        {
            var duration = LongAttr(t, "duration");
            if (duration is not > 0 || periodDuration == null) throw new NotSupportedException("Can't tell how many segments the stream has (no timeline, duration or length).");
            var count = (long)Math.Ceiling(periodDuration.Value * timescale / duration.Value);
            for (long n = 0; n < count; n++) Add(startNumber + n, n * duration.Value, duration.Value / (double)timescale);
        }
        return segments;
    }

    static List<HlsSegment> FromList(Uri repBase, XNamespace ns, XElement list)
    {
        var timescale = LongAttr(list, "timescale") ?? 1; if (timescale <= 0) timescale = 1;
        var duration = LongAttr(list, "duration") ?? 0;
        HlsMap? map = null;
        if (list.Element(ns + "Initialization") is { } init)
        {
            var range = ParseRange((string?)init.Attribute("range"));
            map = new HlsMap(SegmentUri(repBase, (string?)init.Attribute("sourceURL")), range?.Offset, range?.Length);
        }
        var segments = new List<HlsSegment>();
        foreach (var s in list.Elements(ns + "SegmentURL"))
        {
            var range = ParseRange((string?)s.Attribute("mediaRange"));
            segments.Add(new HlsSegment(segments.Count, SegmentUri(repBase, (string?)s.Attribute("media")), duration / (double)timescale, range?.Offset, range?.Length, null, map, segments.Count));
        }
        return segments;
    }

    static Uri SegmentUri(Uri repBase, string? relative) => string.IsNullOrWhiteSpace(relative) ? repBase : new Uri(repBase, relative);

    // ---------------------------------------------------------------- helpers

    /// <summary>$RepresentationID$, $Number$, $Time$, $Bandwidth$ (with optional %0Nd padding) and $$.</summary>
    static string Fill(string template, string repId, long bandwidth, long? number, long? time) =>
        Regex.Replace(template, @"\$(\w*)(?:%0(\d+)d)?\$", m =>
        {
            var name = m.Groups[1].Value;
            if (name == "") return "$";
            if (name == "RepresentationID") return repId;
            long? value = name switch { "Number" => number, "Time" => time, "Bandwidth" => bandwidth, _ => null };
            if (value == null) return m.Value;
            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return value.Value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
        });

    static Uri ResolveBase(Uri parent, XElement element, XNamespace ns)
    {
        var value = element.Element(ns + "BaseURL")?.Value.Trim();
        return string.IsNullOrEmpty(value) ? parent : new Uri(parent, value);
    }

    static long? LongAttr(XElement e, string name) => long.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    static int? IntAttr(XElement e, string name) => int.TryParse((string?)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    static (long Offset, long Length)? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('-');
        return parts.Length == 2 && long.TryParse(parts[0], out var a) && long.TryParse(parts[1], out var b) && b >= a ? (a, b - a + 1) : null;
    }

    /// <summary>ISO-8601 duration such as PT1H2M3.5S.</summary>
    static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = Regex.Match(value, @"^P(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$", RegexOptions.CultureInvariant);
        if (!m.Success) return null;
        double G(int i) => m.Groups[i].Success ? double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        return G(1) * 86400 + G(2) * 3600 + G(3) * 60 + G(4);
    }
}
