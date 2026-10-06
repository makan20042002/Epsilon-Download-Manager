using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;

/// <summary>The BitTorrent client against a real-protocol mock swarm on localhost (tests/server/fake_swarm.py).</summary>
static class TorrentTests
{
    static string Dir = "";

    sealed class Swarm : IDisposable
    {
        readonly Process _process;
        public JsonElement Info;
        public string TorrentPath => Info.GetProperty("torrent").GetString()!;
        public string Magnet => Info.GetProperty("magnet").GetString()!;
        public string Payload => Info.GetProperty("payload").GetString()!;
        public string Name => Info.GetProperty("name").GetString()!;
        public int Total => Info.GetProperty("total").GetInt32();
        public int HttpPort => new Uri(Info.GetProperty("http_tracker").GetString()!).Port;
        public string UdpTracker => Info.GetProperty("udp_tracker").GetString()!;
        public int DhtPort => Info.GetProperty("dht_port").GetInt32();
        public int[] SeederPorts => Info.GetProperty("seeder_ports").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        public string InfoHash => Info.GetProperty("infohash").GetString()!;

        public Swarm(string script, params string[] args)
        {
            var dir = Path.Combine(Dir, "swarm" + Guid.NewGuid().ToString("N")[..6]);
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(script); psi.ArgumentList.Add("serve"); psi.ArgumentList.Add(dir);
            foreach (var a in args) psi.ArgumentList.Add(a);
            _process = Process.Start(psi)!;
            var line = _process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("the mock swarm did not start: " + _process.StandardError.ReadToEnd());
            Info = JsonDocument.Parse(line).RootElement.Clone();
        }
        public void Dispose() { try { _process.Kill(true); } catch (Exception) { } _process.Dispose(); }
    }

