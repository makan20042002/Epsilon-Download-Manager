using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

public sealed record ProbeResult(
    bool AcceptRanges, long? Length, HttpStatusCode Status, string? ETag, string? LastModified,
    string? FileName, string? ContentType, Uri FinalUri);

public sealed partial class DownloadManager
{
    const int BufferSize = 256 * 1024;
    static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);
    /// <summary>How often a segmented download flushes its data and saves its progress map.</summary>
    internal static TimeSpan MapSaveInterval { get; set; } = TimeSpan.FromSeconds(15);

    // ---------------------------------------------------------------- probing

    /// <summary>
    /// Learns size, range support and the real file name with a single tiny "Range: bytes=0-0" GET
    /// (falling back to HEAD for servers that reject that). Throws HttpRequestException with a StatusCode on HTTP errors.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(DownloadItem item, CancellationToken ct)
    {
        using (var request = BuildRequest(HttpMethod.Get, item))
        {
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var total = response.Content.Headers.ContentRange?.Length;
                return Describe(response, total, total.HasValue);
            }
            if (response.IsSuccessStatusCode)
                return Describe(response, response.Content.Headers.ContentLength, false); // server ignored Range
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Zero-byte file (Content-Range: bytes */0) or a server that dislikes Range on this URL.
                if (response.Content.Headers.ContentRange?.Length == 0) return Describe(response, 0, false);
            }
            else if (code is not (400 or 405 or 501))
            {
                throw RetryPolicy.StatusError(response);
            }
        }

        using var headRequest = BuildRequest(HttpMethod.Head, item);
        using var head = await _http.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!head.IsSuccessStatusCode) throw RetryPolicy.StatusError(head);
        var ranges = head.Headers.AcceptRanges.Any(x => string.Equals(x, "bytes", StringComparison.OrdinalIgnoreCase));
        return Describe(head, head.Content.Headers.ContentLength, ranges);
    }

    static ProbeResult Describe(HttpResponseMessage r, long? length, bool ranges)
    {
        var final = r.RequestMessage?.RequestUri ?? new Uri("http://unknown/");
        // A body that is content-encoded has an unreliable length and cannot be range-split safely.
        if (r.Content.Headers.ContentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
        {
            length = null;
            ranges = false;
        }
        return new ProbeResult(ranges, length, r.StatusCode, r.Headers.ETag?.ToString(), r.Content.Headers.LastModified?.ToString(),
            DownloadFileNamer.FromResponse(r.Content.Headers, final), r.Content.Headers.ContentType?.MediaType, final);
    }

    // ---------------------------------------------------------------- orchestration

    async Task DownloadAsync(DownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!);
        item.DiskLoadedBytes = 0;
        if (IsTorrentUrl(item.Url)) { await TorrentDownloadAsync(item, ct); return; }
        if (YtDlpService.TryGetSelection(item.Url, out var ytPage, out var ytKey)) { await YtDlpDownloadAsync(item, ytPage, ytKey, ct); return; }
        if (IsHlsItem(item)) { await HlsAsync(item, ct); return; }
        var probe = await ProbeAsync(item, ct);
        Live(item).ResumeSupported = probe.AcceptRanges;

        if (HasRemoteChanged(item, probe))
        {
            DeletePartialFiles(item);
            item.DoneBytes = 0;
        }
        ApplyProbe(item, probe);
        _store.Save(item);
        DownloadStateManifest.Write(item);

        // ChooseConnections already applies the learned per-server connection limit. Applying it a
        // second time here could halve the worker count twice and make an otherwise healthy server
        // unexpectedly slow after one throttling response.
        var workers = ChooseConnections(item, probe);
        string temp;
        var useSegments = item.TotalBytes is > 0 && probe.AcceptRanges && (workers > 1 || File.Exists(SegMapPath(item)));
        if (useSegments)
        {
            try { temp = await SegmentedAsync(item, item.TotalBytes!.Value, workers, ct); }
            catch (RangeNotSupportedException)
            {
                DeleteSegmentFiles(item);
                temp = await SingleAsync(item, ct);
            }
        }
        else
        {
            DeleteSegmentFiles(item);
            temp = await SingleAsync(item, ct);
        }

        try { await VerifyAsync(item, temp, ct); }
        catch (InvalidDataException) { DeletePartialFiles(item); throw; }

        var finalLength = new FileInfo(temp).Length;
        if (finalLength == 0 && (item.TotalBytes ?? 0) > 0)
        {
            DeletePartialFiles(item);
            throw new InvalidDataException("Downloaded file is empty.");
        }
        item.TotalBytes = finalLength;

        AtomicReplace(temp, item.FilePath);
        DownloadStateManifest.Delete(item);
        DeleteSegmentFiles(item);
        if (SetFileDateFromServer && DateTimeOffset.TryParse(item.LastModified, out var serverDate))
        {
            try { File.SetLastWriteTimeUtc(item.FilePath, serverDate.UtcDateTime); File.SetCreationTimeUtc(item.FilePath, serverDate.UtcDateTime); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        item.DoneBytes = finalLength;
        item.TotalBytes ??= finalLength;
    }

    void ApplyProbe(DownloadItem item, ProbeResult probe)
    {
        item.StatusCode = (int)probe.Status;
        item.TotalBytes = probe.Length;
        if (!string.IsNullOrWhiteSpace(probe.ETag)) item.ETag = probe.ETag;
        if (!string.IsNullOrWhiteSpace(probe.LastModified)) item.LastModified = probe.LastModified;

        // First start of a download whose name was only guessed from the URL: adopt the server's real name.
        if (item.AutoName && item.DoneBytes == 0 && !PartialFilesExist(item))
        {
            item.AutoName = false;
            if (!string.IsNullOrWhiteSpace(probe.FileName) &&
                !string.Equals(probe.FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!;
                var target = DownloadFileNamer.MakeUnique(Path.Combine(dir, probe.FileName), p => IsPathClaimed(item, p));
                item.FilePath = target;
                item.Category = CategoryService.For(target);
            }
        }
    }

    int ChooseConnections(DownloadItem item, ProbeResult probe)
    {
        if (!probe.AcceptRanges || probe.Length is null || probe.Length < 2 * 1024 * 1024) return 1;
        var configured = Math.Clamp(item.Connections > 0 ? item.Connections : DefaultConnections, 1, MaxConnectionsPerDownload);
        configured = SuggestConnections(item, configured);
        var size = probe.Length.Value;
        var adaptive = size >= 1L << 30 ? 16 : size >= 256L << 20 ? 8 : size >= 32L << 20 ? 4 : 2;
        return Math.Clamp(Math.Min(configured, adaptive), 1, MaxConnectionsPerDownload);
    }

    static string Norm(string? etag) => (etag ?? "").Trim().Replace("W/", "", StringComparison.Ordinal);

    bool HasRemoteChanged(DownloadItem item, ProbeResult p)
    {
        if (!PartialFilesExist(item)) return false;
        if (item.TotalBytes is { } old && p.Length is { } now && old != now) return true;
        if (IgnoreModifiedOnResume) return false;   // only a different size counts as "the file changed"
        if (!string.IsNullOrWhiteSpace(item.ETag) && !string.IsNullOrWhiteSpace(p.ETag))
            return !string.Equals(Norm(item.ETag), Norm(p.ETag), StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(item.LastModified) && !string.IsNullOrWhiteSpace(p.LastModified))
            return !string.Equals(item.LastModified, p.LastModified, StringComparison.Ordinal);
        return false;
    }

    // ---------------------------------------------------------------- single stream

    async Task<string> SingleAsync(DownloadItem item, CancellationToken ct)
    {
        var part = PartBase(item) + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (item.TotalBytes is { } total && have > total) { File.Delete(part); have = 0; }
        item.DiskLoadedBytes = have;
        if (item.TotalBytes is > 0 && have == item.TotalBytes) return part;

        for (var pass = 0; ; pass++)
        {
            using var request = BuildRequest(HttpMethod.Get, item);
            if (have > 0)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
                AddIfRange(request, item);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && pass == 0)
            {
                File.Delete(part);
                have = 0;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw RetryPolicy.StatusError(response);
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                // Server ignored our Range header and is sending the whole file again.
                File.Delete(part);
                have = 0;
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (!item.TotalBytes.HasValue && contentLength.HasValue) item.TotalBytes = have + contentLength.Value;
            if (item.TotalBytes.HasValue && contentLength.HasValue && have + contentLength.Value > item.TotalBytes.Value)
                throw new InvalidDataException("Server returned more data than expected.");

            item.ActiveConnections = 1;
            var live = Live(item); live.Workers = 1; live.Info[0] = "Receiving data...";
            var tracker = new ProgressTracker(have);
            await WithReporter(item, tracker, async () =>
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        var read = await ReadWithIdleTimeoutAsync(input, buffer.AsMemory(0, BufferSize), ct);
                        if (read == 0) break;
                        await AcquireBandwidthAsync(item, read, ct);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        tracker.Add(read);
                        Interlocked.Add(ref live.Bytes[0], read);
                    }
                    live.Info[0] = "Download complete.";
                    await output.FlushAsync(ct);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                return 0;
            });

            if (item.TotalBytes is { } expected && new FileInfo(part).Length < expected)
                throw new IOException("Connection closed before the file was complete.");
            return part;
        }
    }

    // ---------------------------------------------------------------- segmented (chunk queue)

    sealed class ChunkMap
    {
        public long Total { get; set; }
        public int ChunkSize { get; set; }
        public long[] Done { get; set; } = Array.Empty<long>();
        [JsonIgnore] public int Count => Done.Length;
        public long StartOf(int i) => (long)i * ChunkSize;
        public long LengthOf(int i) => Math.Min(ChunkSize, Total - StartOf(i));
    }

    /// <summary>
    /// Path prefix of a download's partial files: the final file's own path, or (when a temp directory is set) "&lt;temp&gt;\&lt;id&gt;".
    /// A download that already has parts next to its target keeps using them, so changing the setting never orphans a running download.
    /// </summary>
    string PartBase(DownloadItem item)
    {
        var dir = TempDirectory;
        if (string.IsNullOrWhiteSpace(dir) || item.Id == 0) return item.FilePath;
        var legacy = item.FilePath;
        if (File.Exists(legacy + ".part") || File.Exists(legacy + ".seg") || File.Exists(legacy + ".seg.map") || File.Exists(legacy + ".hls.json")) return legacy;
        try { Directory.CreateDirectory(dir); } catch (Exception) { return item.FilePath; }
        return Path.Combine(dir, item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    string SegPath(DownloadItem item) => PartBase(item) + ".seg";
    string SegMapPath(DownloadItem item) => PartBase(item) + ".seg.map";

    static int ChooseChunkSize(long total, int workers)
    {
        // Keep several small ranges available per worker.  A large fixed tail used to leave one
        // connection doing the final 64 MiB while every other connection sat idle.
        var target = total / Math.Max(1, workers * 16);
        var clamped = Math.Clamp(target, 1L * 1024 * 1024, 16L * 1024 * 1024);
        return (int)(clamped / 65536 * 65536);
    }

    static ChunkMap? LoadMap(string path, long total)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var map = JsonSerializer.Deserialize<ChunkMap>(File.ReadAllText(path));
            if (map == null || map.Total != total || map.ChunkSize <= 0) return null;
            var expected = (int)((total + map.ChunkSize - 1) / map.ChunkSize);
            if (map.Done.Length != expected) return null;
            for (var i = 0; i < expected; i++) map.Done[i] = Math.Clamp(map.Done[i], 0, map.LengthOf(i));
            return map;
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes the progress map so that it never claims more than the disk really holds: the counters are copied first,
    /// then the data file is flushed, and only that copy is saved. A power cut or system crash can therefore lose at
    /// most the last few seconds of progress, but can never leave a "finished" range that was still only in memory
    /// (which used to produce a silently corrupt file after resuming).
    /// </summary>
    static void SaveMap(Microsoft.Win32.SafeHandles.SafeFileHandle handle, ChunkMap map, string path)
    {
        lock (map)
        {
            var durable = new long[map.Done.Length];
            for (var i = 0; i < durable.Length; i++) durable[i] = Volatile.Read(ref map.Done[i]);
            RandomAccess.FlushToDisk(handle);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new ChunkMap { Total = map.Total, ChunkSize = map.ChunkSize, Done = durable }));
            File.Move(tmp, path, true);
        }
    }

    async Task<string> SegmentedAsync(DownloadItem item, long total, int workers, CancellationToken ct)
    {
        var data = SegPath(item);
        var mapPath = SegMapPath(item);
        workers = Math.Clamp(workers, 1, MaxConnectionsPerDownload);

        // Multi-Network (optional): spread the connections over every connected network so their speeds add up.
        var links = MultiNetworkEnabled ? CurrentLinks() : Array.Empty<NetworkLink>();
        if (links.Count < 2) links = Array.Empty<NetworkLink>();
        if (links.Count > 0) workers = Math.Clamp(Math.Max(workers, links.Count), 1, MaxConnectionsPerDownload);
        // Parallel ranges only add speed when each one has its own TCP connection. Over HTTP/2 they would all share a
        // single connection, so ranges ask for HTTP/1.1 (switched back if a server turns out to refuse it).
        var separateConnections = workers > 1;

        var loaded = LoadMap(mapPath, total);
        if (loaded == null || !File.Exists(data) || new FileInfo(data).Length != total)
        {
            TryDelete(data); TryDelete(mapPath);
            var size = ChooseChunkSize(total, workers);
            loaded = new ChunkMap { Total = total, ChunkSize = size, Done = new long[(int)((total + size - 1) / size)] };
        }
        var map = loaded;
        item.DiskLoadedBytes = map.Done.Sum();
        var live = Live(item); live.Map = map; live.Workers = workers;
        live.LinkBytes = new long[links.Count]; live.LinkFailures = new int[links.Count]; live.Links = links.Count > 0 ? links : null;
        for (var w = 0; w < workers; w++) live.Info[w] = "Send GET...";

        // Preallocating up front reserves the disk space (failing early if the disk is full) and avoids fragmentation.
        using var handle = File.Exists(data)
            ? File.OpenHandle(data, FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous)
            : File.OpenHandle(data, FileMode.CreateNew, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous, total);
        if (RandomAccess.GetLength(handle) != total) RandomAccess.SetLength(handle, total);

        var tracker = new ProgressTracker(map.Done.Sum());
        var pending = new System.Collections.Concurrent.ConcurrentQueue<int>(
            Enumerable.Range(0, map.Count).Where(i => map.Done[i] < map.LengthOf(i)));
        var allowed = workers;
        Exception? fatal = null;

        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = workCts.Token;
        item.ActiveConnections = Math.Min(workers, Math.Max(1, pending.Count));

        await WithReporter(item, tracker, async () =>
        {
            using var persistStop = new CancellationTokenSource();
            var persist = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        // The flush is the expensive part on slow disks, so it stays periodic; the map is only ever
                        // written together with it (see SaveMap).
                        await Task.Delay(MapSaveInterval, persistStop.Token);
                        SaveMap(handle, map, mapPath);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _diagnostics.Error("Could not persist segment map.", ex); }
            });

            var tasks = Enumerable.Range(0, workers).Select(index => Task.Run(async () =>
            {
                try { await WorkerAsync(index); }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref fatal, ex, null);
                    workCts.Cancel();
                }
            })).ToArray();

            await Task.WhenAll(tasks);
            persistStop.Cancel();
            await persist;
            try { SaveMap(handle, map, mapPath); } catch (Exception ex) { _diagnostics.Error("Could not save segment map.", ex); }
            return 0;
        });

        ct.ThrowIfCancellationRequested();
        if (fatal != null) ExceptionDispatchInfo.Capture(fatal).Throw();
        for (var i = 0; i < map.Count; i++)
            if (map.Done[i] != map.LengthOf(i)) throw new IOException("Download finished with missing segments.");
        return data;

        async Task WorkerAsync(int index)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (!work.IsCancellationRequested)
                {
                    // After a 429/503 the pool shrinks: surplus workers retire once their current chunk is done.
                    if (index >= Volatile.Read(ref allowed)) { live.Info[index] = "Download complete."; return; }
                    if (!pending.TryDequeue(out var chunk)) { live.Info[index] = "Download complete."; return; }
                    live.Info[index] = "Send GET...";
                    await DownloadChunkWithRetryAsync(chunk, index, buffer);
                    live.Info[index] = "Disconnect.";
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        async Task DownloadChunkWithRetryAsync(int chunk, int workerIndex, byte[] buffer)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { await DownloadChunkOnceAsync(chunk, workerIndex, buffer); return; }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { throw; }
                catch (LinkFailedException) { attempt--; }   // one network had a problem: try again at once, it does not count against the download
                catch (RangeNotSupportedException) { throw; }
                catch (Exception ex) when (attempt < 5 && RetryPolicy.IsTransient(ex))
                {
                    if (RetryPolicy.IsThrottle(ex))
                    {
                        var now = Volatile.Read(ref allowed);
                        if (now > 1) Interlocked.CompareExchange(ref allowed, Math.Max(1, now / 2), now);
                        item.ActiveConnections = Volatile.Read(ref allowed);
                    }
                    await Task.Delay(RetryPolicy.DelayFor(ex, attempt), work);
                }
            }
        }

        async Task DownloadChunkOnceAsync(int chunk, int workerIndex, byte[] buffer)
        {
            var start = map.StartOf(chunk);
            var length = map.LengthOf(chunk);
            if (Volatile.Read(ref map.Done[chunk]) >= length) return;

            // Which network carries this request: each worker keeps to one network until that network has failed too often.
            var link = links.Count > 0 ? workerIndex % links.Count : -1;
            if (link >= 0 && Volatile.Read(ref live.LinkFailures[link]) >= LinkFailureLimit) link = -1;
            try { await FetchAsync(link >= 0 ? ClientFor(links[link]) : _http); }
            catch (Exception ex) when (link >= 0 && !work.IsCancellationRequested)
            {
                // Anything that goes wrong on one particular network (unplugged, a sign-in page, a link that only works
                // from the address it was created for) must not fail the download: after a few failures this
                // download simply stops using that network.
                if (Interlocked.Increment(ref live.LinkFailures[link]) == LinkFailureLimit)
                    _diagnostics.Error($"Multi-Network: download #{item.Id} stopped using {links[link].Kind} after repeated errors", ex);
                throw new LinkFailedException(ex.Message, ex);
            }

            async Task FetchAsync(HttpClient http)
            {
            var have = Volatile.Read(ref map.Done[chunk]);
            if (have >= length) return;
            var from = start + have;
            var to = start + length - 1;
            using var request = BuildRequest(HttpMethod.Get, item);
            if (Volatile.Read(ref separateConnections)) { request.Version = HttpVersion.Version11; request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower; }
            request.Headers.Range = new RangeHeaderValue(from, to);
            AddIfRange(request, item);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, work); }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.VersionNegotiationError)
            {
                Volatile.Write(ref separateConnections, false);   // an HTTP/2-only server: the retry uses HTTP/2 again
                throw;
            }
            using var responseScope = response;

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                // 200 = server ignored Range (or the file changed under If-Range); 416 = our ranges no longer fit.
                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new RangeNotSupportedException();
                throw RetryPolicy.StatusError(response);
            }
            var range = response.Content.Headers.ContentRange;
            if (range?.From is { } gotFrom && gotFrom != from) throw new RangeNotSupportedException();

            await using var input = await response.Content.ReadAsStreamAsync(work);
            live.Info[workerIndex] = "Receiving data...";
            var remaining = length - have;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await ReadWithIdleTimeoutAsync(input, buffer.AsMemory(0, want), work);
                if (read == 0) throw new IOException("Connection closed before the segment was complete.");
                await AcquireBandwidthAsync(item, read, work);
                var written = Volatile.Read(ref map.Done[chunk]);
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), start + written, work);
                Volatile.Write(ref map.Done[chunk], written + read); // one worker owns a chunk at a time
                tracker.Add(read);
                Interlocked.Add(ref live.Bytes[workerIndex], read);
                if (link >= 0) Interlocked.Add(ref live.LinkBytes[link], read);
                remaining -= read;
            }
            }
        }
    }

    // ---------------------------------------------------------------- progress

    sealed class ProgressTracker
    {
        long _done;
        readonly Queue<(long Ticks, long Done)> _samples = new();
        public ProgressTracker(long initial) { _done = initial; }
        public long Done => Interlocked.Read(ref _done);
        public void Add(long bytes) => Interlocked.Add(ref _done, bytes);

        /// <summary>Called from the single reporter loop: speed over roughly the last four seconds.</summary>
        public long Sample()
        {
            var now = Stopwatch.GetTimestamp();
            var done = Done;
            _samples.Enqueue((now, done));
            while (_samples.Count > 1 && now - _samples.Peek().Ticks > 4 * Stopwatch.Frequency) _samples.Dequeue();
            var first = _samples.Peek();
            var seconds = (now - first.Ticks) / (double)Stopwatch.Frequency;
            return seconds < 0.2 ? 0 : (long)((done - first.Done) / seconds);
        }
    }

    async Task<T> WithReporter<T>(DownloadItem item, ProgressTracker tracker, Func<Task<T>> body)
    {
        using var stop = new CancellationTokenSource();
        item.DoneBytes = tracker.Done;
        var reporter = Task.Run(async () =>
        {
            var lastSave = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    await Task.Delay(500, stop.Token).ConfigureAwait(false);
                    Report(item, tracker.Done, tracker.Sample());
                    if (lastSave.ElapsedMilliseconds >= 2000)
                    {
                        lastSave.Restart();
                        try
                        {
                            _store.Save(item);
                            DownloadStateManifest.Write(item);
                        }
                        catch (Exception ex) { _diagnostics.Error("Progress save failed.", ex); }
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { return await body(); }
        finally
        {
            stop.Cancel();
            await reporter.ConfigureAwait(false);
            Report(item, tracker.Done, 0);
        }
    }

    void Report(DownloadItem item, long done, long speed)
    {
        item.DoneBytes = done;
        var total = item.TotalBytes;
        item.Progress = total is > 0 ? Math.Min(100.0, done * 100.0 / total.Value) : 0;
        item.SpeedText = $"{Format(speed)}/s"; item.SpeedBytesPerSec = speed;
        item.SizeText = FormatSize(item);
        item.EtaText = speed > 0 && total.HasValue
            ? FormatEta(TimeSpan.FromSeconds(Math.Max(0, (total.Value - done) / (double)speed)))
            : "—";
        Progress?.Invoke(item, new DownloadProgress(item.Progress, item.SpeedText, item.SizeText, item.Status, done, total));
    }

    // ---------------------------------------------------------------- what the progress window shows

    sealed class LiveState
    {
        public volatile ChunkMap? Map;
        public readonly long[] Bytes = new long[MaxConnectionsPerDownload];
        public readonly string[] Info = new string[MaxConnectionsPerDownload];
        public volatile int Workers = 1;
        public bool? ResumeSupported;
        /// <summary>Multi-Network: the networks this download is spread over (null = the ordinary single route).</summary>
        public volatile IReadOnlyList<NetworkLink>? Links;
        public long[] LinkBytes = Array.Empty<long>();
        public int[] LinkFailures = Array.Empty<int>();
        public LiveState() { for (var i = 0; i < Info.Length; i++) Info[i] = ""; }
    }

    LiveState Live(DownloadItem item) => _live.GetOrAdd(item.Id, _ => new LiveState());

    /// <summary>One row per connection: how much it has fetched and what it is doing (IDM's "Disconnect." / "Receiving data...").</summary>
    public IReadOnlyList<ConnectionInfo> GetConnections(DownloadItem item)
    {
        if (TorrentConnections(item) is { } torrentRows) return torrentRows;
        if (!_live.TryGetValue(item.Id, out var live)) return Array.Empty<ConnectionInfo>();
        var rows = new List<ConnectionInfo>();
        for (var i = 0; i < Math.Min(live.Workers, live.Bytes.Length); i++) rows.Add(new ConnectionInfo(i + 1, Interlocked.Read(ref live.Bytes[i]), live.Info[i]));
        return rows;
    }

    /// <summary>How much of each part of the file is done (0..1 per bucket): IDM's "start positions and download progress by connections".</summary>
    public double[] GetPositionMap(DownloadItem item, int buckets)
    {
        var result = new double[Math.Max(1, buckets)];
        if (_torrentSessions.TryGetValue(item.Id, out var torrent)) return torrent.PieceMap(buckets);
        if (_live.TryGetValue(item.Id, out var live) && live.Map is { } map && map.Total > 0)
        {
            for (var b = 0; b < result.Length; b++)
            {
                long from = map.Total * b / result.Length, to = map.Total * (b + 1) / result.Length;
                if (to <= from) continue;
                long doneBytes = 0;
                var c0 = (int)(from / map.ChunkSize); var c1 = Math.Min(map.Count - 1, (int)((to - 1) / map.ChunkSize));
                for (var c = c0; c <= c1; c++)
                {
                    long start = map.StartOf(c), done = Volatile.Read(ref map.Done[c]);
                    var lo = Math.Max(from, start); var hi = Math.Min(to, start + done);
                    if (hi > lo) doneBytes += hi - lo;
                }
                result[b] = doneBytes / (double)(to - from);
            }
            return result;
        }
        var fraction = Math.Clamp(item.Progress / 100.0, 0, 1);       // no chunk map (single connection, stream): a simple bar
        for (var b = 0; b < result.Length; b++) result[b] = Math.Clamp(fraction * result.Length - b, 0, 1);
        return result;
    }

    /// <summary>Does the server allow resuming (null = not known yet)?</summary>
    public bool? SupportsResume(DownloadItem item) => _live.TryGetValue(item.Id, out var live) ? live.ResumeSupported : null;

    // ---------------------------------------------------------------- yt-dlp (YouTube and other protected sites)

    async Task YtDlpDownloadAsync(DownloadItem item, string pageUrl, string key, CancellationToken ct)
    {
        var yt = YtDlp ?? throw new InvalidOperationException("yt-dlp is not set up. Open Options > YouTube & other sites and click \"Download / update tools\".");
        var outputBase = Path.ChangeExtension(item.FilePath, null);
        try
        {
            var folder = Path.GetDirectoryName(outputBase)!;
            var stem = Path.GetFileName(outputBase);
            item.DiskLoadedBytes = Directory.Exists(folder)
                ? Directory.GetFiles(folder, stem + "*.part").Sum(path => new FileInfo(path).Length)
                : 0;
        }
        catch (Exception) { item.DiskLoadedBytes = 0; }
        var live = Live(item); live.Workers = 1; live.Info[0] = "Receiving data..."; live.ResumeSupported = true;
        item.ActiveConnections = 1;
        var streams = new Dictionary<string, (long Done, long Total)>();
        var clock = Stopwatch.StartNew(); var lastReport = -1000L;
        double speed = 0;

        void OnProgress(YtProgress p)
        {
            var id = p.FormatId ?? "";
            var total = p.Total ?? p.TotalEstimate ?? (streams.TryGetValue(id, out var known) ? known.Total : 0);
            var done = string.Equals(p.Status, "finished", StringComparison.OrdinalIgnoreCase) && total > 0 ? total : p.Downloaded;
            streams[id] = (done, Math.Max(total, done));
            speed = p.Speed ?? 0;
            if (clock.ElapsedMilliseconds - lastReport < 250 && !string.Equals(p.Status, "finished", StringComparison.OrdinalIgnoreCase)) return;
            lastReport = clock.ElapsedMilliseconds;
            var knownTotal = streams.Values.Sum(v => v.Total);
            // The extension already has yt-dlp's combined video+audio estimate. While yt-dlp is reporting only the
            // current stream, do not replace that combined estimate with (for example) the much smaller audio size.
            if (knownTotal > 0) item.TotalBytes = Math.Max(item.TotalBytes ?? 0, knownTotal);
            Interlocked.Exchange(ref live.Bytes[0], streams.Values.Sum(v => v.Done));
            Report(item, streams.Values.Sum(v => v.Done), (long)speed);
        }

        void OnStage(string line) { item.EtaText = "Merging…"; live.Info[0] = "Merging…"; }

        var final = await yt.DownloadAsync(pageUrl, key, outputBase, OnProgress, OnStage, ct);
        item.FilePath = final;
        var length = new FileInfo(final).Length;
        item.TotalBytes = length; item.DoneBytes = length;
        live.Info[0] = "Download complete.";
    }

    // ---------------------------------------------------------------- helpers

    async Task AcquireBandwidthAsync(DownloadItem item, int bytes, CancellationToken ct)
    {
        if (LimitScope == SpeedLimitScope.Combined) await _globalLimiter.WaitAsync(bytes, ct);
        var localLimit = EffectiveLocalLimit(item);
        if (localLimit > 0)
        {
            var limiter = _localLimiters.GetOrAdd(item.Id, _ => new RateLimiter());
            limiter.Limit = localLimit;
            await limiter.WaitAsync(bytes, ct);
        }
        else if (_localLimiters.TryGetValue(item.Id, out var existing)) existing.Limit = 0;
    }

    /// <summary>A dead TCP connection can otherwise leave a download saying 0 B/s forever. Treat a connection that
    /// delivers no bytes for a while as transient; the normal retry path reconnects and resumes from the saved offset.</summary>
    static async Task<int> ReadWithIdleTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(ReadIdleTimeout);
        try { return await input.ReadAsync(buffer, idle.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException("The connection stopped sending data. Reconnecting…", ex);
        }
    }

    static async Task VerifyAsync(DownloadItem item, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.ExpectedSha256)) return;
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));
        var expected = item.ExpectedSha256.Replace(" ", "", StringComparison.Ordinal);
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 verification failed.");
    }

    HttpRequestMessage BuildRequest(HttpMethod method, DownloadItem item) => BuildRequest(method, item, new Uri(item.Url));

    HttpRequestMessage BuildRequest(HttpMethod method, DownloadItem item, Uri uri)
    {
        // Prefer HTTP/2 multiplexing where the server supports it, with a transparent HTTP/1.1
        // fallback for older download hosts.
        var request = new HttpRequestMessage(method, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        if (!string.IsNullOrWhiteSpace(item.Cookie)) request.Headers.TryAddWithoutValidation("Cookie", item.Cookie);
        if (Uri.TryCreate(item.Referrer, UriKind.Absolute, out var referrer)) request.Headers.Referrer = referrer;
        var agent = string.IsNullOrWhiteSpace(item.UserAgent) ? DefaultUserAgent : item.UserAgent;
        if (!string.IsNullOrWhiteSpace(agent)) request.Headers.TryAddWithoutValidation("User-Agent", agent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        return request;
    }

    static void AddIfRange(HttpRequestMessage request, DownloadItem item)
    {
        // If-Range only accepts a *strong* validator; a weak ETag would make servers ignore Range entirely.
        if (!string.IsNullOrWhiteSpace(item.ETag) && !item.ETag.StartsWith("W/", StringComparison.Ordinal))
        {
            try { request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(item.ETag)); return; } catch { }
        }
        if (DateTimeOffset.TryParse(item.LastModified, out var modified)) request.Headers.IfRange = new RangeConditionHeaderValue(modified);
    }

    bool PartialFilesExist(DownloadItem item) =>
        File.Exists(PartBase(item) + ".part") || File.Exists(SegPath(item)) || File.Exists(SegMapPath(item)) || File.Exists(HlsStatePath(item));

    void DeletePartialFiles(DownloadItem item)
    {
        if (YtDlpService.TryGetSelection(item.Url, out _, out _)) YtDlpService.DeleteLeftovers(Path.ChangeExtension(item.FilePath, null));
        DeletePartsAt(item.FilePath);
        var temp = PartBase(item);
        if (!string.Equals(temp, item.FilePath, StringComparison.Ordinal)) DeletePartsAt(temp);
        TryDelete(item.FilePath + ".mux");
    }

    static void DeletePartsAt(string baseName)
    {
        TryDelete(baseName + ".part"); TryDelete(baseName + ".seg"); TryDelete(baseName + ".seg.map"); TryDelete(baseName + ".seg.map.tmp");
        TryDelete(baseName + ".hls.json"); TryDelete(baseName + ".hls.json.tmp"); TryDelete(baseName + ".audio.part");
    }

    void DeleteSegmentFiles(DownloadItem item) { TryDelete(SegPath(item)); TryDelete(SegMapPath(item)); TryDelete(SegMapPath(item) + ".tmp"); }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static void AtomicReplace(string source, string target)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            // One replace operation: there is never a moment with neither the old nor the new file on disk.
            File.Move(source, target, true);
        }
        catch
        {
            try
            {
                File.Copy(source, target, true);
                TryDelete(source);
            }
            catch (Exception ex)
            {
                throw new IOException($"Could not move downloaded file to destination '{target}': {ex.Message}", ex);
            }
        }
    }

    sealed class RangeNotSupportedException : IOException { }
}
