namespace MakanDownloadManager.Services.Torrent;

// Read-only snapshots for the torrent window.
public sealed partial class TorrentSession
{
    public IReadOnlyList<PeerInfo> Peers()
    {
        lock (_sync)
            return _peers.Select(p => new PeerInfo(p.EndPoint.ToString(), p.Client.Length > 0 ? p.Client : "?", p.Flags, p.Progress, p.Down.BytesPerSecond, p.Up.BytesPerSecond, p.Down.Total, p.Up.Total, p.Connection.Incoming, p.Connection.Encrypted))
                         .OrderByDescending(p => p.DownRate + p.UpRate).ThenBy(p => p.Address).ToList();
    }

    public IReadOnlyList<TrackerInfo> Trackers()
    {
        lock (_sync) return _trackers.Select(t => new TrackerInfo(t.Url, t.Status, t.Seeders, t.Leechers, t.Last, t.Tier)).ToList();
    }

    public IReadOnlyList<TorrentFileInfo> Files()
    {
        lock (_sync)
        {
            if (_meta == null) return Array.Empty<TorrentFileInfo>();
            return _meta.Files.Select(f => new TorrentFileInfo(f.Index, f.Path, f.Length, FileDone(f), _priorities[f.Index])).ToList();
        }
    }

    /// <summary>Verified bytes of one file. Called with the lock held.</summary>
    long FileDone(TorrentFile f)
    {
        if (f.Length == 0) return 0;
        var meta = _meta!;
        long done = 0;
        var first = (int)(f.Offset / meta.PieceLength); var last = (int)((f.Offset + f.Length - 1) / meta.PieceLength);
        for (var p = first; p <= last; p++)
        {
            if (!_have[p]) continue;
            var start = Math.Max(f.Offset, meta.PieceOffset(p)); var end = Math.Min(f.Offset + f.Length, meta.PieceOffset(p) + meta.PieceSize(p));
            done += Math.Max(0, end - start);
        }
        return done;
    }

    /// <summary>How much of each part of the torrent is verified (0..1 per bucket): the pieces bar.</summary>
    public double[] PieceMap(int buckets)
    {
        var map = new double[Math.Max(1, buckets)];
        lock (_sync)
        {
            if (_meta == null || _have.Length == 0) return map;
            var n = _have.Length;
            for (var b = 0; b < map.Length; b++)
            {
                int from = (int)((long)n * b / map.Length), to = Math.Max(from + 1, (int)((long)n * (b + 1) / map.Length));
                var have = 0;
                for (var p = from; p < Math.Min(to, n); p++) if (_have[p]) have++;
                map[b] = have / (double)(Math.Min(to, n) - from);
            }
        }
        return map;
    }

    public int PieceCount => _meta?.PieceCount ?? 0;
    public int PiecesHave { get { lock (_sync) return _have.Count; } }
    public string MagnetUri => MagnetLink.Build(_hash, Name, _trackers.Select(t => t.Url));
}
