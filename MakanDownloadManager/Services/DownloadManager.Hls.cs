using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

/// <summary>
/// Native HLS (.m3u8) downloading: segments are fetched in parallel, decrypted (AES-128) and written in order into ONE file
/// (.ts, or .mp4 for fragmented-MP4 streams). The file is resumable at segment granularity. TS -> MP4 conversion and merging of a
/// separate audio track need FFmpeg; without it the result is kept as .ts (video-only when the audio is a separate track).
/// A separate audio track travels inside the item's URL fragment:  https://host/video.m3u8#makan-audio=&lt;escaped audio playlist url&gt;
/// (fragments are never sent to servers, and this needs no database change).
/// </summary>
public sealed partial class DownloadManager
{
    /// <summary>Path of ffmpeg (from Settings). Optional; searched on PATH when empty.</summary>
    public string? FfmpegPath { get; set; }

    const string AudioFragmentKey = "#makan-audio=";

    public static string WithAudio(string videoPlaylistUrl, string? audioPlaylistUrl) =>
        string.IsNullOrWhiteSpace(audioPlaylistUrl) ? videoPlaylistUrl : videoPlaylistUrl.Split('#')[0] + AudioFragmentKey + Uri.EscapeDataString(audioPlaylistUrl);

    public static bool IsHlsUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   uri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                   uri.Query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException) { return false; }
    }

    public static bool IsDashUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || uri.Query.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException) { return false; }
    }

    /// <summary>HLS playlists and DASH manifests are both downloaded by the segment engine in this file.</summary>
    static bool IsHlsItem(DownloadItem item) => IsHlsUrl(item.Url) || IsDashUrl(item.Url);

    static (Uri Video, Uri? Audio) SplitHlsUrl(string url)
    {
        var uri = new Uri(url);
        Uri? audio = null;
        if (uri.Fragment.StartsWith(AudioFragmentKey, StringComparison.Ordinal) &&
            Uri.TryCreate(Uri.UnescapeDataString(uri.Fragment[AudioFragmentKey.Length..]), UriKind.Absolute, out var a)) audio = a;
        return (new UriBuilder(uri) { Fragment = "" }.Uri, audio);
    }

    string HlsStatePath(DownloadItem item) => PartBase(item) + ".hls.json";
    void DeleteHlsFiles(DownloadItem item) => DeleteHlsFilesAt(PartBase(item), item.FilePath);
    static void DeleteHlsFilesAt(string baseName, string filePath)
    {
        TryDelete(baseName + ".hls.json"); TryDelete(baseName + ".hls.json.tmp"); TryDelete(baseName + ".audio.part"); TryDelete(filePath + ".mux");
    }

    sealed class HlsTrackState
    {
        public string Url { get; set; } = "";
        public int Count { get; set; }
        public string First { get; set; } = "";
        public int Written { get; set; }
        public long PartLength { get; set; }
        public bool Done { get; set; }
    }

    sealed class HlsTrack
    {
        public required Uri PlaylistUri { get; init; }
        public required HlsPlaylist Playlist { get; init; }
        public required string PartPath { get; init; }
        public required HlsTrackState State { get; init; }
    }

    // ---------------------------------------------------------------- orchestration

    sealed record LoadedStream(Uri Origin, Uri VideoUri, HlsPlaylist Video, Uri? AudioUri, HlsPlaylist? Audio);

    async Task HlsAsync(DownloadItem item, CancellationToken ct)
    {
        var loaded = IsDashUrl(item.Url) ? await LoadDashAsync(item, ct) : await LoadHlsAsync(item, ct);
        var origin = loaded.Origin; var videoUri = loaded.VideoUri; var playlist = loaded.Video;
        var audioUri = loaded.AudioUri; var audioPlaylist = loaded.Audio;
        Validate(playlist);
        if (audioPlaylist != null) Validate(audioPlaylist);

        var fmp4 = playlist.Segments.Any(s => s.Map != null);
        if (!File.Exists(HlsStatePath(item))) { DeletePartialFiles(item); EnsureHlsFileName(item, fmp4); }
        var partBase = PartBase(item);        // fixed for the whole run (FinishHlsAsync may rename the result)
        var part = partBase + ".part";
        item.StatusCode = 200;
        item.AutoName = false;

        var saved = LoadHlsState(item);
        var tracks = new List<HlsTrack> { NewTrack(videoUri, playlist, part, saved.ElementAtOrDefault(0)) };
        if (audioPlaylist != null) tracks.Add(NewTrack(audioUri!, audioPlaylist, partBase + ".audio.part", saved.ElementAtOrDefault(1)));
        SaveHlsState(item, tracks);
        _store.Save(item);

        var totalSegments = tracks.Sum(t => t.Playlist.Segments.Count);
        var workers = Math.Clamp(item.Connections > 0 ? item.Connections : DefaultConnections, 1, MaxConnectionsPerDownload);
        item.ActiveConnections = workers;
        item.DiskLoadedBytes = tracks.Sum(t => t.State.PartLength);
        var tracker = new ProgressTracker(item.DiskLoadedBytes);

        await WithReporter(item, tracker, async () =>
        {
            foreach (var track in tracks) await RunHlsTrackAsync(item, origin, track, tracks, tracker, totalSegments, workers, ct);
            return 0;
        });

        item.ActiveConnections = 0;
        await FinishHlsAsync(item, tracks, fmp4, ct);
        var length = new FileInfo(item.FilePath).Length;
        item.TotalBytes = length;
        item.DoneBytes = length;
        DeleteHlsFilesAt(partBase, item.FilePath);
    }

    async Task<LoadedStream> LoadHlsAsync(DownloadItem item, CancellationToken ct)
    {
        var (videoUri, audioUri) = SplitHlsUrl(item.Url);
        var origin = videoUri;
        var playlist = await FetchPlaylistAsync(item, origin, videoUri, ct);
        if (playlist.IsMaster)
        {
            // A master playlist was given directly (e.g. pasted): take the best quality.
            var best = playlist.Variants.OrderByDescending(v => v.Bandwidth).FirstOrDefault()
                       ?? throw new InvalidDataException("The playlist lists no streams.");
            videoUri = best.Uri;
            audioUri ??= best.AudioUri;
            playlist = await FetchPlaylistAsync(item, origin, videoUri, ct);
            if (playlist.IsMaster) throw new InvalidDataException("Nested master playlists are not supported.");
        }
        HlsPlaylist? audioPlaylist = null;
        if (audioUri != null)
        {
            audioPlaylist = await FetchPlaylistAsync(item, origin, audioUri, ct);
            if (audioPlaylist.IsMaster) audioPlaylist = null;
        }
        return new LoadedStream(origin, videoUri, playlist, audioUri, audioPlaylist);
    }

    async Task<LoadedStream> LoadDashAsync(DownloadItem item, CancellationToken ct)
    {
        var full = new Uri(item.Url);
        var manifestUri = new UriBuilder(full) { Fragment = "" }.Uri;
        var (text, finalUri) = await FetchTextAsync(item, manifestUri, manifestUri, ct);
        if (!DashManifest.LooksLikeMpd(text)) throw new InvalidDataException("The server did not return a DASH manifest (login page or expired link?).");
        var manifest = DashManifest.Parse(finalUri, text);
        if (manifest.IsDynamic) throw new NotSupportedException("This is a live stream (the manifest never ends). Live streams can't be downloaded yet.");

        var (wantVideo, wantAudio) = DashManifest.ParseSelection(full.Fragment);
        var video = wantVideo != null ? manifest.Representations.FirstOrDefault(r => r.Id == wantVideo) : (manifest.BestVideo() ?? manifest.BestAudio());
        video ??= manifest.BestVideo() ?? manifest.BestAudio() ?? throw new NotSupportedException(manifest.Representations.Any(r => r.Encrypted) ? "This stream is protected with DRM and can't be downloaded." : "The manifest lists no video.");
        var audio = video.Kind == "video" ? (wantAudio != null ? manifest.Representations.FirstOrDefault(r => r.Id == wantAudio) : manifest.BestAudio()) : null;

        Uri Identity(DashRepresentation r) => new(manifestUri + "#rep=" + Uri.EscapeDataString(r.Id));
        return new LoadedStream(manifestUri, Identity(video), manifest.BuildTrack(video.Id), audio != null ? Identity(audio) : null, audio != null ? manifest.BuildTrack(audio.Id) : null);
    }

    static void Validate(HlsPlaylist playlist)
    {
        if (playlist.Segments.Count == 0) throw new InvalidDataException("The playlist contains no segments.");
        if (!playlist.IsEndList) throw new NotSupportedException("This is a live stream (the playlist never ends). Live streams can't be downloaded yet.");
        var bad = playlist.Segments.Select(s => s.Key).FirstOrDefault(k => k != null && k.Method != "AES-128");
        if (bad != null) throw new NotSupportedException($"This stream is protected with {bad.Method} (DRM) and can't be downloaded.");
    }

    HlsTrack NewTrack(Uri playlistUri, HlsPlaylist playlist, string partPath, HlsTrackState? saved)
    {
        // Resume only when the playlist is still the same one and the partial file is at least as long as we recorded.
        var segments = playlist.Segments;
        var fresh = new HlsTrackState { Url = playlistUri.ToString(), Count = segments.Count, First = segments[0].Uri.ToString() };
        var state = fresh;
        if (saved != null && saved.Url == fresh.Url && saved.Count == fresh.Count && saved.First == fresh.First &&
            File.Exists(partPath) && new FileInfo(partPath).Length >= saved.PartLength) state = saved;
        else TryDelete(partPath);
        return new HlsTrack { PlaylistUri = playlistUri, Playlist = playlist, PartPath = partPath, State = state };
    }

    List<HlsTrackState> LoadHlsState(DownloadItem item)
    {
        try
        {
            var path = HlsStatePath(item);
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<HlsTrackState>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { _diagnostics.Error("Ignoring unreadable HLS resume file.", ex); }
        return new();
    }

    void SaveHlsState(DownloadItem item, IReadOnlyList<HlsTrack> tracks)
    {
        var path = HlsStatePath(item);
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(tracks.Select(t => t.State).ToList()));
            File.Move(path + ".tmp", path, true);
        }
        catch (IOException) { /* best effort: worst case the download restarts */ }
    }

    void EnsureHlsFileName(DownloadItem item, bool fmp4)
    {
        // fMP4 streams can only live in an .mp4; TS streams keep .ts or .mp4 (converted at the end); anything else becomes .ts
        var ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
        var desired = fmp4 ? ".mp4" : (ext is ".mp4" or ".ts" ? ext : ".ts");
        if (ext == desired) return;
        var target = DownloadFileNamer.MakeUnique(Path.ChangeExtension(item.FilePath, desired), p => IsPathClaimed(item, p));
        item.FilePath = target;
        item.Category = CategoryService.For(target);
    }

    // ---------------------------------------------------------------- one track (video or audio)

    async Task RunHlsTrackAsync(DownloadItem item, Uri origin, HlsTrack track, IReadOnlyList<HlsTrack> all, ProgressTracker tracker, int totalSegments, int workerCount, CancellationToken ct)
    {
        var segments = track.Playlist.Segments;
        var total = segments.Count;
        var state = track.State;
        if (state.Done) return;
        var start = state.Written;

        await using var output = new FileStream(track.PartPath, start > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        if (start > 0) { output.SetLength(state.PartLength); output.Seek(0, SeekOrigin.End); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var slots = new TaskCompletionSource<byte[]>?[total];
        for (var i = start; i < total; i++) slots[i] = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new SemaphoreSlim(workerCount + 4);      // fetched-but-not-yet-written segments are bounded (memory)
        var keys = new ConcurrentDictionary<Uri, Task<byte[]>>();
        var next = start - 1;
        Exception? failure = null;

        async Task Worker()
        {
            while (true)
            {
                await gate.WaitAsync(linked.Token);
                var i = Interlocked.Increment(ref next);
                if (i >= total) { gate.Release(); return; }
                try { slots[i]!.SetResult(await FetchHlsSegmentAsync(item, origin, segments[i], keys, linked.Token)); }
                catch { gate.Release(); throw; }
            }
        }

        var workerTasks = Enumerable.Range(0, Math.Min(workerCount, total - start)).Select(_ => Task.Run(async () =>
        {
            try { await Worker(); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); linked.Cancel(); }
        })).ToArray();

        var lastSave = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            HlsMap? previousMap = start > 0 ? segments[start - 1].Map : null;
            for (var i = start; i < total; i++)
            {
                var data = await slots[i]!.Task.WaitAsync(linked.Token);
                var segment = segments[i];
                var written = data.Length;
                if (segment.Map != null && (previousMap == null || segment.Map != previousMap))
                {
                    var init = await GetHlsBytesAsync(item, origin, segment.Map.Uri, segment.Map.RangeOffset, segment.Map.RangeLength, linked.Token);
                    await output.WriteAsync(init, linked.Token);
                    written += init.Length;
                    previousMap = segment.Map;
                }
                await output.WriteAsync(data, linked.Token);
                slots[i] = null;
                gate.Release();

                tracker.Add(written);
                state.Written = i + 1;
                state.PartLength += written;
                var segmentsDone = all.Sum(t => t.State.Written);
                item.TotalBytes = (long)(tracker.Done / (double)Math.Max(1, segmentsDone) * totalSegments);

                if (lastSave.ElapsedMilliseconds >= 2000)
                {
                    lastSave.Restart();
                    await output.FlushAsync(linked.Token);
                    SaveHlsState(item, all);
                }
            }
            await output.FlushAsync(linked.Token);
            state.Done = true;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref failure) != null)
        {
            ExceptionDispatchInfo.Capture(Volatile.Read(ref failure)!).Throw();
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(workerTasks); } catch { /* already recorded in 'failure' */ }
            try { await output.FlushAsync(CancellationToken.None); } catch { }
            SaveHlsState(item, all);   // consistent (Written, PartLength) pair for resume
        }
    }

    async Task<byte[]> FetchHlsSegmentAsync(DownloadItem item, Uri origin, HlsSegment segment, ConcurrentDictionary<Uri, Task<byte[]>> keys, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var data = await GetHlsBytesAsync(item, origin, segment.Uri, segment.RangeOffset, segment.RangeLength, ct);
                await AcquireBandwidthAsync(item, (int)Math.Min(data.Length, int.MaxValue), ct);
                if (segment.Key is { Method: "AES-128", KeyUri: { } keyUri } key)
                {
                    var keyTask = keys.GetOrAdd(keyUri, u => GetHlsBytesAsync(item, origin, u, null, null, ct));
                    byte[] keyBytes;
                    try
                    {
                        keyBytes = await keyTask;
                    }
                    catch
                    {
                        // Never leave a failed request in the shared cache. Segment retries must
                        // perform a fresh request instead of awaiting the same faulted Task forever.
                        if (keys.TryGetValue(keyUri, out var cached) && ReferenceEquals(cached, keyTask))
                            keys.TryRemove(keyUri, out _);
                        throw;
                    }
                    if (keyBytes.Length != 16) { keys.TryRemove(keyUri, out _); throw new InvalidDataException("The decryption key is not 16 bytes (login required?)."); }
                    try
                    {
                        using var aes = Aes.Create();
                        aes.Key = keyBytes;
                        data = aes.DecryptCbc(data, key.Iv ?? SequenceIv(segment.Sequence), PaddingMode.PKCS7);
                    }
                    catch (CryptographicException) { throw new InvalidDataException($"Segment {segment.Index + 1} could not be decrypted (wrong key?)."); }
                }
                return data;
            }
            catch (Exception ex) when (attempt < 5 && !ct.IsCancellationRequested && RetryPolicy.IsTransient(ex))
            {
                await Task.Delay(RetryPolicy.DelayFor(ex, attempt), ct);
            }
        }
    }

    static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }

    // ---------------------------------------------------------------- HTTP helpers

    /// <summary>The Cookie header belongs to the site the user was logged in to: it is only sent to that same host.</summary>
    HttpRequestMessage HlsRequest(DownloadItem item, Uri origin, Uri uri)
    {
        var request = BuildRequest(HttpMethod.Get, item, uri);
        if (!string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)) request.Headers.Remove("Cookie");
        return request;
    }

    async Task<(string Text, Uri FinalUri)> FetchTextAsync(DownloadItem item, Uri origin, Uri uri, CancellationToken ct)
    {
        using var request = HlsRequest(item, origin, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw RetryPolicy.StatusError(response);
        return (await response.Content.ReadAsStringAsync(ct), response.RequestMessage?.RequestUri ?? uri);
    }

    async Task<HlsPlaylist> FetchPlaylistAsync(DownloadItem item, Uri origin, Uri uri, CancellationToken ct)
    {
        var (text, finalUri) = await FetchTextAsync(item, origin, uri, ct);
        if (!HlsPlaylist.LooksLikePlaylist(text)) throw new InvalidDataException("The server did not return a video playlist (login page or expired link?).");
        return HlsPlaylist.Parse(finalUri, text);
    }

    async Task<byte[]> GetHlsBytesAsync(DownloadItem item, Uri origin, Uri uri, long? offset, long? length, CancellationToken ct)
    {
        using var request = HlsRequest(item, origin, uri);
        if (offset.HasValue && length.HasValue) request.Headers.Range = new RangeHeaderValue(offset.Value, offset.Value + length.Value - 1);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw RetryPolicy.StatusError(response);
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A video segment came back as a web page (session expired?).");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // Server ignored the Range header and sent the whole resource: cut out the part we asked for.
        if (offset.HasValue && length.HasValue && response.StatusCode == System.Net.HttpStatusCode.OK && bytes.Length >= offset.Value + length.Value)
            bytes = bytes.AsSpan((int)offset.Value, (int)length.Value).ToArray();
        return bytes;
    }

    // ---------------------------------------------------------------- finishing

    async Task FinishHlsAsync(DownloadItem item, IReadOnlyList<HlsTrack> tracks, bool fmp4, CancellationToken ct)
    {
        var target = item.FilePath;
        var wantMp4 = string.Equals(Path.GetExtension(target), ".mp4", StringComparison.OrdinalIgnoreCase);
        var video = tracks[0]; var audio = tracks.Count > 1 ? tracks[1] : null;

        if (audio == null && (fmp4 || !wantMp4)) { AtomicReplace(video.PartPath, target); return; }   // nothing to convert

        string? ffmpeg = null;
        try { ffmpeg = MediaService.ResolveFfmpeg(FfmpegPath); } catch (FileNotFoundException) { }
        string? reason = ffmpeg == null ? "FFmpeg was not found" : null;

        if (ffmpeg != null)
        {
            var temp = target + ".mux";
            try
            {
                var inputs = new List<string> { video.PartPath };
                if (audio != null) inputs.Add(audio.PartPath);
                await MediaService.RemuxAsync(ffmpeg, inputs, temp, wantMp4 ? "mp4" : "mpegts", ct);
                AtomicReplace(temp, target);
                TryDelete(video.PartPath); if (audio != null) TryDelete(audio.PartPath);
                return;
            }
            catch (OperationCanceledException) { TryDelete(temp); throw; }
            catch (Exception ex)
            {
                TryDelete(temp);
                _diagnostics.Error("FFmpeg conversion failed for the HLS download.", ex);
                reason = "FFmpeg failed: " + ex.Message;
            }
        }

        // No usable FFmpeg: keep the raw stream so nothing is lost.
        var rawExt = fmp4 ? ".mp4" : ".ts";
        var fallback = string.Equals(Path.GetExtension(target), rawExt, StringComparison.OrdinalIgnoreCase)
            ? target : DownloadFileNamer.MakeUnique(Path.ChangeExtension(target, rawExt), p => IsPathClaimed(item, p));
        AtomicReplace(video.PartPath, fallback);
        item.FilePath = fallback;
        item.Category = CategoryService.For(fallback);
        var note = wantMp4 && !fmp4 ? $"{reason}: saved as {rawExt} (MP4 needs FFmpeg — set it in Settings)." : "";
        if (audio != null)
        {
            var firstExt = Path.GetExtension(audio.Playlist.Segments[0].Uri.AbsolutePath).ToLowerInvariant();
            var audioExt = audio.Playlist.Segments.Any(s => s.Map != null) ? ".mp4" : (firstExt is ".aac" or ".mp3" or ".ac3" or ".m4a" ? firstExt : ".ts");
            var audioTarget = Path.ChangeExtension(fallback, null) + ".audio" + audioExt;
            AtomicReplace(audio.PartPath, audioTarget);
            note = $"{reason}: the audio track was saved separately as {Path.GetFileName(audioTarget)} (needs FFmpeg to merge).";
        }
        item.CompletionNote = string.IsNullOrEmpty(note) ? null : note;
        if (item.CompletionNote != null) Notification?.Invoke(item, "Saved without conversion");
    }
}
