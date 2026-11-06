using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MakanDownloadManager.Services.Torrent;

/// <summary>uTP (BEP 29) over real UDP sockets on this computer, with optional packet loss and reordering in between.</summary>
static class UtpTests
{
    sealed class Node : IDisposable
    {
        public readonly UdpClient Socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        public readonly UtpManager Manager;
        public readonly CancellationTokenSource Stop = new();
        public IPEndPoint EndPoint => (IPEndPoint)Socket.Client.LocalEndPoint!;
        public Node(double loss, double reorder, int seed)
        {
            var rng = new Random(seed); var gate = new object();
            Socket.Client.ReceiveBufferSize = 4 << 20; Socket.Client.SendBufferSize = 4 << 20;
            Manager = new UtpManager((buffer, length, to) =>
            {
                bool drop, delay; int wait;
                lock (gate) { drop = rng.NextDouble() < loss; delay = rng.NextDouble() < reorder; wait = rng.Next(1, 15); }
                if (drop) return;
                if (delay) _ = Task.Delay(wait).ContinueWith(_ => { try { Socket.Client.SendTo(buffer, 0, length, SocketFlags.None, to); } catch (Exception) { } });
                else Socket.Client.SendTo(buffer, 0, length, SocketFlags.None, to);
            });
            _ = Task.Run(async () =>
            {
                while (!Stop.IsCancellationRequested)
                {
                    try { var r = await Socket.ReceiveAsync(Stop.Token); Manager.HandlePacket(r.Buffer, r.Buffer.Length, r.RemoteEndPoint); }
                    catch (OperationCanceledException) { return; } catch (ObjectDisposedException) { return; } catch (SocketException) { }
                }
            });
        }
        public void Dispose() { Stop.Cancel(); Manager.Dispose(); Socket.Dispose(); }
    }

    sealed class Pair : IDisposable
    {
        public readonly Node A, B;
        readonly TaskCompletionSource<UtpStream> _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Pair(double loss = 0, double reorder = 0)
        {
            A = new Node(loss, reorder, 1); B = new Node(loss, reorder, 2);
            B.Manager.Accepted += s => _accepted.TrySetResult(s);
        }
        public async Task<(UtpStream Client, UtpStream Server)> ConnectAsync()
        {
            var client = await A.Manager.ConnectAsync(B.EndPoint, TimeSpan.FromSeconds(10), CancellationToken.None);
            var server = await _accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return (client, server);
        }
        public void Dispose() { A.Dispose(); B.Dispose(); }
    }

