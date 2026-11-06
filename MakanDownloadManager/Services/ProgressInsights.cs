using System.Globalization;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>
/// One speed sample per second (download and upload) for up to 30 minutes, for the progress window's graph.
/// The graph can show the last 1, 5 or 30 minutes; every range is reduced to the same number of points.
/// Pure logic, no UI: the window only draws what <see cref="Series"/> returns.
/// </summary>
public sealed class SpeedHistory
{
    /// <summary>The ranges offered by the graph: 1 min, 5 min, 30 min.</summary>
    public static readonly int[] RangeSeconds = { 60, 300, 1800 };

    sealed class Second { public long Stamp; public double Down, Up; public int Count; }

    readonly object _gate = new();
    readonly List<Second> _samples = new();
    readonly int _capacity;

    public SpeedHistory(int capacitySeconds = 1800) => _capacity = Math.Max(60, capacitySeconds);

    static long StampOf(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Records a sample. Several samples inside the same second are averaged.</summary>
    public void Add(DateTime utcNow, long downloadBytesPerSecond, long uploadBytesPerSecond = 0)
    {
        var stamp = StampOf(utcNow);
        var down = Math.Max(0, downloadBytesPerSecond);
        var up = Math.Max(0, uploadBytesPerSecond);
        lock (_gate)
        {
            if (_samples.Count > 0 && _samples[^1].Stamp == stamp)
            {
                var last = _samples[^1];
                last.Down = (last.Down * last.Count + down) / (last.Count + 1);
                last.Up = (last.Up * last.Count + up) / (last.Count + 1);
                last.Count++;
            }
            else if (_samples.Count == 0 || _samples[^1].Stamp < stamp)
            {
                _samples.Add(new Second { Stamp = stamp, Down = down, Up = up, Count = 1 });
            }
            else return;                                   // a sample from the past (clock change): ignored
            var oldest = stamp - _capacity;
            var drop = 0;
            while (drop < _samples.Count && _samples[drop].Stamp <= oldest) drop++;
            if (drop > 0) _samples.RemoveRange(0, drop);
        }
    }

    public int SampleCount { get { lock (_gate) return _samples.Count; } }

    /// <summary>
    /// The last <paramref name="rangeSeconds"/> seconds as <paramref name="points"/> averages (oldest first, bytes per second).
    /// A second without a sample counts as 0.
    /// </summary>
    public double[] Series(int rangeSeconds, int points, DateTime utcNow, bool upload = false)
    {
        rangeSeconds = Math.Clamp(rangeSeconds, 1, 24 * 3600);
        points = Math.Clamp(points, 1, rangeSeconds);
        var perSecond = new double[rangeSeconds];
        var end = StampOf(utcNow);                          // newest second shown
        var start = end - rangeSeconds + 1;
        lock (_gate)
        {
            foreach (var s in _samples)
            {
                if (s.Stamp < start || s.Stamp > end) continue;
                perSecond[s.Stamp - start] = upload ? s.Up : s.Down;
            }
        }
        var result = new double[points];
        for (var p = 0; p < points; p++)
        {
            var from = (int)((long)p * rangeSeconds / points);
            var to = Math.Max(from + 1, (int)((long)(p + 1) * rangeSeconds / points));
            double sum = 0;
            for (var i = from; i < to && i < rangeSeconds; i++) sum += perSecond[i];
            result[p] = sum / (to - from);
        }
        return result;
    }

    /// <summary>The fastest second inside the range.</summary>
    public double Peak(int rangeSeconds, DateTime utcNow, bool upload = false) => Series(rangeSeconds, Math.Min(rangeSeconds, 600), utcNow, upload).DefaultIfEmpty(0).Max();

    /// <summary>A "nice" top for the graph's vertical axis (1, 2, 5 x 10^n) that is at least <paramref name="peak"/>; never below 1 MB/s.</summary>
    public static double AxisMaximum(double peak)
    {
        var floor = 1024d * 1024d;
        if (peak <= floor) return floor;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        foreach (var step in new[] { 1d, 2d, 5d, 10d })
            if (step * magnitude >= peak) return step * magnitude;
        return 10 * magnitude;
    }
}

/// <summary>How the connections of one download are spread over the networks (Multi-Network): who carried how much.</summary>
public sealed record NetworkShare(string Network, int Connections, long Bytes, double Percent);

public static class NetworkBreakdown
{
    public const string DefaultRoute = "Windows route";

