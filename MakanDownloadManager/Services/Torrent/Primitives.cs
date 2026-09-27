using System.Diagnostics;
using System.Security.Cryptography;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>Which pieces a peer (or we) have.</summary>
public sealed class Bitfield
{
    readonly bool[] _bits;
    public int Length => _bits.Length;
    public int Count { get; private set; }
    public Bitfield(int length) => _bits = new bool[length];
    public bool this[int index] => (uint)index < (uint)_bits.Length && _bits[index];
    public bool IsComplete => Count == _bits.Length;

    public void Set(int index, bool value = true)
    {
        if ((uint)index >= (uint)_bits.Length || _bits[index] == value) return;
        _bits[index] = value;
        Count += value ? 1 : -1;
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[(_bits.Length + 7) / 8];
        for (var i = 0; i < _bits.Length; i++) if (_bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    /// <summary>Loads the wire format. False when the length is wrong or bits beyond the last piece are set.</summary>
    public bool TryLoad(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != (_bits.Length + 7) / 8) return false;
        for (var i = _bits.Length; i < bytes.Length * 8; i++) if ((bytes[i / 8] & (0x80 >> (i % 8))) != 0) return false;
        Count = 0;
        for (var i = 0; i < _bits.Length; i++) { _bits[i] = (bytes[i / 8] & (0x80 >> (i % 8))) != 0; if (_bits[i]) Count++; }
        return true;
    }

    public Bitfield Clone() { var c = new Bitfield(_bits.Length); Array.Copy(_bits, c._bits, _bits.Length); c.Count = Count; return c; }
}

/// <summary>Bytes per second over the last few seconds.</summary>
public sealed class RateMeter
{
    const int Buckets = 6;
    readonly long[] _bytes = new long[Buckets];
    long _lastSecond = Environment.TickCount64 / 1000;
    readonly object _gate = new();
    public long Total { get; private set; }

    public void Add(long count)
    {
        lock (_gate) { Advance(); _bytes[_lastSecond % Buckets] += count; Total += count; }
    }

    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                Advance();
                long sum = 0;
                for (var i = 1; i < Buckets; i++) sum += _bytes[(_lastSecond - i) % Buckets];   // the last five complete seconds
                return sum / (Buckets - 1);
            }
        }
    }

    void Advance()
    {
        var now = Environment.TickCount64 / 1000;
        if (now == _lastSecond) return;
        for (var s = _lastSecond + 1; s <= now && s - _lastSecond <= Buckets; s++) _bytes[s % Buckets] = 0;
        _lastSecond = now;
    }
}

/// <summary>Speed limit (bytes per second, 0 = unlimited) that can be changed while transfers run.</summary>
public sealed class TokenBucket
{
    long _rate;
    double _tokens;
    long _last = Stopwatch.GetTimestamp();
    readonly object _gate = new();

    public long Rate { get => Interlocked.Read(ref _rate); set => Interlocked.Exchange(ref _rate, Math.Max(0, value)); }

    /// <summary>Waits as long as the limit requires before <paramref name="bytes"/> may be moved.</summary>
    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        var rate = Rate;
        if (rate <= 0) return;
        double delay;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            // at most a quarter of a second may be saved up, so a limit holds even for short transfers
            _tokens = Math.Min(Math.Max(rate / 4.0, 65536), _tokens + (now - _last) / (double)Stopwatch.Frequency * rate);
            _last = now;
            _tokens -= bytes;
            delay = _tokens < 0 ? -_tokens / rate : 0;
        }
        if (delay > 0.002) await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);
    }

    public static async Task WaitAllAsync(int bytes, CancellationToken ct, params TokenBucket?[] buckets)
    {
        foreach (var bucket in buckets) if (bucket != null) await bucket.WaitAsync(bytes, ct).ConfigureAwait(false);
    }
}

/// <summary>The files of one torrent on disk. Reads and writes are addressed by position in the torrent's single byte stream.</summary>
public sealed class TorrentStorage : IDisposable
{
    readonly MetaInfo _meta;
    readonly string _root;
    readonly FileStream?[] _streams;
    readonly object _gate = new();

    /// <param name="saveDirectory">The folder chosen by the user; a multi-file torrent gets its own sub-folder named after the torrent.</param>
    public TorrentStorage(MetaInfo meta, string saveDirectory)
    {
        _meta = meta;
        _root = meta.IsMultiFile ? Path.Combine(saveDirectory, meta.Name) : saveDirectory;
        _streams = new FileStream?[meta.Files.Count];
    }

