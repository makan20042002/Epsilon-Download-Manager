using System.Collections.Concurrent;
using System.Net;

namespace MakanDownloadManager.Services.Torrent;

public enum PeerSource
{
    Tracker,
    Dht,
    Pex,
    Incoming,
    Manual
}

public sealed class CandidateInfo
{
    public IPEndPoint EndPoint { get; }
    public HashSet<PeerSource> Sources { get; } = new();
    public DateTime FirstDiscovered { get; } = DateTime.UtcNow;
    public DateTime LastAttempt { get; set; } = DateTime.MinValue;
    public int FailureCount { get; set; }
    public int SuccessCount { get; set; }
    public double QualityScore { get; set; } = 50.0;
    public bool Banned { get; set; }

    public CandidateInfo(IPEndPoint endPoint, PeerSource source)
    {
        EndPoint = endPoint;
        Sources.Add(source);
    }

    public TimeSpan NextRetryDelay()
    {
        if (FailureCount == 0) return TimeSpan.Zero;
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(FailureCount, 6)) * 5); // 5s, 10s, 20s, 40s, 80s, 160s... max 300s
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// Central peer candidate manager (Phase 4 / Engine 2.0).
/// Unifies peer endpoints from trackers, DHT, PEX, incoming connections, and manual/magnet hints.
/// Provides deduplication, failure backoff tracking, peer quality scoring, and candidate queueing.
/// </summary>
public sealed class PeerCandidateManager
{
    readonly ConcurrentDictionary<string, CandidateInfo> _candidates = new();
    readonly object _lock = new();

    public int Count => _candidates.Count;

    public CandidateInfo AddOrUpdate(IPEndPoint ep, PeerSource source)
    {
        if (ep.Port == 0) return null!;
        var key = ep.ToString();

        lock (_lock)
        {
            if (_candidates.TryGetValue(key, out var existing))
            {
                existing.Sources.Add(source);
                return existing;
            }

            var info = new CandidateInfo(ep, source);
            _candidates[key] = info;
            return info;
        }
    }

    public bool IsBanned(IPEndPoint ep) => _candidates.TryGetValue(ep.ToString(), out var info) && info.Banned;

    public void MarkBanned(IPEndPoint ep)
    {
        if (_candidates.TryGetValue(ep.ToString(), out var info))
        {
            info.Banned = true;
        }
        else
        {
            var info2 = new CandidateInfo(ep, PeerSource.Manual) { Banned = true };
            _candidates[ep.ToString()] = info2;
        }
    }

    public void RecordAttempt(IPEndPoint ep, bool success)
    {
        if (_candidates.TryGetValue(ep.ToString(), out var info))
        {
            info.LastAttempt = DateTime.UtcNow;
            if (success)
            {
                info.SuccessCount++;
                info.FailureCount = 0;
                info.QualityScore = Math.Min(100.0, info.QualityScore + 10.0);
            }
            else
            {
                info.FailureCount++;
                info.QualityScore = Math.Max(0.0, info.QualityScore - 15.0);
            }
        }
    }

    public List<CandidateInfo> GetConnectableCandidates(int maxToTake, HashSet<string> connectedKeys, HashSet<string> connectingKeys)
    {
        var now = DateTime.UtcNow;
        var result = new List<CandidateInfo>();

        lock (_lock)
        {
            var ordered = _candidates.Values
                .Where(c => !c.Banned)
                .Where(c => !connectedKeys.Contains(c.EndPoint.ToString()) && !connectingKeys.Contains(c.EndPoint.ToString()))
                .Where(c => c.LastAttempt == DateTime.MinValue || now - c.LastAttempt >= c.NextRetryDelay())
                .OrderByDescending(c => c.QualityScore)
                .ThenBy(c => c.LastAttempt);

            foreach (var candidate in ordered)
            {
                result.Add(candidate);
                if (result.Count >= maxToTake) break;
            }
        }

        return result;
    }
}