    /// <summary>Groups connections by network, largest first. Percentages follow the bytes each network carried
    /// (or the number of connections when nothing has been fetched yet) and add up to 100.</summary>
    public static IReadOnlyList<NetworkShare> FromConnections(IEnumerable<ConnectionInfo> rows)
    {
        var groups = rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Network) ? DefaultRoute : r.Network.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Count(), Bytes: g.Sum(r => Math.Max(0, r.Downloaded))))
            .ToList();
        if (groups.Count == 0) return Array.Empty<NetworkShare>();
        var totalBytes = groups.Sum(g => g.Bytes);
        var totalCount = groups.Sum(g => g.Count);
        return groups
            .Select(g => new NetworkShare(g.Name, g.Count, g.Bytes,
                totalBytes > 0 ? g.Bytes * 100d / totalBytes : g.Count * 100d / totalCount))
            .OrderByDescending(s => s.Bytes).ThenByDescending(s => s.Connections).ThenBy(s => s.Network, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when more than one real network carries this download.</summary>
    public static bool IsMultiNetwork(IReadOnlyList<NetworkShare> shares) => shares.Count > 1;
}

public enum SegmentState { Unassigned, Waiting, Downloading, Complete, Error }

public readonly record struct SegmentCounts(int Downloading, int Complete, int Waiting, int Error, int Unassigned)
{
    public int Total => Downloading + Complete + Waiting + Error + Unassigned;
}

/// <summary>Turns the position map (0..1 per part of the file) into the five states of the segment-map legend.</summary>
public static class SegmentMap
{
    public static SegmentState[] Classify(IReadOnlyList<double> fractions, string status, int activeConnections)
    {
        var result = new SegmentState[fractions.Count];
        var running = status == nameof(DownloadStatus.Downloading);
        var queued = status == nameof(DownloadStatus.Queued);
        for (var i = 0; i < result.Length; i++)
        {
            var f = double.IsNaN(fractions[i]) ? 0 : Math.Clamp(fractions[i], 0, 1);
            if (status == nameof(DownloadStatus.Complete) || f >= 0.999) result[i] = SegmentState.Complete;
            else if (status == nameof(DownloadStatus.Failed)) result[i] = SegmentState.Error;
            else if (f > 0.001) result[i] = running ? SegmentState.Downloading : SegmentState.Waiting;
            else result[i] = (running && activeConnections > 0) || queued ? SegmentState.Waiting : SegmentState.Unassigned;
        }
        return result;
    }

    public static SegmentCounts Count(IEnumerable<SegmentState> states)
    {
        int d = 0, c = 0, w = 0, e = 0, u = 0;
        foreach (var s in states)
            switch (s)
            {
                case SegmentState.Downloading: d++; break;
                case SegmentState.Complete: c++; break;
                case SegmentState.Waiting: w++; break;
                case SegmentState.Error: e++; break;
                default: u++; break;
            }
        return new SegmentCounts(d, c, w, e, u);
    }
}

/// <summary>The "Logs" tab: a short list of what happened to this download while the window was open.</summary>
public sealed class ProgressLog
{
    readonly List<string> _lines = new();
    readonly int _max;
    string? _status, _error;
    int _connections = -1, _retry;

    public ProgressLog(int maxLines = 300) => _max = Math.Max(10, maxLines);

    public IReadOnlyList<string> Lines => _lines;

    public void Add(DateTime localTime, string message)
    {
        _lines.Add(localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message);
        if (_lines.Count > _max) _lines.RemoveRange(0, _lines.Count - _max);
    }

    /// <summary>Call on every refresh. Adds a line only when something changed and returns the new lines.</summary>
    public IReadOnlyList<string> Observe(DateTime localTime, string status, string? lastError, int activeConnections, int retryCount)
    {
        var added = new List<string>();
        void Log(string text) { Add(localTime, text); added.Add(_lines[^1]); }

        if (_status != status) { Log("Status: " + status); _status = status; }
        if (activeConnections != _connections && (activeConnections > 0 || _connections > 0))
        {
            Log(_connections < 0 ? $"Connections: {activeConnections}" : $"Connections: {_connections} → {activeConnections}");
            _connections = activeConnections;
        }
        else if (_connections < 0) _connections = activeConnections;
        if (retryCount > _retry) { Log($"Retry {retryCount}"); }
        _retry = retryCount;
        var error = string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim();
        if (error != null && !string.Equals(error, _error, StringComparison.Ordinal)) Log(error);
        _error = error;
        return added;
    }
}

/// <summary>The "SHA-256 verification" line of the progress window's footer.</summary>
public static class VerificationText
{
    public static string For(DownloadItem item)
    {
        var expected = (item.ExpectedSha256 ?? "").Replace(" ", "", StringComparison.Ordinal).Trim();
        if (expected.Length == 0) return "—";
        var shown = expected.Length > 16 ? expected[..8] + "…" + expected[^6..] : expected;
        return item.Status == nameof(DownloadStatus.Complete) ? "checked against " + shown : "checked when finished (expected " + shown + ")";
    }
}