    /// <summary>The file (single-file torrent) or folder (multi-file torrent) the user will see.</summary>
    public string ContentPath => _meta.IsMultiFile ? _root : Path.Combine(_root, _meta.Files[0].Path);

    public string FilePath(TorrentFile file)
    {
        var full = Path.GetFullPath(Path.Combine(_root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A file of the torrent would be written outside its folder.");
        return full;
    }

    FileStream Open(TorrentFile file)
    {
        var stream = _streams[file.Index];
        if (stream != null) return stream;
        var path = FilePath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        if (stream.Length < file.Length) stream.SetLength(file.Length);
        _streams[file.Index] = stream;
        return stream;
    }

    /// <summary>Index of the first file that contains this position.</summary>
    int FileAt(long offset)
    {
        int lo = 0, hi = _meta.Files.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_meta.Files[mid].Offset <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            var i = FileAt(offset);
            while (!data.IsEmpty && i < _meta.Files.Count)
            {
                var file = _meta.Files[i];
                if (file.Length == 0) { i++; continue; }
                var inFile = offset - file.Offset;
                var count = (int)Math.Min(data.Length, file.Length - inFile);
                if (count > 0)
                {
                    var stream = Open(file);
                    stream.Position = inFile;
                    stream.Write(data[..count]);
                    data = data[count..]; offset += count;
                }
                i++;
            }
        }
    }

    /// <summary>Reads what exists; false when a file is missing or too short.</summary>
    public bool TryRead(long offset, Span<byte> destination)
    {
        lock (_gate)
        {
            var i = FileAt(offset);
            while (!destination.IsEmpty && i < _meta.Files.Count)
            {
                var file = _meta.Files[i];
                if (file.Length == 0) { i++; continue; }
                var inFile = offset - file.Offset;
                var count = (int)Math.Min(destination.Length, file.Length - inFile);
                if (count > 0)
                {
                    FileStream? stream = _streams[file.Index];
                    if (stream == null)
                    {
                        var path = FilePath(file);
                        if (!File.Exists(path) || new FileInfo(path).Length < file.Length) return false;
                        stream = Open(file);
                    }
                    stream.Position = inFile;
                    if (stream.Read(destination[..count]) != count) return false;
                    destination = destination[count..]; offset += count;
                }
                i++;
            }
            return destination.IsEmpty;
        }
    }

    /// <summary>Do all files exist with their full size (a finished or pre-allocated download)?</summary>
    public bool FilesLookComplete(Func<TorrentFile, bool> wanted)
    {
        foreach (var file in _meta.Files)
        {
            if (!wanted(file)) continue;
            var path = FilePath(file);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length) return false;
        }
        return true;
    }

    /// <summary>Hashes every piece that can be read: what is already on disk counts, the rest is downloaded.</summary>
    public Bitfield Check(Func<int, bool>? progress, CancellationToken ct)
    {
        var have = new Bitfield(_meta.PieceCount);
        using var sha = SHA1.Create();
        var buffer = new byte[_meta.PieceLength];
        for (var piece = 0; piece < _meta.PieceCount; piece++)
        {
            ct.ThrowIfCancellationRequested();
            var size = _meta.PieceSize(piece);
            if (TryRead(_meta.PieceOffset(piece), buffer.AsSpan(0, size)) && sha.ComputeHash(buffer, 0, size).AsSpan().SequenceEqual(_meta.PieceHash(piece))) have.Set(piece);
            if (progress != null && !progress(piece + 1)) break;
        }
        return have;
    }

    public void Delete()
    {
        Dispose();
        foreach (var file in _meta.Files)
            try { var p = FilePath(file); if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (_meta.IsMultiFile)
            try { DeleteEmptyFolders(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void DeleteEmptyFolders(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var sub in Directory.GetDirectories(dir)) DeleteEmptyFolders(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    public void Flush() { lock (_gate) foreach (var s in _streams) try { s?.Flush(); } catch (IOException) { } }

    public void Dispose()
    {
        lock (_gate)
            for (var i = 0; i < _streams.Length; i++) { try { _streams[i]?.Dispose(); } catch (IOException) { } _streams[i] = null; }
    }
}
