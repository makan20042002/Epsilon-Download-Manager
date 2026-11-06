using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MakanDownloadManager.Services.Torrent;

/// <summary>
/// uTP (BEP 29): a reliable, ordered byte stream over UDP with delay-based congestion control (LEDBAT). It is what
/// BitTorrent clients use by default; many peers (behind NAT, or on networks that throttle TCP) can only be reached this way.
/// One <see cref="UtpManager"/> multiplexes every uTP connection over one UDP socket.
/// </summary>
public sealed class UtpManager : IDisposable
{
    internal const int HeaderSize = 20;
    /// <summary>Payload bytes per packet: small enough to avoid IP fragmentation on almost every path.</summary>
    internal const int MaxPayload = 1200;
    internal const byte StData = 0, StFin = 1, StState = 2, StReset = 3, StSyn = 4;

    readonly Action<byte[], int, IPEndPoint> _send;
    readonly ConcurrentDictionary<(IPEndPoint Remote, ushort RecvId), UtpStream> _connections = new();
    readonly CancellationTokenSource _cts = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    bool _disposed;

    /// <summary>Raised for every connection another peer opened to us.</summary>
    public event Action<UtpStream>? Accepted;
    /// <summary>When false, incoming connections are refused (answered with a reset).</summary>
    public bool AcceptIncoming { get; set; } = true;
    public int ConnectionCount => _connections.Count;
    /// <summary>Total payload bytes handed to the network (for diagnostics and tests).</summary>
    public long PacketsSent, PacketsRetransmitted;

    public UtpManager(Action<byte[], int, IPEndPoint> send)
    {
        _send = send;
        _ = Task.Run(() => TickLoopAsync(_cts.Token));
    }

    internal uint NowMicros => unchecked((uint)(_clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency));

