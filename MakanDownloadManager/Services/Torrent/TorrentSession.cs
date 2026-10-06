using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MakanDownloadManager.Services.Torrent;

public enum TorrentState { Stopped, Metadata, Checking, Downloading, Seeding, Paused, Error, Finished }
public enum FilePriority { Skip = 0, Normal = 1, High = 2 }

public sealed record PeerInfo(string Address, string Client, string Flags, double Progress, long DownRate, long UpRate, long Downloaded, long Uploaded, bool Incoming, bool Encrypted = false);
public sealed record TrackerInfo(string Url, string Status, int? Seeders, int? Leechers, DateTime? LastAnnounce, int Tier);
public sealed record TorrentFileInfo(int Index, string Path, long Length, long Done, FilePriority Priority);

/// <summary>The saved position of a torrent (which pieces are verified, how much was transferred, which files are wanted).</summary>
public sealed class TorrentResume
{
    public string Bitfield { get; set; } = "";
    public long Downloaded { get; set; }
    public long Uploaded { get; set; }
    public int[] Priorities { get; set; } = Array.Empty<int>();
    public long[] FileSizes { get; set; } = Array.Empty<long>();
    /// <summary>When each file was last written (UTC ticks): a file changed behind our back is checked again instead of trusted.</summary>
    public long[] FileTimes { get; set; } = Array.Empty<long>();
    public DateTime SavedUtc { get; set; }
    /// <summary>When the wanted files were first complete (UTC); the seed-time limit counts from here, across restarts.</summary>
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>One torrent: finds peers (trackers, DHT, peer exchange, magnet hints), fetches metadata for magnet links, downloads and verifies pieces, uploads to others.</summary>
public sealed partial class TorrentSession
{
    sealed class ActivePiece
    {
        public int Index, Size, BlockCount, ReceivedCount;
        public byte[] Buffer;
        public bool[] Received;
        public List<PeerState>?[] Requesters;
        public List<PeerState> Contributors = new();
        public ActivePiece(int index, int size)
        {
            Index = index; Size = size; BlockCount = (size + BlockSize - 1) / BlockSize;
            Buffer = new byte[size]; Received = new bool[BlockCount]; Requesters = new List<PeerState>?[BlockCount];
        }
        public int BlockLength(int block) => Math.Min(BlockSize, Size - block * BlockSize);
        public int Remaining => BlockCount - ReceivedCount;
        public int FreeBlock(PeerState? duplicateFor)
        {
            for (var b = 0; b < BlockCount; b++)
            {
                if (Received[b]) continue;
                var r = Requesters[b];
                if (r == null || r.Count == 0) return b;
                if (duplicateFor != null && r.Count < 2 && !r.Contains(duplicateFor)) return b;
            }
            return -1;
        }
    }

    sealed class TrackerEntry { public string Url = ""; public string Status = "Not contacted yet"; public int? Seeders, Leechers; public DateTime? Last; public int Tier; }

    public const int BlockSize = 16 * 1024;
    /// <summary>Connection attempts in flight at once. Most addresses a tracker or the DHT returns are dead, so a small
    /// number here is what makes a torrent take minutes to find its first working peers.</summary>
    const int MaxHalfOpen = 40;
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Upload requests one peer may have queued before its read loop waits for them to be served.</summary>
    const int MaxPendingUploadsPerPeer = 64;
    const int OurUtMetadata = 2, OurUtPex = 1;
    static readonly HttpClient WebSeedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(30) };

    readonly TorrentEngine _engine;
    readonly object _sync = new();
    readonly byte[] _hash;
    readonly MagnetLink? _magnet;
    readonly string _saveDirectory;

    MetaInfo? _meta;
    TorrentStorage? _storage;
    Bitfield _have = new(0);
    FilePriority[] _priorities = Array.Empty<FilePriority>();
    bool[] _wanted = Array.Empty<bool>();
    bool[] _high = Array.Empty<bool>();
    int[] _availability = Array.Empty<int>();
    long _wantedBytes, _haveWantedBytes;
    readonly Dictionary<int, ActivePiece> _active = new();
    readonly HashSet<int> _verifying = new();
    readonly List<PeerState> _peers = new();
    readonly HashSet<string> _known = new();
    readonly Queue<IPEndPoint> _candidates = new();
    readonly Dictionary<string, DateTime> _nextTry = new();
    readonly HashSet<string> _connecting = new();
    readonly HashSet<string> _banned = new();
    readonly List<TrackerEntry> _trackers = new();
    readonly RateMeter _down = new(), _up = new();
    long _downloadedTotal, _uploadedTotal, _wasted;
    int _hashFails;

    // metadata exchange (magnet links)
    byte[]? _metaBuffer; bool[] _metaGot = Array.Empty<bool>(); int _metaSize;
    readonly Dictionary<int, DateTime> _metaAsked = new();
    readonly TaskCompletionSource _metadataReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly SemaphoreSlim _announceKick = new(0, 1);

    CancellationTokenSource? _cts;
    Task? _run;
    int _rechokeRound;
    DateTime _completedAt, _lastResumeSave = DateTime.UtcNow;
    bool _announcedCompleted;
    volatile TorrentState _state = TorrentState.Stopped;
    volatile string? _error;
    volatile int _checkedPieces;

    public TokenBucket DownloadLimit { get; } = new();
    public TokenBucket UploadLimit { get; } = new();
    public byte[] InfoHash => _hash;
    public string InfoHashHex => Convert.ToHexString(_hash).ToLowerInvariant();
    public MetaInfo? Meta => _meta;
    public string SaveDirectory => _saveDirectory;
    public TorrentState State => _state;
    public string? Error => _error;
    public bool IsPrivate => _meta?.IsPrivate ?? false;
    public string Name => _meta?.Name ?? _magnet?.Name ?? InfoHashHex;
    public long TotalSize => _meta?.TotalLength ?? 0;
    public long WantedBytes { get { lock (_sync) return _wantedBytes; } }
    public long BytesDone { get { lock (_sync) return _haveWantedBytes; } }
    public double Progress { get { lock (_sync) return _wantedBytes == 0 ? (_meta == null ? 0 : 100) : 100.0 * _haveWantedBytes / _wantedBytes; } }
    public long DownloadedTotal => Interlocked.Read(ref _downloadedTotal);
    public long UploadedTotal => Interlocked.Read(ref _uploadedTotal);
    public long DownloadRate => _down.BytesPerSecond;
    public long UploadRate => _up.BytesPerSecond;
    public double Ratio => UploadedTotal / (double)Math.Max(1, DownloadedTotal > 0 ? DownloadedTotal : Math.Max(1, WantedBytes));
    public int HashFailures => _hashFails;
    public long Wasted => Interlocked.Read(ref _wasted);
    public int PeerCount { get { lock (_sync) return _peers.Count; } }
    public int SeedCount { get { lock (_sync) return _peers.Count(p => p.IsSeed); } }
    public int KnownPeers { get { lock (_sync) return _known.Count; } }
    public double CheckProgress => _meta == null || _meta.PieceCount == 0 ? 0 : 100.0 * _checkedPieces / _meta.PieceCount;
    public DateTime? CompletedAt => _completedAt == default ? null : _completedAt;
    public string ContentPath => _storage?.ContentPath ?? "";
    public bool IsComplete { get { lock (_sync) return _meta != null && WantedComplete(); } }
    public Task Completed => _completed.Task;
    public Task MetadataReady => _metadataReady.Task;