    static async Task<byte[]> ReadAll(Stream s, int count, double seconds = 60)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buffer = new byte[count]; var got = 0; var calls = 0; var sw0 = Stopwatch.StartNew();
        try { while (got < count) { var n = await s.ReadAsync(buffer.AsMemory(got), cts.Token); calls++; if (n == 0) break; got += n; } }
        catch (OperationCanceledException) { Console.WriteLine($"  reader gave up after {got} of {count} bytes, {calls} reads, {sw0.ElapsedMilliseconds} ms"); throw; }
        return got == count ? buffer : buffer[..got];
    }

    public static async Task Run()
    {
        await Section("uTP: connect, send and receive both ways", Basics);
        await Section("uTP: 16 MB each way at the same time is byte-exact and fast", Bulk);
        await Section("uTP: 5 % packet loss and reordering still deliver every byte, in order", LossAndReorder);
        await Section("uTP: closing ends the other side's reading after all data; a refused or unanswered connection fails", Closing);
        await Section("uTP: a reader that stops reading holds the writer back (flow control); 20 connections at once", FlowAndMany);
        await Section("uTP: packets that are not uTP are ignored", NotUtp);
    }

    static async Task Section(string title, Func<Task> body)
    {
        Console.WriteLine("\n== " + title);
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static async Task Basics()
    {
        using var pair = new Pair();
        var (c, s) = await pair.ConnectAsync();
        T.Check("a connection is made and the other side is told", c != null && s != null);
        await c.WriteAsync("hello"u8.ToArray());
        T.Check("data reaches the other side", System.Text.Encoding.ASCII.GetString(await ReadAll(s, 5)) == "hello");
        await s.WriteAsync("and back"u8.ToArray());
        T.Check("and the answer comes back", System.Text.Encoding.ASCII.GetString(await ReadAll(c, 8)) == "and back");
        var big = RandomNumberGenerator.GetBytes(5000);
        await c.WriteAsync(big);
        T.Check("a write larger than one packet is split and put together again", (await ReadAll(s, 5000)).AsSpan().SequenceEqual(big));
        c.Dispose(); s.Dispose();
    }

    static async Task Bulk()
    {
        using var pair = new Pair();
        var (c, s) = await pair.ConnectAsync();
        const int size = 16 * 1024 * 1024;
        var up = RandomNumberGenerator.GetBytes(size); var down = RandomNumberGenerator.GetBytes(size);
        var sw = Stopwatch.StartNew();
        var send1 = Task.Run(async () => { for (var i = 0; i < size; i += 65536) await c.WriteAsync(up.AsMemory(i, Math.Min(65536, size - i))); });
        var send2 = Task.Run(async () => { for (var i = 0; i < size; i += 65536) await s.WriteAsync(down.AsMemory(i, Math.Min(65536, size - i))); });
        var read1 = ReadAll(s, size); var read2 = ReadAll(c, size);
        try { await Task.WhenAll(send1, send2, read1, read2); }
        catch (Exception) { Console.WriteLine("  client: " + c.Diagnostics); Console.WriteLine("  server: " + s.Diagnostics); throw; }
        var seconds = sw.Elapsed.TotalSeconds;
        T.Check("16 MB client -> server is byte-exact", (await read1).AsSpan().SequenceEqual(up));
        T.Check("16 MB server -> client is byte-exact", (await read2).AsSpan().SequenceEqual(down));
        var rate = 2.0 * size / 1048576 / seconds;
        T.Check($"throughput on this computer is at least 5 MB/s in total ({rate:0} MB/s)", rate >= 5, $"{rate:0.0} MB/s");
        Console.WriteLine($"        (uTP loopback: {rate:0} MB/s total, {pair.A.Manager.PacketsSent + pair.B.Manager.PacketsSent} packets, {pair.A.Manager.PacketsRetransmitted + pair.B.Manager.PacketsRetransmitted} resent)");
        c.Dispose(); s.Dispose();
    }

    static async Task LossAndReorder()
    {
        using var pair = new Pair(loss: 0.05, reorder: 0.10);
        var (c, s) = await pair.ConnectAsync();
        const int size = 3 * 1024 * 1024;
        var data = RandomNumberGenerator.GetBytes(size);
        var sender = Task.Run(async () => { for (var i = 0; i < size; i += 32768) await c.WriteAsync(data.AsMemory(i, Math.Min(32768, size - i))); });
        byte[] received;
        try { received = await ReadAll(s, size, 40); } catch (Exception) { Console.WriteLine("  client: " + c.Diagnostics); Console.WriteLine("  server: " + s.Diagnostics); throw; }
        await sender;
        T.Check("3 MB arrive complete and in order although 5 % of the packets were lost and 10 % arrived late", received.Length == size && received.AsSpan().SequenceEqual(data), received.Length.ToString());
        T.Check("lost packets were sent again", pair.A.Manager.PacketsRetransmitted > 0, pair.A.Manager.PacketsRetransmitted.ToString());
        c.Dispose(); s.Dispose();
    }

    static async Task Closing()
    {
        using (var pair = new Pair())
        {
            var (c, s) = await pair.ConnectAsync();
            var data = RandomNumberGenerator.GetBytes(300_000);
            await c.WriteAsync(data);
            c.Dispose();
            var got = await ReadAll(s, 400_000, 20);
            T.Check("all data sent before closing arrives, then reading ends (end of stream)", got.Length == 300_000 && got.AsSpan().SequenceEqual(data), got.Length.ToString());
            s.Dispose();
        }
        using (var pair = new Pair())
        {
            pair.B.Manager.AcceptIncoming = false;
            var refused = false;
            try { await pair.A.Manager.ConnectAsync(pair.B.EndPoint, TimeSpan.FromSeconds(8), CancellationToken.None); }
            catch (IOException) { refused = true; }
            catch (TimeoutException) { refused = false; }
            T.Check("a peer that does not accept uTP answers with a reset and the connect fails at once", refused);
        }
        using (var a = new Node(0, 0, 3))
        {
            var dead = new IPEndPoint(IPAddress.Loopback, 9);   // nothing answers on the discard port
            var sw = Stopwatch.StartNew(); var timedOut = false;
            try { await a.Manager.ConnectAsync(dead, TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (TimeoutException) { timedOut = true; }
            catch (IOException) { timedOut = true; }
            T.Check("connecting to nobody gives up after the time given", timedOut && sw.Elapsed.TotalSeconds is > 1.5 and < 6, $"{sw.Elapsed.TotalSeconds:0.0}s");
            T.Check("a failed connect leaves no connection behind", a.Manager.ConnectionCount == 0, a.Manager.ConnectionCount.ToString());
        }
    }

    static async Task FlowAndMany()
    {
        using (var pair = new Pair())
        {
            var (c, s) = await pair.ConnectAsync();
            const int size = 8 * 1024 * 1024;
            var data = RandomNumberGenerator.GetBytes(size);
            var writer = Task.Run(async () => { for (var i = 0; i < size; i += 65536) await c.WriteAsync(data.AsMemory(i, 65536)); });
            await Task.Delay(1500);
            T.Check("a reader that has not read anything stops the writer instead of filling memory (flow control)", !writer.IsCompleted);
            var got = await ReadAll(s, size, 60);
            await writer;
            T.Check("when the reader starts, everything arrives intact", got.Length == size && got.AsSpan().SequenceEqual(data));
            c.Dispose(); s.Dispose();
        }
        using (var a = new Node(0, 0, 4)) using (var b = new Node(0, 0, 5))
        {
            var servers = new System.Collections.Concurrent.ConcurrentBag<UtpStream>();
            b.Manager.Accepted += s => { servers.Add(s); _ = Task.Run(async () => { var buf = await ReadAll(s, 200_000, 30); await s.WriteAsync(buf); }); };
            var tasks = Enumerable.Range(0, 20).Select(async i =>
            {
                var c = await a.Manager.ConnectAsync(b.EndPoint, TimeSpan.FromSeconds(10), CancellationToken.None);
                var payload = RandomNumberGenerator.GetBytes(200_000);
                await c.WriteAsync(payload);
                var echo = await ReadAll(c, 200_000, 30);
                c.Dispose();
                return echo.AsSpan().SequenceEqual(payload);
            }).ToList();
            var results = await Task.WhenAll(tasks);
            T.Check("20 connections between the same two sockets each get their own bytes back (echo)", results.All(x => x), results.Count(x => x).ToString());
        }
    }

    static async Task NotUtp()
    {
        using var node = new Node(0, 0, 6);
        var handled = node.Manager.HandlePacket("d1:y1:qe"u8.ToArray(), 8, new IPEndPoint(IPAddress.Loopback, 1));
        T.Check("a DHT message is not taken for uTP", !handled);
        T.Check("a too short datagram is not taken for uTP", !node.Manager.HandlePacket(new byte[5], 5, new IPEndPoint(IPAddress.Loopback, 1)));
        var junk = new byte[40]; junk[0] = 0x71;
        T.Check("an unknown packet type or version is not taken for uTP", !node.Manager.HandlePacket(junk, 40, new IPEndPoint(IPAddress.Loopback, 1)));
        var stray = new byte[20]; stray[0] = 0x01; stray[3] = 7;      // a DATA packet for a connection we do not have
        T.Check("a packet for an unknown connection is answered with a reset, not a crash", node.Manager.HandlePacket(stray, 20, new IPEndPoint(IPAddress.Loopback, 1)));
        await Task.CompletedTask;
    }
}