    internal void Send(byte[] buffer, int length, IPEndPoint to)
    {
        Interlocked.Increment(ref PacketsSent);
        try { _send(buffer, length, to); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    static IPEndPoint Normalize(IPEndPoint ep) => ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    /// <summary>True when the datagram looked like uTP (version 1, a known packet type) and was handled.</summary>
    public bool HandlePacket(byte[] data, int length, IPEndPoint fromEndPoint)
    {
        if (_disposed || length < HeaderSize) return false;
        var type = data[0] >> 4; var version = data[0] & 0x0F;
        if (version != 1 || type > StSyn) return false;
        var from = Normalize(fromEndPoint);
        var id = (ushort)((data[2] << 8) | data[3]);

        if (type == StSyn)
        {
            var recvId = unchecked((ushort)(id + 1));
            if (_connections.TryGetValue((from, recvId), out var existing)) { existing.OnPacket(data, length); return true; }
            if (!AcceptIncoming || Accepted == null) { SendReset(from, id, data); return true; }
            var stream = new UtpStream(this, from, recvId: recvId, sendId: id, incomingSyn: data);
            if (!_connections.TryAdd((from, recvId), stream)) return true;
            stream.OnPacket(data, length);
            try { Accepted?.Invoke(stream); } catch (Exception) { stream.Dispose(); }
            return true;
        }

        if (_connections.TryGetValue((from, id), out var conn)) { conn.OnPacket(data, length); return true; }
        if (type != StReset) SendReset(from, id, data);
        return true;
    }

    void SendReset(IPEndPoint to, ushort id, byte[] original)
    {
        var packet = new byte[HeaderSize];
        packet[0] = (StReset << 4) | 1;
        packet[2] = (byte)(id >> 8); packet[3] = (byte)id;
        var now = NowMicros;
        packet[4] = (byte)(now >> 24); packet[5] = (byte)(now >> 16); packet[6] = (byte)(now >> 8); packet[7] = (byte)now;
        // ack what they sent so they know which connection this answers
        packet[18] = original[16]; packet[19] = original[17];
        Send(packet, HeaderSize, to);
    }

    /// <summary>Opens a connection to a peer's UDP port. Fails with a timeout if nobody answers.</summary>
    public async Task<UtpStream> ConnectAsync(IPEndPoint remote, TimeSpan timeout, CancellationToken ct)
    {
        remote = Normalize(remote);
        UtpStream? stream = null;
        for (var attempt = 0; attempt < 8 && stream == null; attempt++)
        {
            var recvId = (ushort)RandomNumberGenerator.GetInt32(1, 65000);
            var candidate = new UtpStream(this, remote, recvId: recvId, sendId: unchecked((ushort)(recvId + 1)), incomingSyn: null);
            if (_connections.TryAdd((remote, recvId), candidate)) stream = candidate; else candidate.Dispose();
        }
        if (stream == null) throw new IOException("No free uTP connection id.");
        try { await stream.ConnectAsync(timeout, ct).ConfigureAwait(false); }
        catch { stream.Dispose(); throw; }
        return stream;
    }

    internal void Remove(UtpStream stream) => _connections.TryRemove((stream.Remote, stream.RecvId), out _);

    async Task TickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(20, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            foreach (var c in _connections.Values)
            {
                try { c.Tick(); } catch (Exception) { /* one broken connection must not stop the others */ }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        foreach (var c in _connections.Values) c.Abort();
        _connections.Clear();
    }
}

/// <summary>A tiny awaitable "something happened" flag.</summary>
sealed class AsyncSignal
{
    TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Set() { lock (this) _tcs.TrySetResult(); }
    public async Task WaitAsync(CancellationToken ct)
    {
        Task t; lock (this) t = _tcs.Task;
        await t.WaitAsync(ct).ConfigureAwait(false);
        lock (this) { if (ReferenceEquals(_tcs.Task, t)) _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }
    }
}

/// <summary>One uTP connection, presented as a <see cref="Stream"/> so the peer protocol code does not care which transport it runs on.</summary>
public sealed class UtpStream : Stream
{
    sealed class OutPacket
    {
        public ushort Seq; public byte Type; public byte[] Payload = Array.Empty<byte>();
        public long SentAtMs; public int Transmissions; public bool Acked;
    }

    enum State { SynSent, Connected, Closing, Closed }

    const int ReceiveCapacity = 1 << 20;            // advertised receive window
    const int ReorderLimit = 256;                    // how far ahead of the next expected packet we keep out-of-order data
    const uint TargetDelayMicros = 100_000;
    const int MinRtoMs = 500, MaxRtoMs = 8_000;
    const int InitialWindow = 10 * UtpManager.MaxPayload;
    const int MaxWindowCap = 8 * 1024 * 1024;

    readonly UtpManager _mgr;
    readonly object _gate = new();
    readonly AsyncSignal _readSignal = new(), _writeSignal = new(), _connectSignal = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    public IPEndPoint Remote { get; }
    public ushort RecvId { get; }
    readonly ushort _sendId;

    State _state;
    bool _reset, _remoteFin, _fatal;
    ushort _seqNr, _ackNr;                           // next sequence number to use; last in-order packet received
    ushort _lastAckedBySender;                       // highest cumulative ack the peer sent
    int _dupAcks;
    readonly LinkedList<OutPacket> _inFlight = new();
    int _inFlightBytes;
    double _maxWindow = InitialWindow;
    bool _slowStart = true;
    long _slowStartThreshold = long.MaxValue;
    uint _peerWindow = ReceiveCapacity;
    double _rttMs, _rttVarMs; int _rtoMs = 1000;
    long _lastLossReactionMs = -10_000;
    long _lastSendMs, _lastReceiveMs, _closingSinceMs;
    uint _replyMicros;                                // timestamp difference to echo back
    uint _baseDelay = uint.MaxValue; long _baseDelayResetMs;
    int _unackedReceived;

    readonly Queue<byte[]> _received = new(); int _receivedBytes, _receivedOffset;
    readonly Dictionary<ushort, (byte[] Data, byte Type)> _ahead = new();

    public long BytesSent, BytesReceived;
    public string Diagnostics { get { lock (_gate) return $"state={_state} seq={_seqNr} ack={_ackNr} inflight={_inFlight.Count}/{_inFlightBytes}B window={_maxWindow:0} peerWnd={_peerWindow} rto={_rtoMs} rtt={_rttMs:0} dup={_dupAcks} ahead={_ahead.Count} rcvBuf={_receivedBytes} q={_received.Count} off={_receivedOffset} sent={BytesSent} recv={BytesReceived} slow={_slowStart} firstSeq={(_inFlight.First?.Value.Seq.ToString() ?? "-")} tx={(_inFlight.First?.Value.Transmissions.ToString() ?? "-")}"; } }
    public double WindowBytes { get { lock (_gate) return _maxWindow; } }

    internal UtpStream(UtpManager mgr, IPEndPoint remote, ushort recvId, ushort sendId, byte[]? incomingSyn)
    {
        _mgr = mgr; Remote = remote; RecvId = recvId; _sendId = sendId;
        _lastReceiveMs = _lastSendMs = 0;
        if (incomingSyn != null)
        {
            // we are the receiver of a SYN: connected at once, our first packet acknowledges the SYN
            _ackNr = (ushort)((incomingSyn[16] << 8) | incomingSyn[17]);
            _seqNr = (ushort)RandomNumberGenerator.GetInt32(1, 65000);
            _state = State.Connected;
        }
        else { _seqNr = 1; _state = State.SynSent; }
    }

    long NowMs => _clock.ElapsedMilliseconds;

    // ---------------------------------------------------------------- connecting

    internal async Task ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var syn = new OutPacket { Seq = _seqNr, Type = UtpManager.StSyn };
        lock (_gate) { _seqNr++; _inFlight.AddLast(syn); }
        SendPacket(syn);
        try
        {
            while (true)
            {
                lock (_gate) { if (_state == State.Connected) return; if (_reset || _fatal) throw new IOException("The peer refused the uTP connection."); }
                await _connectSignal.WaitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("The peer did not answer on uTP."); }
    }

    // ---------------------------------------------------------------- packets out

    byte[] Build(byte type, ushort seq, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[UtpManager.HeaderSize + payload.Length];
        buf[0] = (byte)((type << 4) | 1);
        // buf[1] = 0: no extensions
        ushort id = type == UtpManager.StSyn ? RecvId : _sendId;
        buf[2] = (byte)(id >> 8); buf[3] = (byte)id;
        var now = _mgr.NowMicros;
        buf[4] = (byte)(now >> 24); buf[5] = (byte)(now >> 16); buf[6] = (byte)(now >> 8); buf[7] = (byte)now;
        uint reply; lock (_gate) reply = _replyMicros;
        buf[8] = (byte)(reply >> 24); buf[9] = (byte)(reply >> 16); buf[10] = (byte)(reply >> 8); buf[11] = (byte)reply;
        uint wnd; lock (_gate) wnd = (uint)Math.Max(0, ReceiveCapacity - _receivedBytes);
        buf[12] = (byte)(wnd >> 24); buf[13] = (byte)(wnd >> 16); buf[14] = (byte)(wnd >> 8); buf[15] = (byte)wnd;
        buf[16] = (byte)(seq >> 8); buf[17] = (byte)seq;
        ushort ack; lock (_gate) ack = _ackNr;
        buf[18] = (byte)(ack >> 8); buf[19] = (byte)ack;
        payload.CopyTo(buf.AsSpan(UtpManager.HeaderSize));
        return buf;
    }

    void SendPacket(OutPacket p)
    {
        var buf = Build(p.Type, p.Seq, p.Payload);
        lock (_gate) { p.SentAtMs = NowMs; p.Transmissions++; _lastSendMs = p.SentAtMs; _unackedReceived = 0; }
        if (p.Transmissions > 1) Interlocked.Increment(ref _mgr.PacketsRetransmitted);
        _mgr.Send(buf, buf.Length, Remote);
    }

    void SendAck()
    {
        ushort seq; lock (_gate) { seq = _seqNr; _unackedReceived = 0; _lastSendMs = NowMs; }
        var buf = Build(UtpManager.StState, seq, ReadOnlySpan<byte>.Empty);
        _mgr.Send(buf, buf.Length, Remote);
    }

    // ---------------------------------------------------------------- packets in

    internal void OnPacket(byte[] data, int length)
    {
        var type = data[0] >> 4;
        var timestamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
        var theirDelay = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);
        var wnd = (uint)((data[12] << 24) | (data[13] << 16) | (data[14] << 8) | data[15]);
        var seq = (ushort)((data[16] << 8) | data[17]);
        var ack = (ushort)((data[18] << 8) | data[19]);

        // skip extension headers (we do not use selective acks)
        var offset = UtpManager.HeaderSize; var ext = data[1];
        while (ext != 0)
        {
            if (offset + 2 > length) return;
            var next = data[offset]; var len = data[offset + 1];
            offset += 2 + len; ext = next;
            if (offset > length) return;
        }
        var payloadLength = length - offset;

        bool needAckNow = false, readReady = false, writeReady = false, connectReady = false;
        lock (_gate)
        {
            if (_state == State.Closed) return;
            _lastReceiveMs = NowMs;
            var nowMicros = _mgr.NowMicros;
            _replyMicros = unchecked(nowMicros - timestamp);
            _peerWindow = wnd;

            if (type == UtpManager.StReset) { _reset = true; _state = State.Closed; readReady = writeReady = connectReady = true; goto done; }

            if (_state == State.SynSent)
            {
                if (type != UtpManager.StState) goto done;
                // the receiver's first packet: it carries the sequence number its first data packet will use
                _ackNr = unchecked((ushort)(seq - 1));
                _state = State.Connected;
                connectReady = true;
            }

            ProcessAck(ack, theirDelay);
            writeReady = true;

            if (type is UtpManager.StData or UtpManager.StFin)
            {
                var expected = unchecked((ushort)(_ackNr + 1));
                var distance = unchecked((ushort)(seq - expected));
                if (distance == 0)
                {
                    Accept(type, data, offset, payloadLength);
                    // drain packets that were waiting for this one
                    while (_ahead.TryGetValue(unchecked((ushort)(_ackNr + 1)), out var held))
                    {
                        _ahead.Remove(unchecked((ushort)(_ackNr + 1)));
                        Accept(held.Type, held.Data, 0, held.Data.Length);
                    }
                    readReady = true;
                    _unackedReceived++;
                    needAckNow = _unackedReceived >= 2 || type == UtpManager.StFin || _ahead.Count > 0;
                }
                else if (distance < ReorderLimit)
                {
                    if (!_ahead.ContainsKey(seq)) _ahead[seq] = (data.AsSpan(offset, payloadLength).ToArray(), (byte)type);
                    needAckNow = true;                       // out of order: tell the sender what we still miss
                }
                else needAckNow = true;                      // an old duplicate: repeat our ack
            }
            if (type == UtpManager.StSyn) needAckNow = true;          // we are the receiver: answer the connection request
            if (_state == State.Closing && _inFlight.Count == 0) { _state = State.Closed; readReady = true; }
        done:;
        }
        if (readReady) _readSignal.Set();
        if (writeReady) _writeSignal.Set();
        if (connectReady) _connectSignal.Set();
        if (needAckNow) SendAck();
        if (_state == State.Closed) _mgr.Remove(this);
    }

    /// <summary>Called with the lock held: an in-order data or fin packet arrives.</summary>
    void Accept(int type, byte[] data, int offset, int length)
    {
        _ackNr = unchecked((ushort)(_ackNr + 1));
        if (type == UtpManager.StFin) { _remoteFin = true; return; }
        if (length <= 0) return;
        var chunk = new byte[length];
        Buffer.BlockCopy(data, offset, chunk, 0, length);
        _received.Enqueue(chunk); _receivedBytes += length; BytesReceived += length;
    }

    /// <summary>Called with the lock held. Frees acknowledged packets, measures the round trip and grows or shrinks the window.</summary>
    void ProcessAck(ushort ack, uint theirDelayMicros)
    {
        var ackedBytes = 0;
        var anyNew = false;
        while (_inFlight.First != null)
        {
            var p = _inFlight.First.Value;
            // p.Seq <= ack (modulo 65536)?
            if (unchecked((ushort)(ack - p.Seq)) >= 0x8000) break;
            _inFlight.RemoveFirst();
            anyNew = true; ackedBytes += p.Payload.Length; _inFlightBytes -= p.Payload.Length;
            if (p.Transmissions == 1 && !_recovering)
            {
                var sample = NowMs - p.SentAtMs;
                if (_rttMs == 0) { _rttMs = sample; _rttVarMs = sample / 2.0; }
                else { _rttVarMs += (Math.Abs(_rttMs - sample) - _rttVarMs) / 4; _rttMs += (sample - _rttMs) / 8; }
                _rtoMs = (int)Math.Clamp(_rttMs + _rttVarMs * 4, MinRtoMs, MaxRtoMs);
            }
            if (p.Type == UtpManager.StSyn || p.Type == UtpManager.StFin) { /* handled by state */ }
        }

        if (anyNew)
        {
            _dupAcks = 0; _lastAckedBySender = ack;
            if (_recovering)
            {
                // NewReno: an ack that moves forward but not past the loss point means the next packet was lost too
                if (_inFlight.First != null && unchecked((ushort)(_recoverSeq - _inFlight.First.Value.Seq)) < 0x8000) _pendingFastRetransmit = true;
                else _recovering = false;
            }
            if (_rttMs > 0) _rtoMs = (int)Math.Clamp(_rttMs + _rttVarMs * 4, MinRtoMs, MaxRtoMs);   // progress: drop any backoff
            // LEDBAT: the peer tells us how long our packets took (their receive time - our send time)
            var nowMs = NowMs;
            if (theirDelayMicros != 0 && theirDelayMicros != uint.MaxValue)
            {
                if (nowMs - _baseDelayResetMs > 120_000) { _baseDelay = uint.MaxValue; _baseDelayResetMs = nowMs; }
                if (theirDelayMicros < _baseDelay) _baseDelay = theirDelayMicros;
            }
            var ourDelay = theirDelayMicros == 0 || _baseDelay == uint.MaxValue ? 0 : unchecked(theirDelayMicros - _baseDelay);
            if (ourDelay > 5_000_000) ourDelay = 0;            // clock jump: ignore the sample
            if (_slowStart)
            {
                if (ourDelay > TargetDelayMicros * 3 / 4 || _maxWindow >= _slowStartThreshold) _slowStart = false;
                else _maxWindow += ackedBytes;                 // doubles every round trip
            }
            else
            {
                var offTarget = ((double)TargetDelayMicros - ourDelay) / TargetDelayMicros;
                var windowFactor = Math.Min(1.0, (double)(_inFlightBytes + ackedBytes) / Math.Max(1.0, _maxWindow));
                _maxWindow += 3000 * offTarget * windowFactor * ackedBytes / Math.Max(1.0, _maxWindow) * 4;   // x4: BEP's gain per RTT, applied faster for fast links
            }
            _maxWindow = Math.Clamp(_maxWindow, UtpManager.MaxPayload, MaxWindowCap);
            if (_state == State.Connected || _state == State.Closing) { /* nothing more */ }
        }
        else if (_inFlight.First != null && ack == _lastAckedBySender)
        {
            _dupAcks++;
            if (_dupAcks >= 3 && _dupAcks % 3 == 0)
            {
                // three duplicate acks: the next packet was lost
                _pendingFastRetransmit = true;
                if (!_recovering) { _recovering = true; _recoverSeq = unchecked((ushort)(_seqNr - 1)); }
                if (NowMs - _lastLossReactionMs > Math.Max(100, _rttMs)) { _maxWindow = Math.Max(UtpManager.MaxPayload * 2, _maxWindow * 0.5); _slowStart = false; _lastLossReactionMs = NowMs; }
            }
        }
    }

    bool _pendingFastRetransmit, _recovering; ushort _recoverSeq;

    // ---------------------------------------------------------------- timers

    internal void Tick()
    {
        OutPacket? resend = null; bool ack = false, dead = false, keepAlive = false;
        lock (_gate)
        {
            var now = NowMs;
            if (_state == State.Closed) return;
            if (_pendingFastRetransmit && _inFlight.First != null) { resend = _inFlight.First.Value; _pendingFastRetransmit = false; }
            else if (_inFlight.First != null && now - _inFlight.First.Value.SentAtMs > _rtoMs)
            {
                var p = _inFlight.First.Value;
                if (p.Transmissions >= 10) dead = true;
                else
                {
                    resend = p;
                    _recovering = true; _recoverSeq = unchecked((ushort)(_seqNr - 1));
                    _rtoMs = Math.Min(MaxRtoMs, _rtoMs * 2);
                    _slowStartThreshold = (long)Math.Max(UtpManager.MaxPayload * 4, _maxWindow / 2); _maxWindow = UtpManager.MaxPayload * 2; _slowStart = true; _lastLossReactionMs = now;   // a timeout is a strong signal: back right off
                }
            }
            if (_unackedReceived > 0 && now - _lastSendMs >= 20) ack = true;                      // delayed ack
            else if (_state == State.Connected && now - _lastSendMs > 10_000) keepAlive = true;
            if (_state != State.SynSent && now - _lastReceiveMs > 60_000) dead = true;
            if (_state == State.Closing && (_inFlight.Count == 0 || now - _closingSinceMs > 6_000)) { _state = State.Closed; dead = false; }
        }
        if (resend != null) SendPacket(resend);
        if (ack || keepAlive) SendAck();
        if (dead) { Fail(); return; }
        bool closed; lock (_gate) closed = _state == State.Closed;
        if (closed) { _readSignal.Set(); _writeSignal.Set(); _mgr.Remove(this); }
    }

    void Fail()
    {
        lock (_gate) { _fatal = true; _state = State.Closed; }
        _readSignal.Set(); _writeSignal.Set(); _connectSignal.Set();
        _mgr.Remove(this);
    }

    internal void Abort() => Fail();

    // ---------------------------------------------------------------- Stream

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            lock (_gate)
            {
                if (_received.Count > 0)
                {
                    var copied = 0;
                    while (_received.Count > 0 && copied < buffer.Length)
                    {
                        var head = _received.Peek();
                        var n = Math.Min(head.Length - _receivedOffset, buffer.Length - copied);
                        head.AsSpan(_receivedOffset, n).CopyTo(buffer.Span.Slice(copied));
                        copied += n; _receivedOffset += n;
                        if (_receivedOffset == head.Length) { _received.Dequeue(); _receivedOffset = 0; }
                    }
                    var wasSmall = ReceiveCapacity - _receivedBytes < ReceiveCapacity / 4;
                    _receivedBytes -= copied;
                    if (wasSmall) _unackedReceived = Math.Max(_unackedReceived, 1);   // window opened: let the sender know soon
                    return copied;
                }
                if (_reset || _fatal) throw new IOException("The uTP connection was reset.");
                if (_remoteFin || _state == State.Closed) return 0;
            }
            await _readSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var position = 0;
        while (position < buffer.Length)
        {
            OutPacket? packet = null;
            lock (_gate)
            {
                if (_reset || _fatal) throw new IOException("The uTP connection was reset.");
                if (_state != State.Connected) throw new IOException("The uTP connection is closed.");
                var window = Math.Min(_maxWindow, (double)_peerWindow);
                var size = Math.Min(UtpManager.MaxPayload, buffer.Length - position);
                if (_inFlightBytes + size <= window || _inFlight.Count == 0)
                {
                    packet = new OutPacket { Seq = _seqNr, Type = UtpManager.StData, Payload = buffer.Slice(position, size).ToArray() };
                    _seqNr = unchecked((ushort)(_seqNr + 1));
                    _inFlight.AddLast(packet); _inFlightBytes += size; BytesSent += size;
                    position += size;
                }
            }
            if (packet != null) SendPacket(packet);
            else await _writeSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OutPacket? fin = null;
            lock (_gate)
            {
                if (_state == State.Connected)
                {
                    fin = new OutPacket { Seq = _seqNr, Type = UtpManager.StFin };
                    _seqNr = unchecked((ushort)(_seqNr + 1));
                    _inFlight.AddLast(fin);
                    _state = State.Closing; _closingSinceMs = NowMs;
                }
                else if (_state != State.Closing) _state = State.Closed;
            }
            if (fin != null) SendPacket(fin);
            _readSignal.Set(); _writeSignal.Set(); _connectSignal.Set();
            bool closed; lock (_gate) closed = _state == State.Closed;
            if (closed) _mgr.Remove(this);
        }
        base.Dispose(disposing);
    }
}