    public PeerCandidateManager CandidateManager { get; } = new();
    public TorrentAsyncDiskPipeline DiskPipeline { get; } = new(32, 2);

    public TorrentPerformanceMetrics GetMetricsSnapshot()
    {
        var peers = SnapshotPeers();
        var useful = peers.Count(p => p.Pipeline.Health is PeerHealthClassification.Healthy or PeerHealthClassification.Excellent or PeerHealthClassification.Normal);
        var stalled = peers.Count(p => p.Pipeline.Health == PeerHealthClassification.Stalled);
        var seeds = peers.Count(p => p.IsSeed);
        var avgRtt = peers.Count > 0 ? peers.Average(p => p.Pipeline.SmoothedRttMs) : 0.0;
        var inFlight = peers.Sum(p => p.Outstanding.Count);

        var metrics = new TorrentPerformanceMetrics
        {
            DownloadBytesPerSec = DownloadRate,
            UploadBytesPerSec = UploadRate,
            ConnectedPeers = peers.Count,
            UsefulPeers = useful,
            StalledPeers = stalled,
            Seeds = seeds,
            AverageRttMs = avgRtt,
            TotalCandidates = CandidateManager.Count,
            RequestsInFlight = inFlight,
            DiskWriteBytesPerSec = DiskPipeline.WriteRateBytesPerSec,
            HashingBytesPerSec = DiskPipeline.HashingRateBytesPerSec,
            DiskQueueDepth = DiskPipeline.QueueDepth
        };

        metrics.EvaluateDiagnostics();
        return metrics;
    }

    public TorrentSession(TorrentEngine engine, byte[] infoHash, MagnetLink? magnet, MetaInfo? meta, string saveDirectory)
    {
        _engine = engine; _hash = infoHash; _magnet = magnet; _saveDirectory = saveDirectory;
        if (magnet != null) foreach (var url in magnet.Trackers) _trackers.Add(new TrackerEntry { Url = url, Tier = 0 });
        if (meta != null) SetMeta(meta, alreadyKnown: true);
        if (magnet != null) foreach (var peer in magnet.Peers) if (TryParseEndpoint(peer, out var ep)) AddCandidate(ep, PeerSource.Manual);
    }

    static bool TryParseEndpoint(string text, out IPEndPoint ep)
    {
        ep = null!;
        var i = text.LastIndexOf(':');
        if (i <= 0 || !IPAddress.TryParse(text[..i].Trim('[', ']'), out var address) || !int.TryParse(text[(i + 1)..], out var port) || port is < 1 or > 65535) return false;
        ep = new IPEndPoint(address, port);
        return true;
    }

    void SetMeta(MetaInfo meta, bool alreadyKnown)
    {
        _meta = meta;
        if (!alreadyKnown || _trackers.Count == 0 || meta.TrackerTiers.Count > 0)
        {
            var tier = 0;
            foreach (var t in meta.TrackerTiers)
            {
                foreach (var url in t) if (_trackers.All(x => x.Url != url)) _trackers.Add(new TrackerEntry { Url = url, Tier = tier });
                tier++;
            }
        }
        _have = new Bitfield(meta.PieceCount);
        _priorities = Enumerable.Repeat(FilePriority.Normal, meta.Files.Count).ToArray();
        _availability = new int[meta.PieceCount];
        RecomputeWanted();
        _metadataReady.TrySetResult();
    }

    // ---------------------------------------------------------------- control

    public void Start()
    {
        lock (_sync)
        {
            if (_run is { IsCompleted: false }) return;
            _error = null;
            // forget who we already tried: a fresh run must be able to reconnect the very peers a tracker/DHT hands back again
            _known.Clear(); _candidates.Clear(); _nextTry.Clear(); _connecting.Clear();
            foreach (var t in _trackers) t.Last = null;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _run = Task.Run(() => RunAsync(ct));
        }
    }

    /// <summary>Stops transfers, keeps the position (files and resume data) so it can be started again.</summary>
    public async Task PauseAsync()
    {
        CancellationTokenSource? cts; Task? run;
        lock (_sync) { cts = _cts; run = _run; }
        if (cts == null || run == null) { if (_state is TorrentState.Downloading or TorrentState.Seeding) _state = TorrentState.Paused; return; }
        cts.Cancel();
        try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (_state != TorrentState.Finished && _state != TorrentState.Error) _state = TorrentState.Paused;
    }

    /// <summary>Stops and forgets the peers; <paramref name="deleteData"/> also removes the downloaded files.</summary>
    public async Task StopAsync(bool deleteData)
    {
        await PauseAsync().ConfigureAwait(false);
        try { await AnnounceStoppedAsync().ConfigureAwait(false); } catch (Exception) { }
        lock (_sync) { _storage?.Flush(); }
        if (deleteData) { _storage?.Delete(); _engine.DeleteResume(InfoHashHex); }
        else _storage?.Dispose();
        _state = TorrentState.Stopped;
    }

    public void SetFilePriority(int index, FilePriority priority)
    {
        lock (_sync)
        {
            if (_meta == null || index < 0 || index >= _priorities.Length) return;
            _priorities[index] = priority;
            RecomputeWanted();
            if (!WantedComplete())
            {
                // More to download again (a skipped file was chosen after the rest had finished): completion had
                // switched the files to read-only for sharing, so they must be writable again or nothing can be saved.
                _storage?.MarkWritable();
                _completedAt = default;
                if (_state == TorrentState.Seeding) { _state = TorrentState.Downloading; _announcedCompleted = false; }
            }
        }
        KickRequests();
    }

    /// <summary>Keeps the chosen files (before the first start), so that "download later" survives a restart.</summary>
    public void PersistSelection()
    {
        if (_meta != null && _engine.LoadResume(InfoHashHex) == null) SaveResume();
    }

    public async Task<MetaInfo> WaitForMetadataAsync(CancellationToken ct)
    {
        await _metadataReady.Task.WaitAsync(ct).ConfigureAwait(false);
        return _meta!;
    }

    // ---------------------------------------------------------------- the run loop

