using System.Net;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>V2: the per-download connection maximum is 48 end to end, and adaptive mode still lowers it for throttling servers.
/// Needs no web server. Quick run: set MAKAN_TESTS=connections.</summary>
static class ConnectionLimitTests
{
    sealed class Store : ISettingsStore
    {
        public readonly Dictionary<string, string> Data = new();
        public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Data[key] = value;
    }

    static ProbeResult Probe(long length, bool ranges = true, string url = "https://files.example.com/big.bin") =>
        new(ranges, length, ranges ? HttpStatusCode.PartialContent : HttpStatusCode.OK, null, null, null, null, new Uri(url));

    static DownloadItem Item(int connections, string url = "https://files.example.com/big.bin") =>
        new() { Url = url, FilePath = "big.bin", Connections = connections };

    public static void Run()
    {
        Maximum();
        SizeTiers();
        ChoosingConnections();
        AdaptiveFallback();
        SmartController();
    }

    static void Maximum()
    {
        T.Check("the per-download maximum is 48", DownloadManager.MaxConnectionsPerDownload == 48);
        T.Check("a download item accepts 1, 8, 32 and 48", new[] { 1, 8, 32, 48 }.All(n => Item(n).Connections == n));
        T.Check("a download item never goes above 48 or below 1", Item(99).Connections == 48 && Item(1000).Connections == 48 && Item(0).Connections == 1 && Item(-5).Connections == 1);

        using var manager = new DownloadManager(new MemoryStore());
        manager.DefaultConnections = 99;
        T.Check("the default connection count is capped at 48, not 16", manager.DefaultConnections == 48);
        manager.DefaultConnections = 32;
        T.Check("32 (high-performance) is kept as chosen", manager.DefaultConnections == 32);

        var settings = new AppSettings(new Store());
        T.Check("settings default stays 8", settings.Connections == 8);
        settings.Connections = 48;
        T.Check("settings keep 48", settings.Connections == 48);
        settings.Connections = 500;
        T.Check("settings cap a larger value at 48", settings.Connections == 48);
    }

    static void SizeTiers()
    {
        T.Check("a file of 256 MB or more may use the full maximum", DownloadManager.SizeConnectionCap(256L << 20) == 48 && DownloadManager.SizeConnectionCap(1L << 30) == 48 && DownloadManager.SizeConnectionCap(40L << 30) == 48);
        T.Check("files from 16 MB up use the default 8; small files use fewer (4 / 2)",
            DownloadManager.SizeConnectionCap(200L << 20) == 8 && DownloadManager.SizeConnectionCap(100L << 20) == 8 && DownloadManager.SizeConnectionCap(16L << 20) == 8 && DownloadManager.SizeConnectionCap(10L << 20) == 4 && DownloadManager.SizeConnectionCap(3L << 20) == 2);
    }

    static void ChoosingConnections()
    {
        using var m = new DownloadManager(new MemoryStore());
        m.AdaptiveConnectionsEnabled = false;
        T.Check("48 requested on a 2 GB file with ranges = 48 connections", m.ChooseConnections(Item(48), Probe(2L << 30)) == 48);
        T.Check("32 requested on a 2 GB file = 32 connections", m.ChooseConnections(Item(32), Probe(2L << 30)) == 32);
        T.Check("8 requested on a 2 GB file = 8 connections (default is unchanged)", m.ChooseConnections(Item(8), Probe(2L << 30)) == 8);
        T.Check("a 100 MB file is held to 8 by the size tier", m.ChooseConnections(Item(48), Probe(100L << 20)) == 8);
        T.Check("a 500 MB file may use everything asked for (the size tier now opens at 256 MB)", m.ChooseConnections(Item(48), Probe(500L << 20)) == 48);
        T.Check("a server without Range support uses one connection", m.ChooseConnections(Item(48), Probe(2L << 30, ranges: false)) == 1);
        T.Check("a tiny file uses one connection", m.ChooseConnections(Item(48), Probe(1L << 20)) == 1);
        T.Check("an unknown size uses one connection", m.ChooseConnections(Item(48), new ProbeResult(true, null, HttpStatusCode.OK, null, null, null, null, new Uri("https://files.example.com/x"))) == 1);
    }

    static void AdaptiveFallback()
    {
        using var m = new DownloadManager(new MemoryStore());
        var url = "https://throttle.example.com/big.bin";
        var host = new Uri(url);
        var big = Probe(2L << 30, url: url);
        m.AdaptiveConnectionsEnabled = true;
        T.Check("adaptive mode: a new server gets all 48 the user asked for", m.ChooseConnections(Item(48, url), big) == 48);
        m.AdaptiveConnections.ReportThrottle(host);
        T.Check("after one throttle (429/503) the next start halves to 24", m.ChooseConnections(Item(48, url), big) == 24);
        m.AdaptiveConnections.ReportThrottle(host);
        T.Check("after a second throttle it halves again to 12", m.ChooseConnections(Item(48, url), big) == 12);
        for (var i = 0; i < 10; i++) m.AdaptiveConnections.ReportThrottle(host);
        T.Check("it never goes below one connection", m.ChooseConnections(Item(48, url), big) == 1);
        T.Check("another server is not affected", m.ChooseConnections(Item(48, "https://fine.example.com/b"), Probe(2L << 30, url: "https://fine.example.com/b")) == 48);
        m.AdaptiveConnectionsEnabled = false;
        T.Check("with adaptive mode switched off the chosen 48 is used", m.ChooseConnections(Item(48, url), big) == 48);
    }

    static void SmartController()
    {
        T.Check("the smart controller's 'no limit learned' value is the 48 maximum, not 16", SmartDownloadController.NoLimit == 48);
        var store = new Store();
        var c = new SmartDownloadController(store);
        var host = new Uri("https://cdn.example.com/f.bin");
        var done = new DownloadItem { Url = host.ToString(), DoneBytes = 10_000_000 };
        c.ReportSuccess(done, TimeSpan.FromSeconds(2));
        T.Check("a known, healthy server still allows 48 (it used to be held at 16)", c.Suggest(host, 48) == 48);
        T.Check("and 32", c.Suggest(host, 32) == 32);
        c.ReportThrottle(host, 48);
        T.Check("a throttle with 48 connections lowers the next start to 24", c.Suggest(host, 48) == 24);
        T.Check("the smart controller never raises above what the user asked", c.Suggest(host, 8) <= 8);
    }
}
