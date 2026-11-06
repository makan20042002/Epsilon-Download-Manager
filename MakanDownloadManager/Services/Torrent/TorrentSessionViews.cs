namespace MakanDownloadManager.Services.Torrent;

// Read-only snapshots for the torrent window.
public sealed partial class TorrentSession
{
    public IReadOnlyList<PeerInfo> Peers()
    {
        lock (_sync)
            return _peers.Select(p => new PeerInfo(p.EndPoint.ToString(), p.Client.Length > 0 ? p.Client : "?", p.Flags, p.Progress, p.Down.BytesPerSecond, p.Up.BytesPerSecond, p.Down.Total, p.Up.Total, p.Connection.Incoming, p.Connection.Encrypted, p.Connection.Transport))
                         .OrderByDescending(p => p.DownRate + p.UpRate).ThenBy(p => p.Address).ToList();
    }

    /// <summary>Seeders the trackers say the whole swarm has (the largest figure any tracker reported; 0 if none answered yet).</summary>
    public int SwarmSeeders { get { lock (_sync) return _trackers.Select(t => t.Seeders ?? 0).DefaultIfEmpty(0).Max(); } }
    /// <summary>Leechers the trackers say the swarm has.</summary>
    public int SwarmLeechers { get { lock (_sync) return _trackers.Select(t => t.Leechers ?? 0).DefaultIfEmpty(0).Max(); } }

    /// <summary>How many outgoing peer connections were tried, how many got as far as a TCP/uTP connection, and the last failure (for "why no peers?").</summary>
    public (int Tried, int Reached, string LastError) ConnectStats => (Volatile.Read(ref _connectAttempts), Volatile.Read(ref _connectReached), _lastConnectError);

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

    /// <summary>Returns the validated on-disk path for one file in this torrent.</summary>
    public string? FilePath(int fileIndex)
    {
        lock (_sync)
        {
            if (_meta == null || _storage == null || (uint)fileIndex >= (uint)_meta.Files.Count) return null;
            return _storage.FilePath(_meta.Files[fileIndex]);
        }
    }

    /// <summary>
    /// Number of verified, gap-free bytes starting at the beginning of a torrent file. Unlike the file's allocated
    /// length, this is safe to copy for media preview while later pieces may still be missing.
    /// </summary>
    public long SafeFilePrefixBytes(int fileIndex)
    {
        lock (_sync)
        {
            if (_meta == null || (uint)fileIndex >= (uint)_meta.Files.Count) return 0;
            var file = _meta.Files[fileIndex];
            return PieceMath.VerifiedPrefix(file.Offset, file.Length, _meta.PieceLength, _meta.TotalLength, p => _have[p]);
        }
    }

    /// <summary>Verified bytes of one file. Called with the lock held.</summary>
    long FileDone(TorrentFile f) => PieceMath.VerifiedBytes(f.Offset, f.Length, _meta!.PieceLength, _meta.TotalLength, p => _have[p]);

    /// <summary>How much of each part of the torrent is verified (0..1 per bucket): the pieces bar.</summary>
    public double[] PieceMap(int buckets)
    {
        lock (_sync)
        {
            if (_meta == null || _have.Length == 0) return new double[Math.Max(1, buckets)];
            return PieceMath.BucketMap(_have.Length, buckets, p => _have[p]);
        }
    }

    public int PieceCount => _meta?.PieceCount ?? 0;
    public bool HasPiece(int index) { lock (_sync) return _meta != null && (uint)index < (uint)_meta.PieceCount && _have[index]; }
    public int PiecesHave { get { lock (_sync) return _have.Count; } }
    public int PieceLength => _meta?.PieceLength ?? 0;
    public int TrackerCount { get { lock (_sync) return _trackers.Count; } }
    public int WebSeedCount => _meta?.WebSeeds.Count ?? 0;
    public string MagnetUri => MagnetLink.Build(_hash, Name, _trackers.Select(t => t.Url));
}
