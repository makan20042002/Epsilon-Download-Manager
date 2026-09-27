using System.Collections.Concurrent;

namespace MakanDownloadManager.Services.Torrent;

public enum PeerHealthClassification
{
    Excellent,
    Healthy,
    Normal,
    Slow,
    Stalled,
    Unreliable
}

public sealed class BlockRequestTracker
{
    public int PieceIndex { get; }
    public int BlockOffset { get; }
    public int BlockLength { get; }
    public DateTime RequestedAt { get; }

    public BlockRequestTracker(int pieceIndex, int blockOffset, int blockLength)
    {
        PieceIndex = pieceIndex;
        BlockOffset = blockOffset;
        BlockLength = blockLength;
        RequestedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Adaptive request pipeline & stalled peer detector (Phase 3 / Section 7 & 9).
/// Dynamically measures per-peer RTT latency, request fulfillment rate, and throughput
/// to calculate optimal per-peer request pipeline depth with hysteresis.
/// Detects stalled / unproductive requests and triggers timely recycling.
/// </summary>
public sealed class AdaptivePipeline
{
    readonly ConcurrentDictionary<(int Piece, int Offset), BlockRequestTracker> _requests = new();
    readonly Queue<double> _rttSamples = new();
    readonly object _rttLock = new();

    public double SmoothedRttMs { get; private set; } = 150.0; // default 150ms initial estimate
    public int DynamicTargetDepth { get; private set; } = 16;
    public int RequestsFulfilled { get; set; }
    public int RequestsTimedOut { get; set; }
    public int RequestsCanceled { get; set; }
    public int DuplicateRequests { get; set; }
    public DateTime LastSuccessfulBlockTime { get; private set; } = DateTime.UtcNow;
    public PeerHealthClassification Health { get; private set; } = PeerHealthClassification.Normal;

    public int OutstandingCount => _requests.Count;

    public void TrackRequest(int piece, int offset, int length)
    {
        var req = new BlockRequestTracker(piece, offset, length);
        _requests[(piece, offset)] = req;
    }

    public bool CompleteRequest(int piece, int offset, out double rttMs)
    {
        rttMs = 0;
        if (_requests.TryRemove((piece, offset), out var req))
        {
            rttMs = (DateTime.UtcNow - req.RequestedAt).TotalMilliseconds;
            RecordRtt(rttMs);
            RequestsFulfilled++;
            LastSuccessfulBlockTime = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    public bool RemoveRequest(int piece, int offset)
    {
        return _requests.TryRemove((piece, offset), out _);
    }

    public List<BlockRequestTracker> GetStalledRequests(TimeSpan timeoutThreshold)
    {
        var now = DateTime.UtcNow;
        var stalled = new List<BlockRequestTracker>();

        foreach (var req in _requests.Values)
        {
            if (now - req.RequestedAt > timeoutThreshold)
            {
                stalled.Add(req);
            }
        }

        return stalled;
    }

    private void RecordRtt(double rttMs)
    {
        lock (_rttLock)
        {
            _rttSamples.Enqueue(rttMs);
            if (_rttSamples.Count > 20) _rttSamples.Dequeue();

            // Exponential moving average: 80% previous + 20% sample
            SmoothedRttMs = 0.8 * SmoothedRttMs + 0.2 * rttMs;
        }
    }

    /// <summary>
    /// Calculates the optimal request depth for this peer based on throughput, RTT, and health.
    /// Formula: TargetDepth = Clamp( (Throughput * (RTT + 100ms)) / BlockSize, Min, Max )
    /// </summary>
    public int CalculateOptimalDepth(long currentDownloadBytesPerSec, int minDepth = 8, int maxDepth = 128)
    {
        if (currentDownloadBytesPerSec <= 0)
        {
            DynamicTargetDepth = minDepth;
            UpdateHealthCategory(0);
            return minDepth;
        }

        // Delay in seconds including target RTT
        var effectiveRttSec = (SmoothedRttMs + 80.0) / 1000.0;
        var estimatedBytesInFlight = currentDownloadBytesPerSec * effectiveRttSec;
        var calculatedDepth = (int)Math.Ceiling(estimatedBytesInFlight / 16384.0);

        // Apply hysteresis to prevent rapid depth jittering
        var newDepth = Math.Clamp(calculatedDepth, minDepth, maxDepth);
        DynamicTargetDepth = (int)Math.Round(0.7 * DynamicTargetDepth + 0.3 * newDepth);

        UpdateHealthCategory(currentDownloadBytesPerSec);
        return DynamicTargetDepth;
    }

    private void UpdateHealthCategory(long bytesPerSec)
    {
        var secondsSinceLastBlock = (DateTime.UtcNow - LastSuccessfulBlockTime).TotalSeconds;

        if (secondsSinceLastBlock > 30 && _requests.Count > 0)
        {
            Health = PeerHealthClassification.Stalled;
        }
        else if (RequestsTimedOut > 5 && RequestsFulfilled < 2)
        {
            Health = PeerHealthClassification.Unreliable;
        }
        else if (bytesPerSec > 1_000_000 && SmoothedRttMs < 100)
        {
            Health = PeerHealthClassification.Excellent;
        }
        else if (bytesPerSec > 250_000)
        {
            Health = PeerHealthClassification.Healthy;
        }
        else if (bytesPerSec < 20_000)
        {
            Health = PeerHealthClassification.Slow;
        }
        else
        {
            Health = PeerHealthClassification.Normal;
        }
    }
}