    async Task RunAsync(CancellationToken ct)
    {
        var loops = new List<Task>();
        try
        {
            _state = _meta == null ? TorrentState.Metadata : TorrentState.Checking;
            loops.Add(Task.Run(() => AnnounceLoopAsync(ct)));
            loops.Add(Task.Run(() => ConnectLoopAsync(ct)));
            loops.Add(Task.Run(() => DhtLoopAsync(ct)));
            loops.Add(Task.Run(() => MaintenanceLoopAsync(ct)));

            if (_meta == null) await _metadataReady.Task.WaitAsync(ct).ConfigureAwait(false);
            await PrepareContentAsync(ct).ConfigureAwait(false);
            loops.Add(Task.Run(() => RechokeLoopAsync(ct)));
            // A single-file BEP 19 source maps directly to the torrent byte stream. Multi-file sources need a
            // separate range request for every file a piece crosses, so leave those to peers rather than risk bad data.
            if (!_meta!.IsMultiFile)
                for (var i = 0; i < Math.Min(2, _meta.WebSeeds.Count); i++) loops.Add(Task.Run(() => WebSeedLoopAsync(ct)));
            KickRequests();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _error = ex.Message; _state = TorrentState.Error;
            _completed.TrySetException(ex);
        }
        finally
        {
            lock (_sync) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
            List<PeerState> peers;
            lock (_sync) peers = _peers.ToList();
            foreach (var p in peers) p.Connection.Dispose();
            try { await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
            _storage?.Dispose();          // closes the files (they reopen on demand) so their modification times are final
            SaveResume();
        }
    }

    async Task PrepareContentAsync(CancellationToken ct)
    {
        var meta = _meta!;
        _storage ??= new TorrentStorage(meta, _saveDirectory);
        _state = TorrentState.Checking;
        _checkedPieces = 0;
        var resume = _engine.LoadResume(InfoHashHex);
        if (resume != null && resume.Priorities.Length == meta.Files.Count)
            lock (_sync) { for (var i = 0; i < _priorities.Length; i++) _priorities[i] = Enum.IsDefined(typeof(FilePriority), resume.Priorities[i]) ? (FilePriority)resume.Priorities[i] : FilePriority.Normal; RecomputeWanted(); }

        Bitfield? have = null;
        if (resume != null && TryLoadResume(resume, meta, out var trusted)) have = trusted;
        have ??= await Task.Run(() => _storage.Check(n => { _checkedPieces = n; return !ct.IsCancellationRequested; }, ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _have = have!;
            if (resume != null) { Interlocked.Exchange(ref _downloadedTotal, resume.Downloaded); Interlocked.Exchange(ref _uploadedTotal, resume.Uploaded); }
            if (resume?.CompletedUtc is { } completedUtc && _completedAt == default) _completedAt = completedUtc;
            RecomputeWanted();
            foreach (var p in _peers) ApplyRawBitfield(p);
            if (WantedComplete()) EnterSeedingLocked(); else { _state = TorrentState.Downloading; _completedAt = default; _storage.MarkWritable(); }
        }
        // everybody we already talk to may now be interesting
        foreach (var p in SnapshotPeers()) _ = UpdateInterestAsync(p, ct);
    }

    /// <summary>The saved bitfield is trusted only when the files look right and a few "verified" pieces really still verify.</summary>
    bool TryLoadResume(TorrentResume resume, MetaInfo meta, out Bitfield? have)
    {
        have = null;
        try
        {
            var loaded = new Bitfield(meta.PieceCount);
            if (!loaded.TryLoad(Convert.FromBase64String(resume.Bitfield)) || loaded.Count == 0) return false;      // nothing verified yet: look at the disk instead
            if (resume.FileSizes.Length != meta.Files.Count || resume.FileTimes.Length != meta.Files.Count) return false;
            var fileHasPieces = new bool[meta.Files.Count];
            for (var piece = 0; piece < meta.PieceCount; piece++)
            {
                if (!loaded[piece]) continue;
                var start = meta.PieceOffset(piece); var end = start + meta.PieceSize(piece);
                foreach (var f in meta.Files) if (f.Length > 0 && f.Offset < end && f.Offset + f.Length > start) fileHasPieces[f.Index] = true;
            }
            foreach (var f in meta.Files)
            {
                if (!fileHasPieces[f.Index]) continue;
                var info = new FileInfo(_storage!.FilePath(f));
                // an exact match, not a tolerance window: our own save records the instant the file was last written, so anything
                // else touching the file afterwards - even moments later - must not be waved through as "probably still fine".
                if (!info.Exists || info.Length != f.Length || info.LastWriteTimeUtc.Ticks != resume.FileTimes[f.Index]) return false;
            }
            var rnd = new Random();
            var have1 = Enumerable.Range(0, meta.PieceCount).Where(i => loaded[i]).OrderBy(_ => rnd.Next()).Take(3).ToList();
            using var sha = SHA1.Create();
            var buffer = new byte[meta.PieceLength];
            foreach (var piece in have1)
            {
                var size = meta.PieceSize(piece);
                if (!_storage!.TryRead(meta.PieceOffset(piece), buffer.AsSpan(0, size)) || !sha.ComputeHash(buffer, 0, size).AsSpan().SequenceEqual(meta.PieceHash(piece))) return false;
            }
            have = loaded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }

    void SaveResume()
    {
        try
        {
            TorrentResume resume;
            _storage?.Flush();
            lock (_sync)
            {
                if (_meta == null || _have.Length == 0) return;
                resume = new TorrentResume
                {
                    Bitfield = Convert.ToBase64String(_have.ToBytes()), Downloaded = DownloadedTotal, Uploaded = UploadedTotal, SavedUtc = DateTime.UtcNow,
                    Priorities = _priorities.Select(p => (int)p).ToArray(), FileSizes = _meta.Files.Select(f => f.Length).ToArray(),
                    CompletedUtc = _completedAt == default ? null : _completedAt
                };
                resume.FileTimes = _meta.Files.Select(f => { try { var p = _storage?.FilePath(f); return p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : 0L; } catch (IOException) { return 0L; } }).ToArray();
            }
            _engine.SaveResume(InfoHashHex, resume);
            if (_meta != null) _engine.SaveTorrentFile(_meta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next save tries again */ }
    }

    // ---------------------------------------------------------------- wanted pieces

    void RecomputeWanted()
    {
        var meta = _meta;
        if (meta == null) return;
        _wanted = new bool[meta.PieceCount]; _high = new bool[meta.PieceCount];
        foreach (var f in meta.Files)
        {
            if (f.Length == 0 || _priorities[f.Index] == FilePriority.Skip) continue;
            var first = (int)(f.Offset / meta.PieceLength); var last = (int)((f.Offset + f.Length - 1) / meta.PieceLength);
            for (var p = first; p <= last; p++) { _wanted[p] = true; if (_priorities[f.Index] == FilePriority.High) _high[p] = true; }
        }
        _wantedBytes = 0; _haveWantedBytes = 0;
        for (var p = 0; p < meta.PieceCount; p++)
            if (_wanted[p]) { _wantedBytes += meta.PieceSize(p); if (_have[p]) _haveWantedBytes += meta.PieceSize(p); }
    }

    bool WantedComplete()
    {
        for (var p = 0; p < _wanted.Length; p++) if (_wanted[p] && !_have[p]) return false;
        return true;
    }

    // ---------------------------------------------------------------- HTTP web seeds (BEP 19)

    async Task WebSeedLoopAsync(CancellationToken ct)
    {
        var seedOffset = Random.Shared.Next(Math.Max(1, _meta!.WebSeeds.Count));
        while (!ct.IsCancellationRequested)
        {
            ActivePiece? piece = null;
            lock (_sync)
            {
                if (_state != TorrentState.Downloading) return;
                for (var i = 0; i < _wanted.Length; i++)
                {
                    if (!_wanted[i] || _have[i] || _active.ContainsKey(i) || _verifying.Contains(i)) continue;
                    piece = new ActivePiece(i, _meta!.PieceSize(i));
                    _active[i] = piece;
                    break;
                }
            }
            if (piece == null) { await Task.Delay(500, ct).ConfigureAwait(false); continue; }

            var downloaded = false;
            for (var n = 0; n < _meta!.WebSeeds.Count && !downloaded; n++)
            {
                try
                {
                    var url = WebSeedUrl(_meta.WebSeeds[(seedOffset + n) % _meta.WebSeeds.Count], _meta);
                    var start = _meta.PieceOffset(piece.Index);
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, start + piece.Size - 1);
                    using var response = await WebSeedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent && !(start == 0 && response.IsSuccessStatusCode)) continue;
                    await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    var read = 0;
                    while (read < piece.Size)
                    {
                        var count = await input.ReadAsync(piece.Buffer.AsMemory(read, Math.Min(64 * 1024, piece.Size - read)), ct).ConfigureAwait(false);
                        if (count == 0) break;
                        await TokenBucket.WaitAllAsync(count, ct, _engine.DownloadLimit, DownloadLimit).ConfigureAwait(false);
                        read += count; _down.Add(count); Interlocked.Add(ref _downloadedTotal, count);
                    }
                    downloaded = read == piece.Size;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { }
            }

            lock (_sync)
            {
                _active.Remove(piece.Index);
                if (downloaded) _verifying.Add(piece.Index);
            }
            if (downloaded) await FinishPieceAsync(piece, ct).ConfigureAwait(false);
            else await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    static string WebSeedUrl(string seed, MetaInfo meta)
    {
        if (!meta.IsMultiFile && !seed.EndsWith('/')) return seed;
        var parts = new List<string>();
        if (meta.IsMultiFile) parts.Add(meta.Name);
        if (!meta.IsMultiFile) parts.Add(meta.Files[0].Path);
        var suffix = string.Join('/', parts.SelectMany(p => p.Split('/')).Select(Uri.EscapeDataString));
        return seed.TrimEnd('/') + "/" + suffix;
    }

    // ---------------------------------------------------------------- peers: finding and connecting

    public void AddCandidate(IPEndPoint ep) => AddCandidate(ep, PeerSource.Manual);

    public void AddCandidate(IPEndPoint ep, PeerSource source)
    {
        if (ep.Port == 0 || IPAddress.IsLoopback(ep.Address) && ep.Port == _engine.ListenPort && !_engine.AllowLocalPeers) return;
        lock (_sync)
        {
            if (_banned.Contains(ep.Address.ToString())) return;
            CandidateManager.AddOrUpdate(ep, source);
            if (_known.Add(ep.ToString())) _candidates.Enqueue(ep);
        }
    }

    async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var toTry = new List<IPEndPoint>();
            lock (_sync)
            {
                var retry = _nextTry.Where(kv => kv.Value <= DateTime.UtcNow).Select(kv => kv.Key).ToList();
                foreach (var key in retry) { _nextTry.Remove(key); var parts = key.Split('|'); if (TryParseEndpoint(parts[0], out var ep)) _candidates.Enqueue(ep); }
                while (_candidates.Count > 0 && _peers.Count + _connecting.Count + toTry.Count < _engine.Options.MaxPeersPerTorrent && _connecting.Count + toTry.Count < MaxHalfOpen)
                {
                    var ep = _candidates.Dequeue();
                    if (_peers.Any(p => p.EndPoint.Equals(ep)) || _connecting.Contains(ep.ToString()) || !_engine.TryReserveConnection()) continue;
                    _connecting.Add(ep.ToString());
                    toTry.Add(ep);
                }
            }
            foreach (var ep in toTry) _ = Task.Run(() => ConnectPeerAsync(ep, ct), ct);
        }
    }

    async Task ConnectPeerAsync(IPEndPoint ep, CancellationToken ct)
    {
        var connected = false;
        try
        {
            var conn = await PeerConnection.ConnectAsync(ep, ConnectTimeout, ct).ConfigureAwait(false);
            connected = true;
            var encryption = _engine.Options.Encryption;
            if (encryption != EncryptionMode.Off)
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(TimeSpan.FromSeconds(8));
                    await conn.NegotiateOutgoingAsync(_hash, encryption != EncryptionMode.Require, limit.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested && encryption == EncryptionMode.Prefer)
                {
                    // this peer does not speak encryption (or something in between broke it): once more, plainly
                    conn.Dispose();
                    conn = await PeerConnection.ConnectAsync(ep, ConnectTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception) { conn.Dispose(); throw; }
            }
            await RunPeerAsync(conn, null, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { /* unreachable or misbehaving: try again later */ }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_sync)
            {
                _connecting.Remove(ep.ToString());
                if (!ct.IsCancellationRequested) _nextTry[ep + "|retry"] = DateTime.UtcNow.AddSeconds(connected ? 40 : 90);
            }
            _engine.ReleaseConnection();
        }
    }

    /// <summary>An incoming connection whose handshake the engine already read.</summary>
    internal async Task AcceptAsync(PeerConnection conn, PeerConnection.Handshake handshake)
    {
        CancellationToken ct;
        lock (_sync)
        {
            if (_cts == null || _cts.IsCancellationRequested || _state is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error or TorrentState.Finished || _peers.Count >= _engine.Options.MaxPeersPerTorrent + 20 || _banned.Contains(conn.EndPoint.Address.ToString()))
            { conn.Dispose(); return; }
            ct = _cts.Token;
        }
        try { await RunPeerAsync(conn, handshake, ct).ConfigureAwait(false); }
        catch (Exception) { /* the peer went away */ }
    }

    async Task RunPeerAsync(PeerConnection conn, PeerConnection.Handshake? incoming, CancellationToken ct)
    {
        var peer = new PeerState(conn);
        var added = false;
        try
        {
            PeerConnection.Handshake hs;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (incoming == null) { await conn.SendHandshakeAsync(_hash, _engine.PeerId, timeout.Token).ConfigureAwait(false); hs = await conn.ReadHandshakeAsync(timeout.Token).ConfigureAwait(false); }
                else { hs = incoming; await conn.SendHandshakeAsync(_hash, _engine.PeerId, timeout.Token).ConfigureAwait(false); }
            }
            if (!hs.InfoHash.AsSpan().SequenceEqual(_hash) || hs.PeerId.AsSpan().SequenceEqual(_engine.PeerId)) return;
            peer.PeerId = hs.PeerId; peer.Client = PeerState.ClientFromPeerId(hs.PeerId);
            peer.SupportsExtensions = hs.SupportsExtensions; peer.SupportsDht = hs.SupportsDht;
            lock (_sync)
            {
                if (_peers.Any(p => p.PeerId.AsSpan().SequenceEqual(peer.PeerId))) return;
                _peers.Add(peer); added = true;
            }
            if (peer.SupportsExtensions) await SendExtendedHandshakeAsync(peer, ct).ConfigureAwait(false);
            byte[]? bitfield = null;
            lock (_sync) { if (_meta != null && _have.Count > 0) bitfield = _have.ToBytes(); }
            if (bitfield != null) await conn.SendAsync(PeerConnection.BitfieldId, bitfield, ct).ConfigureAwait(false);
            if (peer.SupportsDht && _engine.Dht != null) await conn.SendAsync(PeerConnection.Port, new[] { (byte)(_engine.Dht.Port >> 8), (byte)_engine.Dht.Port }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var (id, payload) = await conn.ReadMessageAsync(ct).ConfigureAwait(false);
                peer.LastReceived = DateTime.UtcNow;
                if (id != 255) await HandleMessageAsync(peer, id, payload, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (added) RemovePeer(peer);
            conn.Dispose();
        }
    }

    void RemovePeer(PeerState peer)
    {
        lock (_sync)
        {
            _peers.Remove(peer);
            foreach (var o in peer.Outstanding.ToList()) UnmarkRequested(peer, o.Piece, o.Offset);
            peer.Outstanding.Clear();
            if (peer.Have != null && _availability.Length == peer.Have.Length) for (var i = 0; i < _availability.Length; i++) if (peer.Have[i] && _availability[i] > 0) _availability[i]--;
        }
    }

    List<PeerState> SnapshotPeers() { lock (_sync) return _peers.ToList(); }

    // ---------------------------------------------------------------- messages

    async Task HandleMessageAsync(PeerState peer, byte id, byte[] payload, CancellationToken ct)
    {
        switch (id)
        {
            case PeerConnection.Choke:
                lock (_sync)
                {
                    peer.PeerChoking = true;
                    foreach (var o in peer.Outstanding.ToList()) UnmarkRequested(peer, o.Piece, o.Offset);
                    peer.Outstanding.Clear();
                }
                break;
            case PeerConnection.Unchoke:
                lock (_sync) peer.PeerChoking = false;
                await RequestMoreAsync(peer, ct).ConfigureAwait(false);
                break;
            case PeerConnection.Interested:
                lock (_sync) peer.PeerInterested = true;
                await MaybeUnchokeAsync(peer, ct).ConfigureAwait(false);
                break;
            case PeerConnection.NotInterested:
                lock (_sync) peer.PeerInterested = false;
                break;
            case PeerConnection.Have:
                if (payload.Length != 4) throw new IOException("Bad have message.");
                lock (_sync)
                {
                    if (_meta == null) break;
                    var index = PeerConnection.ReadInt(payload, 0);
                    if ((uint)index >= (uint)_meta.PieceCount) throw new IOException("A peer announced a piece that does not exist.");
                    peer.Have ??= new Bitfield(_meta.PieceCount);
                    if (!peer.Have[index]) { peer.Have.Set(index); _availability[index]++; }
                }
                await UpdateInterestAsync(peer, ct).ConfigureAwait(false);
                await RequestMoreAsync(peer, ct).ConfigureAwait(false);
                break;
            case PeerConnection.BitfieldId:
                lock (_sync)
                {
                    peer.RawBitfield = payload;
                    ApplyRawBitfield(peer);
                }
                await UpdateInterestAsync(peer, ct).ConfigureAwait(false);
                await RequestMoreAsync(peer, ct).ConfigureAwait(false);
                break;
            case PeerConnection.Request: await QueueUploadAsync(peer, payload, ct).ConfigureAwait(false); break;
            case PeerConnection.Piece: await HandlePieceAsync(peer, payload, ct).ConfigureAwait(false); break;
            case PeerConnection.Port:
                if (payload.Length == 2 && _engine.Dht != null && !IsPrivate && _engine.Dht.Bootstrap.Count < 32) _engine.Dht.Bootstrap.Add(new IPEndPoint(peer.EndPoint.Address, (payload[0] << 8) | payload[1]));
                break;
            case PeerConnection.Extended:
                if (payload.Length > 0) await HandleExtendedAsync(peer, payload, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Called with the lock held.</summary>
    void ApplyRawBitfield(PeerState peer)
    {
        if (_meta == null || peer.RawBitfield == null) return;
        var bits = new Bitfield(_meta.PieceCount);
        if (!bits.TryLoad(peer.RawBitfield)) throw new IOException("A peer sent a damaged bitfield.");
        if (peer.Have != null) for (var i = 0; i < _availability.Length; i++) if (peer.Have[i] && _availability[i] > 0) _availability[i]--;
        peer.Have = bits;
        for (var i = 0; i < _availability.Length; i++) if (bits[i]) _availability[i]++;
        peer.RawBitfield = null;
    }

    async Task UpdateInterestAsync(PeerState peer, CancellationToken ct)
    {
        bool want;
        lock (_sync)
        {
            if (_meta == null || peer.Have == null) return;
            want = false;
            for (var i = 0; i < _wanted.Length && !want; i++) want = _wanted[i] && !_have[i] && peer.Have[i];
            if (want == peer.AmInterested) return;
            peer.AmInterested = want;
        }
        await peer.Connection.SendAsync(want ? PeerConnection.Interested : PeerConnection.NotInterested, ReadOnlySpan<byte>.Empty, ct).ConfigureAwait(false);
        if (want) await RequestMoreAsync(peer, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- downloading

    void KickRequests()
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        foreach (var p in SnapshotPeers()) _ = Task.Run(async () => { try { await UpdateInterestAsync(p, ct).ConfigureAwait(false); await RequestMoreAsync(p, ct).ConfigureAwait(false); } catch (Exception) { p.Connection.Dispose(); } }, ct);
    }

    async Task RequestMoreAsync(PeerState peer, CancellationToken ct)
    {
        var requests = new List<(int Piece, int Offset, int Length)>();
        lock (_sync)
        {
            if (_meta == null || _state != TorrentState.Downloading || peer.PeerChoking || !peer.AmInterested || peer.Have == null) return;
            var depth = peer.Pipeline.CalculateOptimalDepth(peer.Down.BytesPerSecond);
            while (peer.Outstanding.Count + requests.Count < depth)
            {
                var next = NextBlock(peer);
                if (next == null) break;
                requests.Add(next.Value);
            }
        }
        foreach (var (piece, offset, length) in requests)
        {
            peer.Pipeline.TrackRequest(piece, offset, length);
            await peer.Connection.SendAsync(PeerConnection.Request, PeerConnection.RequestPayload(piece, offset, length), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Picks the next block for this peer and records the request. Called with the lock held.</summary>
    (int Piece, int Offset, int Length)? NextBlock(PeerState peer)
    {
        // 1. finish pieces that are already started (the one with the fewest missing blocks first)
        ActivePiece? chosen = null; var block = -1;
        foreach (var ap in _active.Values)
        {
            if (!peer.Have![ap.Index]) continue;
            var b = ap.FreeBlock(null);
            if (b >= 0 && (chosen == null || ap.Remaining < chosen.Remaining)) { chosen = ap; block = b; }
        }
        // 2. start a new piece: high priority first, then the rarest one this peer has
        if (chosen == null)
        {
            var best = -1; var bestScore = int.MaxValue;
            var start = Random.Shared.Next(Math.Max(1, _wanted.Length));
            for (var n = 0; n < _wanted.Length; n++)
            {
                var i = (start + n) % _wanted.Length;
                if (!_wanted[i] || _have[i] || !peer.Have![i] || _active.ContainsKey(i) || _verifying.Contains(i)) continue;
                var score = (_high[i] ? 0 : 1_000_000) + _availability[i];
                if (score < bestScore) { bestScore = score; best = i; if (score <= 1) break; }
            }
            if (best >= 0)
            {
                chosen = new ActivePiece(best, _meta!.PieceSize(best));
                _active[best] = chosen;
                block = 0;
            }
        }
        // 3. end game: everything left is already requested - ask a second peer for the missing blocks so the last pieces do not wait for a slow one
        if (chosen == null)
        {
            var missingNotActive = 0;
            for (var i = 0; i < _wanted.Length && missingNotActive == 0; i++) if (_wanted[i] && !_have[i] && !_active.ContainsKey(i) && !_verifying.Contains(i)) missingNotActive++;
            if (missingNotActive == 0)
                foreach (var ap in _active.Values)
                {
                    if (!peer.Have![ap.Index]) continue;
                    var b = ap.FreeBlock(peer);
                    if (b >= 0) { chosen = ap; block = b; break; }
                }
        }
        if (chosen == null) return null;
        (chosen.Requesters[block] ??= new List<PeerState>()).Add(peer);
        var offset = block * BlockSize;
        peer.Outstanding.Add((chosen.Index, offset));
        return (chosen.Index, offset, chosen.BlockLength(block));
    }

    /// <summary>Called with the lock held.</summary>
    void UnmarkRequested(PeerState peer, int piece, int offset)
    {
        peer.Pipeline.RemoveRequest(piece, offset);
        if (_active.TryGetValue(piece, out var ap))
        {
            var block = offset / BlockSize;
            if (block < ap.BlockCount) ap.Requesters[block]?.Remove(peer);
        }
    }

    async Task HandlePieceAsync(PeerState peer, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 9) throw new IOException("Bad piece message.");
        var piece = PeerConnection.ReadInt(payload, 0); var offset = PeerConnection.ReadInt(payload, 4); var length = payload.Length - 8;
        ActivePiece? finished = null;
        List<PeerState>? cancel = null;
        peer.Pipeline.CompleteRequest(piece, offset, out _);
        lock (_sync)
        {
            peer.Outstanding.Remove((piece, offset));
            if (!_active.TryGetValue(piece, out var ap) || offset < 0 || offset % BlockSize != 0 || offset / BlockSize >= ap.BlockCount || length != ap.BlockLength(offset / BlockSize))
            {
                Interlocked.Add(ref _wasted, length);
                goto after;
            }
            var block = offset / BlockSize;
            ap.Requesters[block]?.Remove(peer);
            if (ap.Received[block]) { Interlocked.Add(ref _wasted, length); goto after; }
            Buffer.BlockCopy(payload, 8, ap.Buffer, offset, length);
            ap.Received[block] = true; ap.ReceivedCount++;
            if (!ap.Contributors.Contains(peer)) ap.Contributors.Add(peer);
            if (ap.Requesters[block] is { Count: > 0 } others)
            {
                cancel = others.ToList();
                foreach (var o in cancel)
                {
                    o.Outstanding.Remove((piece, offset));
                    o.Pipeline.RemoveRequest(piece, offset);
                    o.Pipeline.DuplicateRequests++;
                }
                ap.Requesters[block]!.Clear();
            }
            Interlocked.Add(ref _downloadedTotal, length); _down.Add(length); peer.Down.Add(length);
            if (ap.ReceivedCount == ap.BlockCount) { _active.Remove(piece); _verifying.Add(piece); finished = ap; }
        after:;
        }
        if (cancel != null)
            foreach (var other in cancel) _ = other.Connection.SendAsync(PeerConnection.Cancel, PeerConnection.RequestPayload(piece, offset, length), ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        await TokenBucket.WaitAllAsync(length, ct, _engine.DownloadLimit, DownloadLimit).ConfigureAwait(false);
        if (finished != null) await FinishPieceAsync(finished, ct).ConfigureAwait(false);
        await RequestMoreAsync(peer, ct).ConfigureAwait(false);
    }

    async Task FinishPieceAsync(ActivePiece piece, CancellationToken ct)
    {
        var meta = _meta!;
        bool ok;
        try
        {
            var diskTask = new DiskWriteTask(
                piece.Index,
                piece.Buffer,
                meta.PieceOffset(piece.Index),
                meta.PieceHash(piece.Index).ToArray(),
                piece.Contributors);
            ok = await DiskPipeline.EnqueueAndVerifyAsync(diskTask, _storage!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _error = "The files could not be written: " + ex.Message; _state = TorrentState.Error;
            _completed.TrySetException(ex);
            _cts?.Cancel();
            return;
        }

        var peersToTell = new List<PeerState>();
        var completeNow = false;
        lock (_sync)
        {
            _verifying.Remove(piece.Index);
            if (ok)
            {
                _have.Set(piece.Index);
                if (_wanted[piece.Index]) _haveWantedBytes += piece.Size;
                peersToTell = _peers.ToList();
                completeNow = _state == TorrentState.Downloading && WantedComplete();
                if (completeNow) EnterSeedingLocked();
            }
            else
            {
                Interlocked.Increment(ref _hashFails);
                Interlocked.Add(ref _wasted, piece.Size);
                Interlocked.Add(ref _downloadedTotal, 0);
                foreach (var c in piece.Contributors)
                {
                    if (piece.Contributors.Count == 1) c.Strikes++;      // only a peer that sent the whole bad piece alone is blamed
                    if (c.Strikes >= 3) { _banned.Add(c.EndPoint.Address.ToString()); c.Connection.Dispose(); }
                }
            }
        }
        if (ok)
        {
            var have = PeerConnection.U32(piece.Index);
            foreach (var p in peersToTell) _ = p.Connection.SendAsync(PeerConnection.Have, have, ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            foreach (var p in peersToTell) _ = UpdateInterestAsync(p, ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            if (completeNow) OnWantedCompleted();
        }
        else KickRequests();
    }

    /// <summary>Called with the lock held.</summary>
    void EnterSeedingLocked()
    {
        _state = TorrentState.Seeding;
        if (_completedAt == default) _completedAt = DateTime.UtcNow;
    }

    void OnWantedCompleted()
    {
        // Release write access before observers see completion. Seeding reopens these files read-only,
        // allowing Explorer, media players and checksum tools to use them immediately.
        _storage?.MarkComplete();
        SaveResume();
        _completed.TrySetResult();
        if (!_announcedCompleted) { _announcedCompleted = true; if (_announceKick.CurrentCount == 0) _announceKick.Release(); }
        if (!_engine.Options.SeedAfterCompletion) FinishSeeding();
    }

    void FinishSeeding()
    {
        _state = TorrentState.Finished;
        _completed.TrySetResult();
        var cts = _cts;
        _ = Task.Run(async () => { await Task.Delay(1500).ConfigureAwait(false); try { cts?.Cancel(); } catch (ObjectDisposedException) { } });   // let "completed" reach the tracker first
    }

    // ---------------------------------------------------------------- uploading and choking

    /// <summary>
    /// Serves an upload request on its own task. It used to run inside the peer's read loop, so whenever the upload
    /// limit made an upload wait, nothing could be received from that peer either - a tight upload limit throttled the
    /// download as well.
    /// </summary>
    async Task QueueUploadAsync(PeerState peer, byte[] payload, CancellationToken ct)
    {
        // A request must never be dropped: the other client keeps waiting for the block (other clients send hundreds
        // of requests at once). With a long backlog the read loop waits for it to drain instead - ordinary back-pressure.
        if (Volatile.Read(ref peer.PendingUploads) >= MaxPendingUploadsPerPeer)
        {
            Task tail;
            lock (peer.UploadGate) tail = peer.UploadTail;
            await tail.WaitAsync(ct).ConfigureAwait(false);
        }
        Interlocked.Increment(ref peer.PendingUploads);
        payload = payload.ToArray();   // the read loop may reuse its buffer for the next message before this one is served
        // One peer's requests are still answered in the order they arrived; they just no longer hold up its read loop.
        lock (peer.UploadGate) peer.UploadTail = ServeAfterAsync(peer.UploadTail, peer, payload, ct);
    }

    async Task ServeAfterAsync(Task previous, PeerState peer, byte[] payload, CancellationToken ct)
    {
        await previous.ConfigureAwait(false);   // never faults: every failure is handled below
        try { await Task.Run(() => HandleRequestAsync(peer, payload, ct), CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { peer.Connection.Dispose(); }   // a bad request or a broken connection ends this peer, as before
        finally { Interlocked.Decrement(ref peer.PendingUploads); }
    }

    async Task HandleRequestAsync(PeerState peer, byte[] payload, CancellationToken ct)
    {
        if (payload.Length != 12) throw new IOException("Bad request message.");
        var piece = PeerConnection.ReadInt(payload, 0); var offset = PeerConnection.ReadInt(payload, 4); var length = PeerConnection.ReadInt(payload, 8);
        long start;
        lock (_sync)
        {
            if (_meta == null || peer.AmChoking || (uint)piece >= (uint)_meta.PieceCount || !_have[piece]) return;
            if (offset < 0 || length <= 0 || length > 128 * 1024 || (long)offset + length > _meta.PieceSize(piece)) throw new IOException("A peer asked for an impossible block.");
            start = _meta.PieceOffset(piece) + offset;
        }
        await TokenBucket.WaitAllAsync(length, ct, _engine.UploadLimit, UploadLimit).ConfigureAwait(false);
        lock (_sync) { if (peer.AmChoking) return; }   // choked while it waited for the upload limit
        var message = new byte[8 + length];
        PeerConnection.U32(piece).CopyTo(message, 0); PeerConnection.U32(offset).CopyTo(message, 4);
        bool read;
        try { read = await Task.Run(() => _storage!.TryRead(start, message.AsSpan(8)), ct).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        if (!read) return;
        await peer.Connection.SendAsync(PeerConnection.Piece, message, ct).ConfigureAwait(false);
        Interlocked.Add(ref _uploadedTotal, length); _up.Add(length); peer.Up.Add(length);
    }

    async Task MaybeUnchokeAsync(PeerState peer, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!peer.AmChoking || _peers.Count(p => !p.AmChoking && p.PeerInterested) >= _engine.Options.UploadSlots) return;
            peer.AmChoking = false;
        }
        await peer.Connection.SendAsync(PeerConnection.Unchoke, ReadOnlySpan<byte>.Empty, ct).ConfigureAwait(false);
    }

    async Task RechokeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var unchoke = new List<PeerState>(); var choke = new List<PeerState>();
            lock (_sync)
            {
                var seeding = _state == TorrentState.Seeding;
                var interested = _peers.Where(p => p.PeerInterested).OrderByDescending(p => seeding ? p.Up.BytesPerSecond : p.Down.BytesPerSecond).ToList();
                var chosen = interested.Take(_engine.Options.UploadSlots).ToList();
                if (++_rechokeRound % 3 == 0)      // every third round one random interested peer gets a chance (optimistic unchoke)
                {
                    var rest = interested.Except(chosen).ToList();
                    if (rest.Count > 0) chosen.Add(rest[Random.Shared.Next(rest.Count)]);
                }
                foreach (var p in _peers)
                {
                    var shouldBeOpen = chosen.Contains(p);
                    if (shouldBeOpen && p.AmChoking) { p.AmChoking = false; unchoke.Add(p); }
                    else if (!shouldBeOpen && !p.AmChoking) { p.AmChoking = true; choke.Add(p); }
                }
            }
            foreach (var p in unchoke) _ = p.Connection.SendAsync(PeerConnection.Unchoke, ReadOnlySpan<byte>.Empty, ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            foreach (var p in choke) _ = p.Connection.SendAsync(PeerConnection.Choke, ReadOnlySpan<byte>.Empty, ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    // ---------------------------------------------------------------- housekeeping

    async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        var second = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            second++;
            var now = DateTime.UtcNow;
            foreach (var p in SnapshotPeers())
            {
                bool stalled;
                lock (_sync) stalled = p.Outstanding.Count > 0 && now - p.LastReceived > TimeSpan.FromSeconds(60);
                if (stalled) { p.Connection.Dispose(); continue; }
                if (second % 60 == 0) _ = p.Connection.SendKeepAliveAsync(ct).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
            }
            if (_meta == null) { await RequestMetadataFromAllAsync(ct).ConfigureAwait(false); continue; }
            if (_state == TorrentState.Downloading && second % 2 == 0) KickRequests();
            if (_state == TorrentState.Seeding && SeedingLimitReached()) { FinishSeeding(); }
            if (now - _lastResumeSave > TimeSpan.FromSeconds(30)) { _lastResumeSave = now; SaveResume(); }
        }
    }

    bool SeedingLimitReached()
    {
        var o = _engine.Options;
        if (o.SeedRatioLimit > 0 && Ratio >= o.SeedRatioLimit) return true;
        if (o.SeedTimeLimit > TimeSpan.Zero && _completedAt != default && DateTime.UtcNow - _completedAt >= o.SeedTimeLimit) return true;
        return false;
    }

    // ---------------------------------------------------------------- trackers and DHT

    IReadOnlyList<List<TrackerEntry>> Tiers()
    {
        lock (_sync) return _trackers.GroupBy(t => t.Tier).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
    }

    AnnounceRequest MakeAnnounce(string eventName)
    {
        long left;
        lock (_sync) left = _meta == null ? 1 : Math.Max(0, _wantedBytes - _haveWantedBytes);
        return new AnnounceRequest(_hash, _engine.PeerId, _engine.ListenPort, UploadedTotal, DownloadedTotal, left, eventName);
    }

    async Task AnnounceLoopAsync(CancellationToken ct)
    {
        var eventName = "started";
        while (!ct.IsCancellationRequested)
        {
            var interval = 60; var any = false;
            if (_announcedCompleted && eventName == "") eventName = "completed";
            // Every tier is asked at the same time. Asking them one after another meant each dead tracker cost a full
            // time-out before the next was even tried, which is most of why a torrent was slow to find its first peers.
            var announceEvent = eventName;
            foreach (var seconds in await Task.WhenAll(Tiers().Select(tier => AnnounceTierAsync(tier, announceEvent, ct))).ConfigureAwait(false))
            {
                if (seconds is not { } s) continue;
                interval = any ? Math.Min(interval, s) : s;
                any = true;
            }
            if (any && eventName is "started" or "completed") eventName = "";
            var wait = any ? TimeSpan.FromSeconds(Math.Max(interval, _engine.Options.MinAnnounceInterval.TotalSeconds)) : TimeSpan.FromSeconds(30);
            try { await _announceKick.WaitAsync(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        }
    }

    /// <summary>Announces to one tier: its trackers in order until one answers (BEP 12). Returns that tracker's interval, or null.</summary>
    async Task<int?> AnnounceTierAsync(List<TrackerEntry> tier, string eventName, CancellationToken ct)
    {
        foreach (var entry in tier.ToList())
        {
            try
            {
                var response = await TrackerClient.AnnounceAsync(entry.Url, MakeAnnounce(eventName), ct, _engine.Options.UdpTrackerTimeout).ConfigureAwait(false);
                lock (_sync) { entry.Status = response.Warning is { } w ? "Working (" + w + ")" : "Working"; entry.Seeders = response.Seeders; entry.Leechers = response.Leechers; entry.Last = DateTime.UtcNow; }
                foreach (var ep in response.Peers) AddCandidate(ep);
                lock (_sync) { tier.Remove(entry); tier.Insert(0, entry); }
                return response.IntervalSeconds;
            }
            catch (TrackerException ex) { lock (_sync) { entry.Status = ex.Message; entry.Last = DateTime.UtcNow; } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException) { lock (_sync) entry.Status = ex.Message; }
        }
        return null;
    }

    async Task AnnounceStoppedAsync()
    {
        foreach (var tier in Tiers())
        {
            var entry = tier.FirstOrDefault(t => t.Last != null);
            if (entry == null) continue;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await TrackerClient.AnnounceAsync(entry.Url, MakeAnnounce("stopped"), cts.Token, TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (Exception) { }
        }
    }

    async Task DhtLoopAsync(CancellationToken ct)
    {
        await Task.Delay(500, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            var dht = _engine.Dht;
            if (dht != null && !IsPrivate)
            {
                try { foreach (var ep in await dht.FindPeersAsync(_hash, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false)) AddCandidate(ep); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* offline: try again */ }
            }
            await Task.Delay(TimeSpan.FromSeconds(PeerCount < 5 ? 20 : 120), ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- extension protocol (BEP 10): metadata (BEP 9) and peer exchange (BEP 11)

    Task SendExtendedHandshakeAsync(PeerState peer, CancellationToken ct)
    {
        var d = new Dictionary<string, object>
        {
            ["m"] = new Dictionary<string, object> { ["ut_metadata"] = (long)OurUtMetadata, ["ut_pex"] = (long)OurUtPex },
            ["v"] = "Makan 16", ["reqq"] = 250L, ["p"] = (long)_engine.ListenPort
        };
        if (_meta != null) d["metadata_size"] = (long)_meta.RawInfo.Length;
        return peer.Connection.SendAsync(PeerConnection.Extended, new byte[] { 0 }.Concat(Bencode.Encode(d)).ToArray(), ct);
    }

    async Task HandleExtendedAsync(PeerState peer, byte[] payload, CancellationToken ct)
    {
        var extId = payload[0];
        if (extId == 0)
        {
            object hs;
            try { hs = Bencode.ParsePrefix(payload, 1, out _); } catch (FormatException) { return; }
            var m = Bencode.Dict(hs, "m");
            lock (_sync)
            {
                peer.UtMetadataId = (int)(Bencode.Long(m, "ut_metadata") ?? 0);
                peer.UtPexId = (int)(Bencode.Long(m, "ut_pex") ?? 0);
                peer.MetadataSize = (int)(Bencode.Long(hs, "metadata_size") ?? 0);
                peer.Reqq = (int)(Bencode.Long(hs, "reqq") ?? 250);
                if (Bencode.Text(hs, "v") is { Length: > 0 } v && peer.Client.Length == 0) peer.Client = v.Length > 40 ? v[..40] : v;
            }
            if (_meta == null && peer.UtMetadataId > 0) await RequestMetadataAsync(peer, ct).ConfigureAwait(false);
        }
        else if (extId == OurUtMetadata) await HandleMetadataMessageAsync(peer, payload, ct).ConfigureAwait(false);
        else if (extId == OurUtPex && _engine.Options.EnablePex && !IsPrivate)
        {
            try
            {
                var d = Bencode.ParsePrefix(payload, 1, out _);
                if (Bencode.Bytes(d, "added") is { } added) foreach (var ep in TrackerClient.ParseCompact(added).Take(50)) AddCandidate(ep);
            }
            catch (FormatException) { }
        }
    }

    async Task RequestMetadataFromAllAsync(CancellationToken ct)
    {
        foreach (var p in SnapshotPeers()) if (p.UtMetadataId > 0) await RequestMetadataAsync(p, ct).ConfigureAwait(false);
    }

    async Task RequestMetadataAsync(PeerState peer, CancellationToken ct)
    {
        var wanted = new List<int>();
        lock (_sync)
        {
            if (_meta != null) return;
            if (_metaBuffer == null)
            {
                if (peer.MetadataSize is <= 0 or > 8 * 1024 * 1024) return;
                _metaSize = peer.MetadataSize; _metaBuffer = new byte[_metaSize];
                _metaGot = new bool[(_metaSize + 16383) / 16384];
            }
            if (peer.MetadataSize != _metaSize) return;
            var now = DateTime.UtcNow;
            for (var i = 0; i < _metaGot.Length && wanted.Count < 4; i++)
                if (!_metaGot[i] && (!_metaAsked.TryGetValue(i, out var at) || now - at > TimeSpan.FromSeconds(8))) { _metaAsked[i] = now; wanted.Add(i); }
        }
        foreach (var piece in wanted)
        {
            var body = Bencode.Encode(new Dictionary<string, object> { ["msg_type"] = 0L, ["piece"] = (long)piece });
            await peer.Connection.SendAsync(PeerConnection.Extended, new byte[] { (byte)peer.UtMetadataId }.Concat(body).ToArray(), ct).ConfigureAwait(false);
        }
    }

    async Task HandleMetadataMessageAsync(PeerState peer, byte[] payload, CancellationToken ct)
    {
        object d; int used;
        try { d = Bencode.ParsePrefix(payload, 1, out used); } catch (FormatException) { return; }
        var type = Bencode.Long(d, "msg_type"); var piece = (int)(Bencode.Long(d, "piece") ?? -1);
        if (type == 0)      // somebody asks us for the metadata
        {
            byte[]? raw; lock (_sync) raw = _meta?.RawInfo;
            if (peer.UtMetadataId == 0) return;
            byte[] reply;
            if (raw == null || piece < 0 || piece * 16384 >= raw.Length)
                reply = Bencode.Encode(new Dictionary<string, object> { ["msg_type"] = 2L, ["piece"] = (long)piece });
            else
            {
                var chunk = raw.AsSpan(piece * 16384, Math.Min(16384, raw.Length - piece * 16384)).ToArray();
                reply = Bencode.Encode(new Dictionary<string, object> { ["msg_type"] = 1L, ["piece"] = (long)piece, ["total_size"] = (long)raw.Length }).Concat(chunk).ToArray();
            }
            await peer.Connection.SendAsync(PeerConnection.Extended, new byte[] { (byte)peer.UtMetadataId }.Concat(reply).ToArray(), ct).ConfigureAwait(false);
            return;
        }
        if (type != 1) return;
        MetaInfo? built = null;
        lock (_sync)
        {
            if (_meta != null || _metaBuffer == null || piece < 0 || piece >= _metaGot.Length || _metaGot[piece]) return;
            var dataLength = payload.Length - 1 - used;
            var expected = Math.Min(16384, _metaSize - piece * 16384);
            if (dataLength != expected) return;
            Buffer.BlockCopy(payload, 1 + used, _metaBuffer, piece * 16384, dataLength);
            _metaGot[piece] = true;
            if (_metaGot.All(x => x))
            {
                if (SHA1.HashData(_metaBuffer).AsSpan().SequenceEqual(_hash))
                {
                    try { built = MetaInfo.FromInfo(_metaBuffer, _trackers.GroupBy(t => t.Tier).OrderBy(g => g.Key).Select(g => (IReadOnlyList<string>)g.Select(t => t.Url).ToList()).ToList()); }
                    catch (InvalidDataException ex) { _error = ex.Message; }
                }
                if (built == null) { Array.Clear(_metaGot); _metaAsked.Clear(); _metaBuffer = null; }      // wrong data: ask again
            }
        }
        if (built != null)
        {
            lock (_sync) SetMeta(built, alreadyKnown: true);
            _engine.SaveTorrentFile(built);
        }
    }
}
