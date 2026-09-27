using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace MakanDownloadManager.Services.Torrent;

public sealed class DiskWriteTask
{
    public int PieceIndex { get; }
    public byte[] Buffer { get; }
    public long FileOffset { get; }
    public byte[] ExpectedHash { get; }
    public List<PeerState> Contributors { get; }
    public TaskCompletionSource<bool> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DiskWriteTask(int pieceIndex, byte[] buffer, long fileOffset, byte[] expectedHash, List<PeerState> contributors)
    {
        PieceIndex = pieceIndex;
        Buffer = buffer;
        FileOffset = fileOffset;
        ExpectedHash = expectedHash;
        Contributors = contributors;
    }
}

/// <summary>
/// Bounded asynchronous disk & hashing pipeline (Phase 6 / Section 19 & 20).
/// Prevents disk write latency or SHA-1 calculation from blocking peer socket loops.
/// Provides bounded queueing with automatic backpressure to bound memory usage.
/// </summary>
public sealed class TorrentAsyncDiskPipeline : IDisposable
{
    readonly Channel<DiskWriteTask> _writeChannel;
    readonly Task[] _workers;
    readonly CancellationTokenSource _cts = new();
    readonly RateMeter _writeRateMeter = new();
    readonly RateMeter _hashingRateMeter = new();

    public int MaxQueueCapacity { get; }
    public int QueueDepth => _writeChannel.Reader.Count;
    public long WriteRateBytesPerSec => _writeRateMeter.BytesPerSecond;
    public long HashingRateBytesPerSec => _hashingRateMeter.BytesPerSecond;

    public TorrentAsyncDiskPipeline(int maxQueueCapacity = 32, int workerCount = 2)
    {
        MaxQueueCapacity = maxQueueCapacity;
        var options = new BoundedChannelOptions(maxQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // Apply backpressure if queue fills
            SingleReader = workerCount == 1,
            SingleWriter = false
        };

        _writeChannel = Channel.CreateBounded<DiskWriteTask>(options);
        _workers = new Task[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }
    }

    public async Task<bool> EnqueueAndVerifyAsync(DiskWriteTask task, TorrentStorage storage, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _writeChannel.Writer.WriteAsync(task, linkedCts.Token).ConfigureAwait(false);

        // Process actual disk operation on worker pool
        _ = Task.Run(() =>
        {
            try
            {
                var success = ProcessDiskTask(task, storage);
                task.CompletionSource.TrySetResult(success);
            }
            catch (Exception ex)
            {
                task.CompletionSource.TrySetException(ex);
            }
        }, linkedCts.Token);

        return await task.CompletionSource.Task.ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _writeChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_writeChannel.Reader.TryRead(out var task))
                {
                    task.CompletionSource.TrySetResult(true);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool ProcessDiskTask(DiskWriteTask task, TorrentStorage storage)
    {
        // 1. Parallel SHA-1 hash check
        var hashSw = System.Diagnostics.Stopwatch.StartNew();
        var actualHash = SHA1.HashData(task.Buffer);
        hashSw.Stop();

        _hashingRateMeter.Add(task.Buffer.Length);

        if (!actualHash.AsSpan().SequenceEqual(task.ExpectedHash))
        {
            return false;
        }

        // 2. Write piece buffer to disk storage
        storage.Write(task.FileOffset, task.Buffer);
        _writeRateMeter.Add(task.Buffer.Length);

        return true;
    }

    public void Dispose()
    {
        _writeChannel.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
    }
}