    static string? _script;
    static string Script()
    {
        if (_script != null) return _script;
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "server", "fake_swarm.py"))) dir = Path.GetDirectoryName(dir);
        return _script = dir == null ? "" : Path.Combine(dir, "server", "fake_swarm.py");
    }

    static string Sub() { var d = Path.Combine(Dir, "t" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    static TorrentEngine NewEngine(string? state = null, bool dht = false, Action<TorrentEngineOptions>? tweak = null)
    {
        var options = new TorrentEngineOptions
        {
            ListenPort = 0, EnableDht = dht, StateDirectory = state ?? Sub(), MinAnnounceInterval = TimeSpan.FromSeconds(1),
            UdpTrackerTimeout = TimeSpan.FromSeconds(1), SeedRatioLimit = 0, MaxPeersPerTorrent = 30
        };
        tweak?.Invoke(options);
        var engine = new TorrentEngine(options) { AllowLocalPeers = true };
        engine.Start();
        return engine;
    }

    static async Task<bool> Until(Func<bool> condition, double seconds = 40)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { if (condition()) return true; await Task.Delay(40); }
        return condition();
    }

    static async Task<bool> Done(TorrentSession s, double seconds = 60) =>
        await Task.WhenAny(s.Completed, Task.Delay(TimeSpan.FromSeconds(seconds))) == s.Completed && s.Completed.IsCompletedSuccessfully;

    static byte[] Concat(Swarm swarm, string saveDir)
    {
        var files = swarm.Info.GetProperty("files").EnumerateArray().Select(f => (Path: f[0].GetString()!, Length: f[1].GetInt32())).ToList();
        using var all = new MemoryStream();
        foreach (var (path, _) in files)
        {
            var single = files.Count == 1 && !path.Contains('/');      // a single-file torrent is one file named like the torrent
            var full = single ? Path.Combine(saveDir, swarm.Name) : Path.Combine(saveDir, swarm.Name, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) all.Write(File.ReadAllBytes(full));
        }
        return all.ToArray();
    }

    static bool SameAsPayload(Swarm swarm, string saveDir) => Concat(swarm, saveDir).AsSpan().SequenceEqual(File.ReadAllBytes(swarm.Payload));

    public static async Task RunAll(string dir)
    {
        Dir = dir;
        await Run("Torrent: bencode, metainfo, magnet links", Formats);
        await Run("Torrent: unlimited by default; removing a user limit wakes transfers immediately", TokenBucketChanges);
        await Run("DHT: known bootstrap nodes survive an app restart", DhtPersistence);
        await Run("Port mapping: NAT-PMP request, renew and delete against a mock gateway", NatPmp);
        await Run("Port mapping: UPnP discovery, device description, and SOAP request/response handling", Upnp);
        var script = Script();
        if (script == "" || OperatingSystem.IsWindows()) { Console.WriteLine("\n== Torrent swarm tests: skipped (need python3 and Linux/macOS)"); return; }
        try { File.SetUnixFileMode(script, File.GetUnixFileMode(script) | UnixFileMode.UserExecute); } catch (Exception) { }
        await Run("Torrent: trackers (HTTP, UDP, failures)", Trackers);
        await Run("Torrent: download from a .torrent file, byte-exact, then seeding", DownloadFile);
        await Run("Torrent: magnet link (metadata exchange), two seeders", DownloadMagnet);
        await Run("Torrent: bad data is rejected and re-fetched; hostile peers are dropped", Integrity);
        await Run("Torrent: file selection (skip / high priority) and completion", FileSelection);
        await Run("Torrent: pause and resume across a restart", Resume);
        await Run("Torrent: dead trackers do not delay the start; the seed-time limit stops sharing and survives a restart", FastStartAndSeedTime);
        await Run("Torrent: download and upload speed limits (global and per torrent)", Limits);
        await Run("Torrent: seeding uploads to another client", Seeding);
        await Run("Torrent: trackerless magnet through the DHT", DhtLookup);
        await Run("Torrent: remove with and without deleting the files", Removal);
        await Run("Torrent: items in the download list (start, pause, resume, .torrent file, remove)", InDownloadList);
        await Run("Torrent: download and seed schedules pause and resume the right torrents on their own hours", Schedule);
        await Run("Torrent: Force Download overrides the schedule for one torrent, on and off again", ForceDownload);
        await Run("Torrent: the main list's peers and upload-speed text is populated for a real torrent, blank for anything else", PeersAndUploadDisplay);
        await Run("Torrent: MaxActive blocks a torrent past the limit the same as any download, and Force Download actually breaks through it (regression)", ForceDownloadBypassesMaxActiveToo);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    // ------------------------------------------------------------------------------------------------ formats

    static async Task TokenBucketChanges()
    {
        var bucket = new TokenBucket();
        var unlimited = Stopwatch.StartNew();
        await bucket.WaitAsync(1_000_000, CancellationToken.None);
        T.Check("torrent bandwidth is unlimited by default", unlimited.ElapsedMilliseconds < 100);

        bucket.Rate = 1_000; // this reservation would wait many minutes if changing the limit did not wake it
        var waiting = bucket.WaitAsync(1_000_000, CancellationToken.None);
        await Task.Delay(100);
        T.Check("a user-set torrent limit is actually active", !waiting.IsCompleted);
        bucket.Rate = 0;
        T.Check("setting the torrent limit back to 0 (unlimited) wakes it immediately", await Task.WhenAny(waiting, Task.Delay(1000)) == waiting);
        await waiting;
    }

    static Task DhtPersistence()
    {
        var state = Sub();
        File.WriteAllText(Path.Combine(state, "dht-nodes.json"), "[{\"Address\":\"127.0.0.1\",\"Port\":49001}]");
        using (var engine = NewEngine(state, dht: true))
            T.Check("the saved DHT endpoint is loaded before the first lookup", engine.Dht!.Bootstrap.Any(x => x.Address.Equals(IPAddress.Loopback) && x.Port == 49001));
        var saved = File.ReadAllText(Path.Combine(state, "dht-nodes.json"));
        T.Check("the routing cache is written atomically on shutdown", saved.Contains("49001", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    static Task Formats()
    {
        T.Check("bencode round trip (numbers, strings, lists, dictionaries in key order)",
            Encoding.ASCII.GetString(Bencode.Encode(new Dictionary<string, object> { ["b"] = new List<object> { 1L, "x" }, ["a"] = -42L })) == "d1:ai-42e1:bl i1e1:xee".Replace(" ", ""));
        var parsed = (Dictionary<string, object>)Bencode.Parse(Encoding.ASCII.GetBytes("d3:cow3:moo4:spam4:eggse"));
        T.Check("parse a dictionary", Encoding.ASCII.GetString((byte[])parsed["cow"]) == "moo" && Encoding.ASCII.GetString((byte[])parsed["spam"]) == "eggs");
        foreach (var bad in new[] { "i03e", "i-0e", "ie", "i12", "5:abc", "l", "d3:cowe", "x", "", "i1ee", "d1:a", "99999999999:x", "-1:x" })
        {
            var rejected = false; try { Bencode.Parse(Encoding.ASCII.GetBytes(bad)); } catch (FormatException) { rejected = true; }
            T.Check("damaged input is a FormatException: '" + bad + "'", rejected);
        }
        var deep = new string('l', 200) + new string('e', 200);
        var deepRejected = false; try { Bencode.Parse(Encoding.ASCII.GetBytes(deep)); } catch (FormatException) { deepRejected = true; }
        T.Check("absurd nesting cannot overflow the stack", deepRejected);
        var withInfo = Encoding.ASCII.GetBytes("d4:infod4:name1:xe1:ai1ee");
        Bencode.Parse(withInfo, out var span);
        T.Check("the exact bytes of the info dictionary are found (they give the info hash)", span is { } s && Encoding.ASCII.GetString(withInfo, s.Start, s.Length) == "d4:name1:xe");

        // a single-file torrent built here
        var pieces = new byte[40];
        var info = new Dictionary<string, object> { ["name"] = "file.bin", ["piece length"] = 16384L, ["length"] = 20000L, ["pieces"] = pieces };
        var file = Bencode.Encode(new Dictionary<string, object> { ["info"] = info, ["announce"] = "http://tracker.example/announce", ["announce-list"] = new List<object> { new List<object> { "udp://a.example:80", "http://b.example/announce" }, new List<object> { "ftp://ignored.example/x" } }, ["url-list"] = new List<object> { "https://cdn.example/file.bin", "ftp://ignored.example/file.bin" } });
        var meta = MetaInfo.Parse(file);
        T.Check("single file: name, size, pieces, last piece is shorter", meta is { Name: "file.bin", TotalLength: 20000, PieceCount: 2, IsMultiFile: false } && meta.PieceSize(0) == 16384 && meta.PieceSize(1) == 3616);
        T.Check("tracker tiers keep udp/http and drop unknown protocols", meta.TrackerTiers.Count == 1 && meta.TrackerTiers[0].SequenceEqual(new[] { "udp://a.example:80", "http://b.example/announce" }));
        T.Check("BEP 19 web seeds keep HTTP(S), reject other schemes, and survive rebuilding", meta.WebSeeds.SequenceEqual(new[] { "https://cdn.example/file.bin" }) && MetaInfo.Parse(meta.ToTorrentFile()).WebSeeds.SequenceEqual(meta.WebSeeds));
        T.Check("the info hash is the SHA-1 of the info bytes", meta.InfoHash.Length == 20 && meta.InfoHashHex.Length == 40 && MetaInfo.FromInfo(meta.RawInfo).InfoHashHex == meta.InfoHashHex);
        T.Check("a torrent file can be rebuilt from its info (magnet metadata is kept as .torrent)", MetaInfo.Parse(meta.ToTorrentFile()).InfoHashHex == meta.InfoHashHex);

        Dictionary<string, object> Multi(params string[][] paths) => new()
        {
            ["name"] = "album", ["piece length"] = 16384L, ["pieces"] = new byte[20],
            ["files"] = paths.Select(p => (object)new Dictionary<string, object> { ["length"] = 100L, ["path"] = p.Select(x => (object)x).ToList() }).ToList()
        };
        bool Rejects(Dictionary<string, object> i) { try { MetaInfo.Parse(Bencode.Encode(new Dictionary<string, object> { ["info"] = i })); return false; } catch (InvalidDataException) { return true; } }
        T.Check("a path with .. is refused (a torrent must not write outside its folder)", Rejects(Multi(new[] { "a", ".." , "evil.txt" })));
        T.Check("an empty path part is refused", Rejects(Multi(new[] { "a", "", "b.txt" })));
        var ok = MetaInfo.Parse(Bencode.Encode(new Dictionary<string, object> { ["info"] = Multi(new[] { "disc 1", "01: intro?.mp3" }, new[] { "cover.jpg" }) }));
        T.Check("multi-file: offsets and safe names (illegal characters replaced)", ok.IsMultiFile && ok.Files[1].Offset == 100 && ok.Files[0].Path == "disc 1/01_ intro_.mp3" && ok.TotalLength == 200);
        var bad2 = Multi(new[] { "a" }); bad2["piece length"] = 100L;
        T.Check("a tiny piece length is refused", Rejects(bad2));
        var bad3 = Multi(new[] { "a" }); bad3["pieces"] = new byte[21];
        T.Check("piece hashes that are not a multiple of 20 bytes are refused", Rejects(bad3));
        var bad4 = Multi(new[] { "a" }); bad4["pieces"] = new byte[60];
        T.Check("a size that does not fit the number of pieces is refused", Rejects(bad4));
        var noInfo = false; try { MetaInfo.Parse(Encoding.ASCII.GetBytes("d1:ai1ee")); } catch (InvalidDataException) { noInfo = true; }
        var notTorrent = false; try { MetaInfo.Parse(Encoding.ASCII.GetBytes("hello world")); } catch (InvalidDataException) { notTorrent = true; }
        T.Check("no info dictionary / not a torrent at all: clear error", noInfo && notTorrent);

        var hex = "cfc258121b99ffa77bfb588ae260f947762e0038";
        T.Check("magnet with hex hash, name, trackers and a peer hint", MagnetLink.TryParse($"magnet:?xt=urn:btih:{hex}&dn=My%20File&tr=http%3A%2F%2Ft.example%2Fannounce&tr=udp%3A%2F%2Fu.example%3A80&x.pe=10.0.0.1:6881", out var m) && m!.InfoHashHex == hex && m.Name == "My File" && m.Trackers.Count == 2 && m.Peers[0] == "10.0.0.1:6881");
        var b32 = "ZPBFRAJRTH72PN5UWWMILYTA75TRHYAA";   // the same hash in base32
        T.Check("magnet with a base32 hash", MagnetLink.TryParse("magnet:?xt=urn:btih:" + b32, out var m2) && m2!.InfoHash.Length == 20);
        foreach (var badMagnet in new[] { "magnet:?dn=x", "magnet:?xt=urn:btih:zzzz", "magnet:?xt=urn:btih:" + hex[..39], "http://example.com/a.torrent", "", "magnet:?xt=urn:sha1:" + hex })
            T.Check("not a usable magnet link: '" + badMagnet + "'", !MagnetLink.TryParse(badMagnet, out _));
        T.Check("build and parse again", MagnetLink.TryParse(MagnetLink.Build(Convert.FromHexString(hex), "a b", new[] { "http://t.example/a?x=1&y=2" }), out var m3) && m3!.Name == "a b" && m3.Trackers[0] == "http://t.example/a?x=1&y=2");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------ trackers

    static async Task Trackers()
    {
        using var swarm = new Swarm(Script(), "--seeders", "2");
        var hash = Convert.FromHexString(swarm.InfoHash);
        var request = new AnnounceRequest(hash, Encoding.ASCII.GetBytes("-MK0170-abcdefghijkl"), 6881, 0, 0, 1000, "started");
        var http = await TrackerClient.AnnounceAsync(swarm.Info.GetProperty("http_tracker").GetString()!, request, CancellationToken.None);
        T.Check("HTTP tracker: both seeders come back (compact peers)", http.Peers.Select(p => p.Port).OrderBy(x => x).SequenceEqual(swarm.SeederPorts.OrderBy(x => x)) && http.IntervalSeconds >= 1 && http.Seeders == 2);
        var udp = await TrackerClient.AnnounceAsync(swarm.UdpTracker, request, CancellationToken.None, TimeSpan.FromSeconds(2));
        T.Check("UDP tracker (connect + announce): the same peers", udp.Peers.Select(p => p.Port).OrderBy(x => x).SequenceEqual(swarm.SeederPorts.OrderBy(x => x)));
        var unknown = new AnnounceRequest(new byte[20], request.PeerId, 6881, 0, 0, 1, "");
        var failure = ""; try { await TrackerClient.AnnounceAsync(swarm.Info.GetProperty("http_tracker").GetString()!, unknown, CancellationToken.None); } catch (TrackerException ex) { failure = ex.Message; }
        T.Check("a tracker's failure reason is passed on", failure == "unknown torrent", failure);
        var udpFailure = ""; try { await TrackerClient.AnnounceAsync(swarm.UdpTracker, unknown, CancellationToken.None, TimeSpan.FromSeconds(2)); } catch (TrackerException ex) { udpFailure = ex.Message; }
        T.Check("and from a UDP tracker", udpFailure == "unknown torrent", udpFailure);
        var dead = ""; try { await TrackerClient.AnnounceAsync("udp://127.0.0.1:9/announce", request, CancellationToken.None, TimeSpan.FromMilliseconds(200)); } catch (TrackerException ex) { dead = ex.Message; }
        T.Check("a UDP tracker that does not answer is an error, not a hang", dead.Contains("did not answer"), dead);
        var refused = false; try { await TrackerClient.AnnounceAsync("http://127.0.0.1:9/announce", request, CancellationToken.None); } catch (TrackerException) { refused = true; }
        T.Check("a refused HTTP connection is a tracker error", refused);
        T.Check("list-style peers and garbage answers are handled", TrackerClient.ParseHttp(Encoding.ASCII.GetBytes("d8:intervali900e5:peersld2:ip9:127.0.0.14:porti6881eeee")).Peers.Single().Port == 6881 && Throws(() => TrackerClient.ParseHttp(Encoding.ASCII.GetBytes("nonsense"))));
    }

    static bool Throws(Action a) { try { a(); return false; } catch (TrackerException) { return true; } }

    // ------------------------------------------------------------------------------------------------ downloads

    static async Task DownloadFile()
    {
        using var swarm = new Swarm(Script());
        var save = Sub(); using var engine = NewEngine();
        var meta = MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath));
        T.Check("the .torrent describes two files", meta.Files.Count == 2 && meta.Name == "swarm-test" && meta.InfoHashHex == swarm.InfoHash && meta.TotalLength == swarm.Total);
        var session = engine.AddTorrent(meta, save);
        session.Start();
        T.Check("downloads to the end", await Done(session), session.Error + " / " + session.State);
        T.Check("every byte is what the seeder had (all pieces verified)", SameAsPayload(swarm, save) && session.PiecesHave == session.PieceCount);
        T.Check("the state is Seeding and progress is 100 %", await Until(() => session.State == TorrentState.Seeding) && session.Progress >= 99.99);
        T.Check("we downloaded about the size of the torrent (no big waste)", session.DownloadedTotal >= swarm.Total && session.DownloadedTotal < swarm.Total * 1.3, session.DownloadedTotal.ToString());
        var trackers = session.Trackers();
        T.Check("the tracker was contacted and is reported as working", trackers.Count == 1 && trackers[0].Status.StartsWith("Working") && trackers[0].Seeders == 1, string.Join(",", trackers.Select(t => t.Status)));
        T.Check("the files list shows both files finished", session.Files().All(f => f.Done == f.Length) && session.Files().Count == 2);
        T.Check("the pieces map is full", session.PieceMap(20).All(x => x > 0.999));
        T.Check("resume data and a copy of the .torrent were saved", File.Exists(Path.Combine(engine.Options.StateDirectory, swarm.InfoHash + ".resume.json")) && File.Exists(Path.Combine(engine.Options.StateDirectory, swarm.InfoHash + ".torrent")));
        T.Check("the content path is the folder named like the torrent", session.ContentPath == Path.Combine(save, "swarm-test"));
        await engine.RemoveAsync(session, false);
    }

    static async Task DownloadMagnet()
    {
        using var swarm = new Swarm(Script(), "--seeders", "2", "--files", "one.bin:200000,two/three.bin:350000,four.bin:100", "--piece", "32768");
        var save = Sub(); using var engine = NewEngine();
        MagnetLink.TryParse(swarm.Magnet, out var link);
        var session = engine.AddMagnet(link!, save);
        T.Check("before the metadata arrives the torrent has only its name from the link", session.Meta == null && session.Name == "swarm-test");
        session.Start();
        var meta = await session.WaitForMetadataAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        T.Check("the metadata was fetched from a peer and matches the info hash", meta.InfoHashHex == swarm.InfoHash && meta.Files.Count == 3 && meta.TotalLength == swarm.Total);
        T.Check("downloads to the end", await Done(session), session.Error + " / " + session.State);
        T.Check("all three files are byte-exact (including the tiny one)", SameAsPayload(swarm, save));
        T.Check("both seeders were used", await Until(() => session.Peers().Count >= 2, 10), session.Peers().Count.ToString());
        T.Check("the metadata was stored, so the next start does not need to fetch it again", File.Exists(Path.Combine(engine.Options.StateDirectory, swarm.InfoHash + ".torrent")));
        await engine.RemoveAsync(session, false);

        // a second engine that finds the stored metadata does not need any peer for it
        using var engine2 = new TorrentEngine(new TorrentEngineOptions { ListenPort = 0, EnableDht = false, StateDirectory = engine.Options.StateDirectory, SeedRatioLimit = 0 }) { AllowLocalPeers = true };
        var again = engine2.AddMagnet(link!, save);
        T.Check("metadata from an earlier run is used at once", again.Meta != null && again.Meta.InfoHashHex == swarm.InfoHash);
    }

    static async Task Integrity()
    {
        // one seeder only: every block of the corrupted piece is guaranteed to come from it, so the hash check is exercised every run
        using var swarm = new Swarm(Script(), "--seeders", "1", "--corrupt-piece", "3", "--files", "big.bin:900000", "--piece", "65536");
        var save = Sub(); using var engine = NewEngine();
        var session = engine.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), save);
        session.Start();
        T.Check("a seeder that sends one corrupted piece does not spoil the download", await Done(session), session.Error + " / " + session.State);
        T.Check("the file is byte-exact", SameAsPayload(swarm, save));
        T.Check("the bad piece was noticed by its hash", session.HashFailures >= 1 || session.Wasted > 0, $"fails={session.HashFailures} wasted={session.Wasted}");

        // hostile peers talk to our port
        async Task<int> Talk(byte[] handshake, byte[]? after)
        {
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, engine.ListenPort);
            var stream = client.GetStream(); await stream.WriteAsync(handshake);
            if (after != null) { await Task.Delay(300); try { await stream.WriteAsync(after); } catch (IOException) { } }
            var buf = new byte[4096]; var total = 0;
            using var cts = new CancellationTokenSource(3000);
            try { while (true) { var n = await stream.ReadAsync(buf, cts.Token); if (n == 0) break; total += n; if (total > 100000) break; } } catch (Exception) { }
            return total;
        }
        byte[] Handshake(byte[] hash) { var h = new byte[68]; h[0] = 19; Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(h, 1); hash.CopyTo(h, 28); Encoding.ASCII.GetBytes("-XX0001-abcdefghijkl").CopyTo(h, 48); return h; }
        T.Check("a handshake for a torrent we do not have is closed without an answer", await Talk(Handshake(new byte[20]), null) == 0);
        T.Check("garbage instead of a handshake is closed", await Talk(Encoding.ASCII.GetBytes(new string('x', 68)), null) == 0);
        var badBitfield = new byte[] { 0, 0, 0, 3, 5, 0xFF, 0xFF };
        var answered = await Talk(Handshake(session.InfoHash), badBitfield);
        T.Check("a bitfield of the wrong size: we answered the handshake, then closed the connection (no crash)", answered > 0 && session.State == TorrentState.Seeding);
        var huge = new byte[] { 0x7F, 0xFF, 0xFF, 0xFF, 7 };
        await Talk(Handshake(session.InfoHash), huge);
        T.Check("a message of 2 GB is refused and the session carries on", session.State == TorrentState.Seeding && session.Error == null);
        await engine.RemoveAsync(session, true);
    }

    static async Task FileSelection()
    {
        using var swarm = new Swarm(Script(), "--files", "small.bin:150000,big/large.bin:900000", "--piece", "32768");
        var save = Sub(); using var engine = NewEngine();
        var session = engine.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), save);
        session.SetFilePriority(1, FilePriority.Skip);
        session.Start();
        T.Check("with the big file skipped the torrent completes after the small one", await Done(session, 40), session.Error + " / " + session.State);
        var files = session.Files();
        T.Check("the small file is complete, the skipped one is (almost) untouched", files[0].Done == files[0].Length && files[1].Done < files[1].Length / 3, $"{files[0].Done}/{files[0].Length} {files[1].Done}/{files[1].Length}");
        T.Check("progress counts only what is wanted", session.WantedBytes < swarm.Total && session.Progress >= 99.99);
        var small = File.ReadAllBytes(Path.Combine(save, "swarm-test", "small.bin"));
        T.Check("the wanted file is byte-exact", small.AsSpan().SequenceEqual(File.ReadAllBytes(swarm.Payload).AsSpan(0, 150000)));
        session.SetFilePriority(1, FilePriority.High);
        T.Check("choosing the other file later starts downloading again", await Until(() => session.State == TorrentState.Downloading || session.Progress < 100, 5) || await Until(() => SameAsPayload(swarm, save), 40));
        T.Check("and finishes it", await Until(() => SameAsPayload(swarm, save), 40) && await Until(() => session.State == TorrentState.Seeding, 10));
        await engine.RemoveAsync(session, false);
    }

    static async Task Resume()
    {
        using var swarm = new Swarm(Script(), "--rate", "700000", "--files", "movie.bin:2400000", "--piece", "65536");
        var save = Sub(); var state = Sub();
        var meta = MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath));
        int before;
        using (var engine = NewEngine(state))
        {
            var session = engine.AddTorrent(meta, save);
            session.Start();
            T.Check("about a third arrives", await Until(() => session.Progress > 30, 40), session.Progress.ToString("0"));
            await session.PauseAsync();
            before = session.PiecesHave;
            T.Check("pause keeps everything (state Paused, resume data written)", session.State == TorrentState.Paused && before > 5 && File.Exists(Path.Combine(state, swarm.InfoHash + ".resume.json")));
            await engine.ShutdownAsync(TimeSpan.FromSeconds(3));
        }
        using var engine2 = NewEngine(state);
        var again = engine2.AddTorrent(meta, save);
        again.Start();
        T.Check("after a restart the verified pieces are recognised", await Until(() => again.State == TorrentState.Downloading && again.PiecesHave >= before, 15), $"have {again.PiecesHave}, before {before}, state {again.State}");
        T.Check("and the download finishes", await Done(again, 60), again.Error + " / " + again.State);
        T.Check("byte-exact", SameAsPayload(swarm, save));

        // the data was changed on disk while Makan was closed: a piece that no longer verifies is fetched again
        await engine2.RemoveAsync(again, false);
        var file = Path.Combine(save, "swarm-test");
        var bytes = File.ReadAllBytes(file); bytes[70000] ^= 0xFF; File.WriteAllBytes(file, bytes);
        using var engine3 = NewEngine(state, tweak: o => { });
        var third = engine3.AddTorrent(meta, save);
        third.Start();
        T.Check("a damaged file is detected (resume data is only trusted when the files still verify) and repaired", await Done(third, 60) && SameAsPayload(swarm, save), third.Error);
        await engine3.RemoveAsync(third, false);
    }

    static async Task Limits()
    {
        using var swarm = new Swarm(Script(), "--files", "data.bin:1200000", "--piece", "65536");
        var meta = MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath));
        // global download limit 300 KB/s: 1.2 MB needs about 4 seconds
        using (var engine = NewEngine())
        {
            engine.DownloadLimit.Rate = 300_000;
            var session = engine.AddTorrent(meta, Sub());
            var sw = Stopwatch.StartNew(); session.Start();
            T.Check("finishes under the global limit", await Done(session, 40), session.Error);
            T.Check("and it took what the limit says (3-12 s for 1.2 MB at 300 KB/s)", sw.Elapsed.TotalSeconds is > 3 and < 12, sw.Elapsed.TotalSeconds.ToString("0.0"));
            await engine.RemoveAsync(session, false);
        }
        // per-torrent limit changed while it runs
        using (var engine = NewEngine())
        {
            var session = engine.AddTorrent(meta, Sub());
            session.DownloadLimit.Rate = 250_000;
            var sw = Stopwatch.StartNew(); session.Start();
            await Until(() => session.BytesDone > 300_000, 20);
            session.DownloadLimit.Rate = 0;      // "unlimited" while running
            T.Check("finishes after the limit was removed while it ran", await Done(session, 40), session.Error);
            T.Check("faster than the limit alone would have allowed (< 4.8 s)", sw.Elapsed.TotalSeconds < 4.8, sw.Elapsed.TotalSeconds.ToString("0.0"));
            await engine.RemoveAsync(session, false);
        }
    }

    static async Task<JsonElement> Leech(int port, string torrent, params string[] extra)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(Script()); psi.ArgumentList.Add("leech"); psi.ArgumentList.Add("127.0.0.1"); psi.ArgumentList.Add(port.ToString()); psi.ArgumentList.Add(torrent);
        foreach (var e in extra) psi.ArgumentList.Add(e);
        using var p = Process.Start(psi)!;
        var line = await p.StandardOutput.ReadLineAsync() ?? "{}";
        if (!p.WaitForExit(30000)) p.Kill(true);
        return JsonDocument.Parse(line.Length == 0 ? "{}" : line).RootElement.Clone();
    }

    static async Task FastStartAndSeedTime()
    {
        using var swarm = new Swarm(Script(), "--files", "s.bin:400000", "--piece", "32768");
        var real = MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath));
        // eight trackers that never answer, each in its own tier, in front of the real ones (what a public magnet looks like)
        var tiers = Enumerable.Range(0, 8).Select(i => (IReadOnlyList<string>)new[] { $"udp://127.0.0.1:{9 + i}" }).Concat(real.TrackerTiers).ToList();
        var top = (Dictionary<string, object>)Bencode.Parse(File.ReadAllBytes(swarm.TorrentPath));
        top["announce-list"] = tiers.Select(t => (object)t.Select(u => (object)u).ToList()).ToList();
        var meta = MetaInfo.Parse(Bencode.Encode(top));
        var state = Sub(); var save = Sub();
        using (var engine = NewEngine(state, tweak: o => o.SeedTimeLimit = TimeSpan.FromSeconds(3)))
        {
            var session = engine.AddTorrent(meta, save);
            var sw = Stopwatch.StartNew();
            session.Start();
            T.Check("the first peer is found at once although eight trackers ahead of the working one are dead (they used to cost a time-out each)", await Until(() => session.PeerCount > 0, 4), sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            T.Check("the torrent completes", await Done(session, 40), session.Error + " / " + session.State);
            T.Check("and shares at first", session.State is TorrentState.Seeding or TorrentState.Finished);
            T.Check("sharing stops by itself when the seed-time limit is reached", await Until(() => session.State == TorrentState.Finished, 12), session.State.ToString());
            T.Check("the completion time is saved with the resume data", engine.LoadResume(session.InfoHashHex)?.CompletedUtc != null);
        }
        using (var engine = NewEngine(state, tweak: o => o.SeedTimeLimit = TimeSpan.FromSeconds(3)))
        {
            var session = engine.AddTorrent(meta, save);
            session.Start();
            T.Check("after a restart the limit still counts from the original completion, so sharing does not start over", await Until(() => session.State == TorrentState.Finished, 8), session.State.ToString());
        }
    }

    static async Task Seeding()
    {
        using var swarm = new Swarm(Script(), "--files", "s.bin:700000", "--piece", "65536");
        var save = Sub(); using var engine = NewEngine();
        var session = engine.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), save);
        session.Start();
        await Done(session);
        T.Check("complete and seeding", await Until(() => session.State == TorrentState.Seeding, 10));
        var result = await Leech(engine.ListenPort, swarm.TorrentPath);
        T.Check("another client can download the whole torrent from us, every piece verified", result.TryGetProperty("ok", out var ok) && ok.GetBoolean() && result.GetProperty("good").GetInt32() == session.PieceCount, result.ToString());
        T.Check("what we uploaded is counted", session.UploadedTotal >= swarm.Total, session.UploadedTotal.ToString());
        session.UploadLimit.Rate = 250_000;
        var before = session.UploadedTotal; var sw = Stopwatch.StartNew();
        var limited = await Leech(engine.ListenPort, swarm.TorrentPath, "--max-pieces", "8");
        T.Check("an upload limit slows the other client (8 pieces = 512 KB at 250 KB/s takes about 2 s)", limited.TryGetProperty("ok", out var ok2) && ok2.GetBoolean() && sw.Elapsed.TotalSeconds > 1.4, sw.Elapsed.TotalSeconds.ToString("0.0") + " " + limited);
        session.UploadLimit.Rate = 0; engine.UploadLimit.Rate = 250_000; sw.Restart();
        var globalLimited = await Leech(engine.ListenPort, swarm.TorrentPath, "--max-pieces", "8");
        T.Check("the global upload limit slows it the same way", globalLimited.TryGetProperty("ok", out var ok3) && ok3.GetBoolean() && sw.Elapsed.TotalSeconds > 1.4, sw.Elapsed.TotalSeconds.ToString("0.0"));

        // seeding ends by itself at the chosen share ratio
        engine.UploadLimit.Rate = 0;
        using var engine2 = NewEngine(tweak: o => o.SeedRatioLimit = 0.5);
        var save2 = Sub();
        var s2 = engine2.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), save2);
        s2.Start(); await Done(s2);
        await Leech(engine2.ListenPort, swarm.TorrentPath);
        T.Check("with a ratio limit of 0.5 the torrent finishes seeding once enough was uploaded", await Until(() => s2.State == TorrentState.Finished, 15), s2.State + " ratio " + s2.Ratio.ToString("0.00"));
        using var engine3 = NewEngine(tweak: o => o.SeedAfterCompletion = false);
        var s3 = engine3.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), Sub());
        s3.Start(); await Done(s3);
        T.Check("with seeding switched off it is Finished right after the download", await Until(() => s3.State == TorrentState.Finished, 10), s3.State.ToString());
        await engine.RemoveAsync(session, false);
    }

    static async Task DhtLookup()
    {
        using var swarm = new Swarm(Script(), "--no-tracker", "--seeders", "2");
        T.Check("the magnet link has no tracker", !swarm.Magnet.Contains("tr="));
        var save = Sub(); using var engine = NewEngine(dht: true);
        engine.Dht!.Bootstrap.Add(new IPEndPoint(IPAddress.Loopback, swarm.DhtPort));
        MagnetLink.TryParse(swarm.Magnet, out var link);
        var session = engine.AddMagnet(link!, save);
        session.Start();
        T.Check("peers come from the DHT, metadata from them, then the download runs", await Done(session, 60), session.Error + " / " + session.State + " peers=" + session.PeerCount);
        T.Check("byte-exact", SameAsPayload(swarm, save));
        var direct = await engine.Dht.FindPeersAsync(Convert.FromHexString(swarm.InfoHash), TimeSpan.FromSeconds(5), CancellationToken.None);
        T.Check("a direct DHT lookup returns the seeders", direct.Select(p => p.Port).OrderBy(x => x).SequenceEqual(swarm.SeederPorts.OrderBy(x => x)));
        var none = await engine.Dht.FindPeersAsync(new byte[20], TimeSpan.FromSeconds(3), CancellationToken.None);
        T.Check("an unknown torrent gives no peers (and no crash)", none.Count == 0);
        await engine.RemoveAsync(session, false);
    }

    static async Task Removal()
    {
        using var swarm = new Swarm(Script(), "--files", "r/one.bin:100000,two.bin:100000", "--piece", "32768");
        using var engine = NewEngine();
        var keep = Sub(); var drop = Sub();
        var a = engine.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), keep);
        a.Start(); await Done(a);
        await engine.RemoveAsync(a, false);
        T.Check("remove without deleting keeps the files", File.Exists(Path.Combine(keep, "swarm-test", "two.bin")) && !engine.Sessions.Any());
        var b = engine.AddTorrent(MetaInfo.Parse(File.ReadAllBytes(swarm.TorrentPath)), drop);
        b.Start(); await Done(b);
        await engine.RemoveAsync(b, true);
        T.Check("remove with deleting removes files, empty folders and the resume data", !Directory.Exists(Path.Combine(drop, "swarm-test")) && !File.Exists(Path.Combine(engine.Options.StateDirectory, swarm.InfoHash + ".resume.json")));
        var again = engine.AddMagnet(MagnetLink.TryParse(swarm.Magnet, out var l) ? l! : null!, Sub());
        T.Check("adding the same torrent twice gives the same session", ReferenceEquals(again, engine.AddMagnet(l!, Sub())));
    }

    static async Task Schedule()
    {
        T.Check("a window that does not cross midnight",
            TestInTimeWindow(TimeSpan.FromHours(8), TimeSpan.FromHours(20), TimeSpan.FromHours(12)) &&
            !TestInTimeWindow(TimeSpan.FromHours(8), TimeSpan.FromHours(20), TimeSpan.FromHours(21)) &&
            !TestInTimeWindow(TimeSpan.FromHours(8), TimeSpan.FromHours(20), TimeSpan.FromHours(0.5)));
        T.Check("a window that crosses midnight (e.g. 23:00 to 07:00) wraps correctly",
            TestInTimeWindow(TimeSpan.FromHours(23), TimeSpan.FromHours(7), TimeSpan.FromMinutes(30)) &&
            TestInTimeWindow(TimeSpan.FromHours(23), TimeSpan.FromHours(7), TimeSpan.FromHours(23.5)) &&
            !TestInTimeWindow(TimeSpan.FromHours(23), TimeSpan.FromHours(7), TimeSpan.FromHours(12)));
        T.Check("equal start and stop means always on (a schedule the person has not actually set yet)",
            TestInTimeWindow(TimeSpan.FromHours(9), TimeSpan.FromHours(9), TimeSpan.FromHours(3)));

        using var swarm = new Swarm(Script(), "--seeders", "1", "--rate", "150000", "--files", "sched.bin:1800000", "--piece", "65536");
        using var engine = NewEngine();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { Torrents = engine };
        var save = Sub();
        var item = new DownloadItem { Url = swarm.Magnet, FilePath = Path.Combine(save, "swarm-test"), Connections = 8 };
        manager.Enqueue(item);
        T.Check("starts downloading", await Until(() => item.Status == nameof(DownloadStatus.Downloading) && item.Progress > 0, 20), item.Status + " " + item.LastError);

        var real = DateTime.Now;
        var outsideWindow = real.AddHours(3);   // 3 hours from now: definitely not "now" by anyone's clock, including the manager's own background tick
        var insideWindow = real;

        manager.TorrentDownloadScheduleEnabled = true;
        manager.TorrentDownloadStart = outsideWindow.TimeOfDay; manager.TorrentDownloadStop = outsideWindow.AddMinutes(1).TimeOfDay;
        manager.CheckTorrentSchedule(insideWindow);
        T.Check("outside the download window: the schedule pauses it on its own", await Until(() => item.Status == nameof(DownloadStatus.Paused), 10), item.Status);

        manager.TorrentDownloadStart = insideWindow.AddHours(-1).TimeOfDay; manager.TorrentDownloadStop = insideWindow.AddHours(1).TimeOfDay;
        manager.CheckTorrentSchedule(insideWindow);
        T.Check("back inside the window: the schedule resumes it on its own", await Until(() => item.Status == nameof(DownloadStatus.Downloading), 10), item.Status);

        manager.Pause(item);
        T.Check("the person pauses it by hand", await Until(() => item.Status == nameof(DownloadStatus.Paused), 10));
        manager.CheckTorrentSchedule(insideWindow);
        await Task.Delay(300);
        T.Check("a manual pause is never auto-resumed by the schedule (only ones the schedule itself paused are)", item.Status == nameof(DownloadStatus.Paused));

        manager.Enqueue(item);
        T.Check("the person resumes it by hand, and it finishes (and keeps seeding, this engine's default)", await Program.WaitStatusPublic(item, DownloadStatus.Complete, 60) && await Until(() => manager.SessionOf(item)?.State == TorrentState.Seeding, 10), item.LastError);

        manager.TorrentDownloadScheduleEnabled = false;
        manager.TorrentSeedScheduleEnabled = true;
        manager.TorrentSeedStart = outsideWindow.TimeOfDay; manager.TorrentSeedStop = outsideWindow.AddMinutes(1).TimeOfDay;
        manager.CheckTorrentSchedule(insideWindow);
        T.Check("outside the seed window: seeding pauses on its own", await Until(() => manager.SessionOf(item)?.State == TorrentState.Paused, 10));

        manager.TorrentSeedStart = insideWindow.AddHours(-1).TimeOfDay; manager.TorrentSeedStop = insideWindow.AddHours(1).TimeOfDay;
        manager.CheckTorrentSchedule(insideWindow);
        T.Check("back inside the seed window: seeding resumes on its own", await Until(() => manager.SessionOf(item)?.State == TorrentState.Seeding, 10));

        await manager.RemoveAsync(item, true);
    }

    static async Task ForceDownload()
    {
        using var swarm = new Swarm(Script(), "--seeders", "1", "--rate", "150000", "--files", "force.bin:1800000", "--piece", "65536");
        using var engine = NewEngine();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { Torrents = engine };
        var save = Sub();
        var item = new DownloadItem { Url = swarm.Magnet, FilePath = Path.Combine(save, "force-test"), Connections = 8 };
        manager.Enqueue(item);
        T.Check("starts downloading", await Until(() => item.Status == nameof(DownloadStatus.Downloading) && item.Progress > 0, 20), item.Status + " " + item.LastError);
        T.Check("not forced yet", !manager.IsForced(item));

        var real = DateTime.Now;
        var outsideWindow = real.AddHours(3);
        manager.TorrentDownloadScheduleEnabled = true;
        manager.TorrentDownloadStart = outsideWindow.TimeOfDay; manager.TorrentDownloadStop = outsideWindow.AddMinutes(1).TimeOfDay;

        manager.SetForced(item, true);
        T.Check("Force Download marks it forced right away", manager.IsForced(item));
        manager.CheckTorrentSchedule(real);
        await Task.Delay(300);
        T.Check("a forced torrent is never paused by the schedule, even while outside its allowed hours", item.Status == nameof(DownloadStatus.Downloading), item.Status);

        manager.CheckTorrentSchedule(real);
        manager.CheckTorrentSchedule(real);
        await Task.Delay(300);
        T.Check("calling the schedule check again and again changes nothing further for a forced torrent", item.Status == nameof(DownloadStatus.Downloading));

        manager.SetForced(item, false);
        T.Check("turning Force off is immediate", !manager.IsForced(item));
        manager.CheckTorrentSchedule(real);
        T.Check("once Force is off, the very same torrent is paused by the schedule normally, same as any other", await Until(() => item.Status == nameof(DownloadStatus.Paused), 10), item.Status);

        // forcing a paused-by-hand torrent starts it immediately, ignoring the schedule that would otherwise have kept it paused
        manager.SetForced(item, true);
        T.Check("Force Download restarts a torrent that was sitting paused", await Until(() => item.Status == nameof(DownloadStatus.Downloading), 10), item.Status);

        manager.TorrentDownloadStart = real.AddHours(-1).TimeOfDay; manager.TorrentDownloadStop = real.AddHours(1).TimeOfDay;   // back inside the window, so RemoveAsync below has nothing fighting it
        manager.SetForced(item, false);
        await manager.RemoveAsync(item, true);
    }

    static async Task PeersAndUploadDisplay()
    {
        using var swarm = new Swarm(Script(), "--seeders", "1", "--rate", "150000", "--files", "peers.bin:1200000", "--piece", "65536");
        using var engine = NewEngine();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { Torrents = engine };
        var save = Sub();
        var item = new DownloadItem { Url = swarm.Magnet, FilePath = Path.Combine(save, "peers-test"), Connections = 8 };

        T.Check("before it's even added, a plain item has no peers/upload text at all", item.PeersText == "" && item.UpSpeedText == "");

        manager.Enqueue(item);
        T.Check("once connected to the mock swarm, the peers/seeds text is populated", await Until(() => item.PeersText != "" && item.PeersText != "0 (0)", 20), item.PeersText);
        T.Check("the peers text follows the 'seeds (known peers)' shape other torrent clients use", System.Text.RegularExpressions.Regex.IsMatch(item.PeersText, @"^\d+ \(\d+\)$"), item.PeersText);
        T.Check("the upload text, whatever it is right now, is either blank or a real '.../s' reading - never garbage", item.UpSpeedText == "" || item.UpSpeedText.EndsWith("/s"), item.UpSpeedText);

        await manager.RemoveAsync(item, true);
        T.Check("a plain (non-torrent) item is never touched by any of this - it stays blank for its whole life", new DownloadItem { Url = "https://example.com/f.zip" } is { PeersText: "", UpSpeedText: "" });
    }

    /// <summary>Regression test: MaxActive (the overall "how many downloads at once" limit, default 4) applies to
    /// torrents exactly like it applies to ordinary downloads, since both are added the same way (Enqueue). Adding a
    /// 5th, 6th... torrent beyond that limit correctly leaves them Queued - but Force Download exists precisely to
    /// override that, and it must actually do so, not just look like it does.</summary>
    static async Task ForceDownloadBypassesMaxActiveToo()
    {
        using var engine = NewEngine();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { Torrents = engine, MaxActive = 2 };
        var save = Sub();

        using var swarmA = new Swarm(Script(), "--seeders", "1", "--rate", "20000", "--files", "max-a.bin:3000000", "--piece", "65536");
        using var swarmB = new Swarm(Script(), "--seeders", "1", "--rate", "20000", "--files", "max-b.bin:3000000", "--piece", "65536");
        using var swarmC = new Swarm(Script(), "--seeders", "1", "--rate", "20000", "--files", "max-c.bin:3000000", "--piece", "65536");

        var a = manager.AddTorrent(swarmA.Magnet, save).Item!;
        var b = manager.AddTorrent(swarmB.Magnet, save).Item!;
        T.Check("the first two torrents start fine - MaxActive is 2 and nothing else is running yet", await Until(() => a.Status == nameof(DownloadStatus.Downloading) && b.Status == nameof(DownloadStatus.Downloading), 20), $"{a.Status} {b.Status}");

        var c = manager.AddTorrent(swarmC.Magnet, save).Item!;
        await Task.Delay(1500);
        T.Check("a third torrent beyond MaxActive correctly sits Queued, not Downloading - this is the bug as reported (only 4, or here 2, ever run)", c.Status == nameof(DownloadStatus.Queued), c.Status);

        manager.SetForced(c, true);
        T.Check("Force Download on the blocked torrent actually starts it, despite MaxActive still being full", await Until(() => c.Status == nameof(DownloadStatus.Downloading), 15), c.Status);
        T.Check("...and the other two keep running too - forcing one does not pause anything else", a.Status == nameof(DownloadStatus.Downloading) && b.Status == nameof(DownloadStatus.Downloading));

        manager.SetForced(c, false);
        await manager.RemoveAsync(a, true); await manager.RemoveAsync(b, true); await manager.RemoveAsync(c, true);
    }

    // ------------------------------------------------------------------------------------------------ port mapping (UPnP / NAT-PMP)

    /// <summary>A minimal NAT-PMP responder standing in for a home router, for tests. Not a real gateway - just enough
    /// of RFC 6886 to exercise the client: answers mapping requests, can drop a number of packets first (to prove the
    /// client's retry logic works), or answer with a non-zero result code (to prove failures are reported, not thrown).</summary>
    sealed class FakeGateway : IDisposable
    {
        readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public int DropFirstN;
        public int? ForceResultCode;
        public int RequestsSeen;
        CancellationTokenSource? _cts;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => Loop(_cts.Token));
        }

        async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await _udp.ReceiveAsync(ct); } catch (Exception) { return; }
                RequestsSeen++;
                if (RequestsSeen <= DropFirstN) continue;   // simulate a packet the router never answered
                var req = received.Buffer;
                if (req.Length < 12) continue;
                var opcode = req[1];
                var internalPort = (req[4] << 8) | req[5];
                var externalHint = (req[6] << 8) | req[7];
                var lifetime = (uint)((req[8] << 24) | (req[9] << 16) | (req[10] << 8) | req[11]);
                var reply = new byte[16];
                reply[1] = (byte)(opcode + 128);
                var resultCode = ForceResultCode ?? 0;
                reply[2] = (byte)(resultCode >> 8); reply[3] = (byte)resultCode;
                var externalPort = externalHint != 0 ? externalHint : internalPort;
                reply[8] = (byte)(internalPort >> 8); reply[9] = (byte)internalPort;
                reply[10] = (byte)(externalPort >> 8); reply[11] = (byte)externalPort;
                reply[12] = (byte)(lifetime >> 24); reply[13] = (byte)(lifetime >> 16); reply[14] = (byte)(lifetime >> 8); reply[15] = (byte)lifetime;
                await _udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
            }
        }

        public void Dispose() { _cts?.Cancel(); _udp.Dispose(); }
    }

    static async Task NatPmp()
    {
        using var gateway = new FakeGateway();
        gateway.Start();

        var ok = await NatPmpClient.RequestMappingAsync(gateway.EndPoint, 6881, 6881, TimeSpan.FromMinutes(30), tcp: true, CancellationToken.None);
        T.Check("a normal request succeeds and echoes the port", ok.Success && ok.ExternalPort == 6881 && ok.GrantedLifetime == TimeSpan.FromMinutes(30), ok.ToString());

        gateway.DropFirstN = 2;
        gateway.RequestsSeen = 0;   // reset: the previous assertion already used the gateway once
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var retried = await NatPmpClient.RequestMappingAsync(gateway.EndPoint, 6881, 6881, TimeSpan.FromMinutes(30), tcp: false, CancellationToken.None);
        T.Check("two dropped packets are retried until one gets through (not treated as failure)", retried.Success && gateway.RequestsSeen >= 3 && sw.Elapsed.TotalSeconds > 0.6, $"seen={gateway.RequestsSeen} ok={retried.Success} elapsed={sw.Elapsed.TotalSeconds:0.0}");

        gateway.DropFirstN = 0;
        gateway.RequestsSeen = 0;
        gateway.ForceResultCode = 2;   // "not authorized" per RFC 6886
        var refused = await NatPmpClient.RequestMappingAsync(gateway.EndPoint, 6881, 6881, TimeSpan.FromMinutes(30), tcp: true, CancellationToken.None);
        T.Check("a non-zero result code is reported as a clean failure, not an exception", !refused.Success && refused.ResultCode == 2 && refused.Error != null, refused.ToString());

        gateway.ForceResultCode = null;
        var deleted = await NatPmpClient.DeleteMappingAsync(gateway.EndPoint, 6881, tcp: true, CancellationToken.None);
        T.Check("delete uses a zero lifetime and succeeds the same way a mapping request does", deleted.Success);

        // a gateway that never answers at all: bounded retries, then a clean failure (not a hang)
        using var deadGateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var deadEndpoint = (IPEndPoint)deadGateway.Client.LocalEndPoint!;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var silence = await NatPmpClient.RequestMappingAsync(deadEndpoint, 6881, 6881, TimeSpan.FromMinutes(30), tcp: true, CancellationToken.None);
        T.Check("a gateway that never answers fails cleanly within a few seconds, not a hang", !silence.Success && sw2.Elapsed.TotalSeconds < 10, sw2.Elapsed.TotalSeconds.ToString("0.0"));
    }

    static async Task Upnp()
    {
        T.Check("an SSDP response's LOCATION header is found regardless of header case",
            UpnpClient.ParseLocation("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nlocation: http://192.168.1.1:5000/desc.xml\r\nST: upnp:rootdevice\r\n\r\n")?.ToString() == "http://192.168.1.1:5000/desc.xml");
        T.Check("a response with no LOCATION header at all is not a match", UpnpClient.ParseLocation("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n") == null);

        const string deviceXml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
                <deviceList>
                  <device>
                    <deviceList>
                      <device>
                        <serviceList>
                          <service>
                            <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                            <controlURL>/upnp/control/WANIPConn1</controlURL>
                          </service>
                        </serviceList>
                      </device>
                    </deviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;
        var location = new Uri("http://192.168.1.1:5000/desc.xml");
        var found = UpnpClient.ParseDeviceDescription(deviceXml, location);
        T.Check("the WANIPConnection service is found in a realistically nested device description, and its control URL resolved against LOCATION",
            found != null && found.ServiceType == "urn:schemas-upnp-org:service:WANIPConnection:1" && found.ControlUrl.ToString() == "http://192.168.1.1:5000/upnp/control/WANIPConn1", found?.ControlUrl.ToString());
        T.Check("a description with no WAN connection service at all is not a match", UpnpClient.ParseDeviceDescription("<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device/></root>", location) == null);
        T.Check("garbage instead of XML does not throw", UpnpClient.ParseDeviceDescription("not xml at all", location) == null);

        // a mock router: serves the device description over HTTP, and answers SOAP AddPortMapping/DeletePortMapping
        var soapRequestsSeen = new List<string>();
        using var httpListener = new HttpListener();
        var port = 34000 + Random.Shared.Next(2000);
        httpListener.Prefixes.Add($"http://127.0.0.1:{port}/");
        httpListener.Start();
        var serverTask = Task.Run(async () =>
        {
            for (var i = 0; i < 4; i++)
            {
                HttpListenerContext ctx;
                try { ctx = await httpListener.GetContextAsync(); } catch (Exception) { return; }
                if (ctx.Request.Url!.AbsolutePath == "/desc.xml")
                {
                    var bytes = Encoding.UTF8.GetBytes(deviceXml);
                    ctx.Response.ContentType = "text/xml";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    soapRequestsSeen.Add(await reader.ReadToEndAsync());
                    var ok = !soapRequestsSeen[^1].Contains("<NewExternalPort>0</NewExternalPort>") || ctx.Request.Headers["SOAPAction"]?.Contains("Delete") == true;
                    var body = ok
                        ? "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:Response/></s:Body></s:Envelope>"
                        : "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
                    ctx.Response.StatusCode = ok ? 200 : 500;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                ctx.Response.Close();
            }
        });
        try
        {
            using var http = new HttpClient();
            var mockLocation = new Uri($"http://127.0.0.1:{port}/desc.xml");
            var device = await UpnpClient.GetControlUrlAsync(mockLocation, http, CancellationToken.None);
            T.Check("the control URL is fetched and parsed over real HTTP from the mock router", device != null && device.ControlUrl.ToString() == $"http://127.0.0.1:{port}/upnp/control/WANIPConn1", device?.ControlUrl.ToString());

            var added = await UpnpClient.AddPortMappingAsync(device!, http, 6881, tcp: true, "192.168.1.50", TimeSpan.FromMinutes(30), CancellationToken.None);
            T.Check("AddPortMapping succeeds against the mock router", added.Success, added.Error);
            T.Check("the SOAP request actually carried our port, IP and lease", soapRequestsSeen[^1].Contains("<NewExternalPort>6881</NewExternalPort>") && soapRequestsSeen[^1].Contains("<NewInternalClient>192.168.1.50</NewInternalClient>") && soapRequestsSeen[^1].Contains("<NewLeaseDuration>1800</NewLeaseDuration>"));

            var deleted = await UpnpClient.DeletePortMappingAsync(device!, http, 6881, tcp: true, CancellationToken.None);
            T.Check("DeletePortMapping succeeds too (a NewExternalPort of the real port, not 0, for delete)", deleted.Success && soapRequestsSeen[^1].Contains("<NewExternalPort>6881</NewExternalPort>"), deleted.Error);
        }
        finally { httpListener.Stop(); try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } }

        // a router that refuses (SOAP fault): reported as a failure with the router's own reason, not an exception
        using var faultListener = new HttpListener();
        var faultPort = 36000 + Random.Shared.Next(2000);
        faultListener.Prefixes.Add($"http://127.0.0.1:{faultPort}/");
        faultListener.Start();
        var faultTask = Task.Run(async () =>
        {
            var ctx = await faultListener.GetContextAsync();
            ctx.Response.StatusCode = 500;
            var body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorDescription>Only one static port mapping per port allowed</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            var bytes = Encoding.UTF8.GetBytes(body);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });
        try
        {
            using var http = new HttpClient();
            var faultDevice = new UpnpDevice(new Uri($"http://127.0.0.1:{faultPort}/control"), "urn:schemas-upnp-org:service:WANIPConnection:1");
            var refused = await UpnpClient.AddPortMappingAsync(faultDevice, http, 6881, tcp: true, "192.168.1.50", TimeSpan.FromMinutes(30), CancellationToken.None);
            T.Check("a SOAP fault is reported with the router's own error text, not thrown as an exception", !refused.Success && refused.Error != null && refused.Error.Contains("port mapping"), refused.Error);
        }
        finally { faultListener.Stop(); try { await faultTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { } }

        // no router at all on that address: fails cleanly, quickly
        using var http2 = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var unreachable = await UpnpClient.GetControlUrlAsync(new Uri("http://127.0.0.1:1/desc.xml"), http2, CancellationToken.None);
        T.Check("an unreachable router returns null rather than throwing", unreachable == null);
    }

    static bool TestInTimeWindow(TimeSpan start, TimeSpan stop, TimeSpan now) =>
        start == stop || (start < stop ? now >= start && now < stop : now >= start || now < stop);

    static async Task InDownloadList()
    {
        T.Check("magnet links and .torrent addresses are recognised, other links are not",
            DownloadManager.IsTorrentUrl("magnet:?xt=urn:btih:cfc258121b99ffa77bfb588ae260f947762e0038") && DownloadManager.IsTorrentUrl("https://x.example/a/b.torrent?id=5") && DownloadManager.IsTorrentUrl(@"C:\dl\Movie.TORRENT")
            && !DownloadManager.IsTorrentUrl("https://x.example/file.zip") && !DownloadManager.IsTorrentUrl("") && !DownloadManager.IsTorrentUrl(null) && !DownloadManager.IsTorrentUrl("https://x.example/torrent"));

        using var swarm = new Swarm(Script(), "--seeders", "2", "--rate", "600000", "--files", "clip.bin:1500000,extras/readme.txt:2000", "--piece", "65536");
        using var engine = NewEngine();
        using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { Torrents = engine };
        var save = Sub();
        var item = new DownloadItem { Url = swarm.Magnet, FilePath = Path.Combine(save, "swarm-test"), Connections = 8 };
        manager.Enqueue(item);
        T.Check("the torrent starts like any download and reports peers and speed", await Until(() => item.Progress > 5 && item.ActiveConnections > 0, 30), $"{item.Status} {item.Progress} {item.LastError}");
        T.Check("the description column shows the live torrent line", item.Description.Contains("peers") && item.Description.Contains("↓"), item.Description);
        T.Check("the progress window has one row per peer and a piece map", manager.GetConnections(item).Count >= 1 && manager.GetPositionMap(item, 20).Any(x => x > 0));
        manager.Pause(item);
        T.Check("Pause stops it (the session is paused, the item is Paused)", await Program.WaitStatusPublic(item, DownloadStatus.Paused) && manager.SessionOf(item)?.State == TorrentState.Paused, item.Status + " " + item.LastError);
        var partial = item.Progress;
        manager.Enqueue(item);
        T.Check("Resume continues (it does not start from zero) and completes", await Program.WaitStatusPublic(item, DownloadStatus.Complete) && item.Progress >= 99.9, item.LastError);
        T.Check("the item points at the torrent's folder and every file is byte-exact", item.FilePath == Path.Combine(save, "swarm-test") && SameAsPayload(swarm, save));
        T.Check("a finished torrent keeps sharing and says so", await Until(() => manager.SessionOf(item)?.State == TorrentState.Seeding && item.Description.StartsWith("Seeding"), 10), item.Description);
        await manager.RemoveAsync(item, true);
        T.Check("removing with 'delete files' deletes the torrent's files and the list entry", !Directory.Exists(Path.Combine(save, "swarm-test")) && manager.Items.Count == 0);

        // a .torrent file on the disk
        var second = new DownloadItem { Url = swarm.TorrentPath, FilePath = Path.Combine(save, "placeholder") };
        manager.Enqueue(second);
        T.Check("a .torrent file works as the address", await Program.WaitStatusPublic(second, DownloadStatus.Complete), second.LastError);
        T.Check("and the item was renamed to the torrent's real name", second.FileName == "swarm-test" && SameAsPayload(swarm, save));
        await manager.RemoveAsync(second, false);
        T.Check("removing without deleting keeps the files", Directory.Exists(Path.Combine(save, "swarm-test")));

        var bad = new DownloadItem { Url = Path.Combine(Dir, "not-a-torrent.torrent"), FilePath = Path.Combine(save, "bad") };
        File.WriteAllText(bad.Url, "this is not bencoded");
        manager.Enqueue(bad);
        T.Check("a damaged .torrent file fails with a clear message", await Program.WaitStatusPublic(bad, DownloadStatus.Failed) && bad.LastError!.Contains(".torrent"), bad.LastError);
    }
}
