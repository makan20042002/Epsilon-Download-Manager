using System.Diagnostics;
using MakanDownloadManager;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;

/// <summary>
/// V2 logic that needs no web server: saved column widths, the verified-prefix / piece-bar arithmetic of torrents,
/// the remote-control address and QR code, and the separation of V2's identity from V1's. Plus one live test of the
/// configurable automatic retries (needs the test server).
/// </summary>
static class V2LogicTests
{
    sealed class Store : ISettingsStore
    {
        public readonly Dictionary<string, string> Values = new();
        public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Values[key] = value;
    }

    public static void Run()
    {
        Console.WriteLine("\n== Column widths: defaults, saving, restart, damaged or old text");
        ColumnWidthChecks();
        Console.WriteLine("\n== Torrent pieces: verified bytes, verified prefix for playback, the pieces bar");
        PieceChecks();
        Console.WriteLine("\n== Remote control: address, port, access key, QR code");
        RemoteChecks();
        Console.WriteLine("\n== V2 identity: installs over the public release, keeps its own data folder");
        IdentityChecks();
        ColumnLimitAndRetryMinutes();
    }

    // ---------------------------------------------------------------- column widths
    static void ColumnWidthChecks()
    {
        T.Check("there are 15 default widths (one per table column)", ColumnWidths.Defaults.Length == 15);
        var store = new Store(); var settings = new AppSettings(store);
        T.Check("a fresh install has the 15 default widths (the old 12-value default is gone)", ColumnWidths.Parse(settings.SortTabWidths).SequenceEqual(ColumnWidths.Defaults), settings.SortTabWidths);

        var changed = ColumnWidths.Defaults.Select((w, i) => w + i * 3).ToArray();
        settings.SortTabWidths = ColumnWidths.Serialize(changed);
        var restarted = new AppSettings(store);   // "restart": a new settings object over the same stored values
        T.Check("widths survive a restart exactly", ColumnWidths.Parse(restarted.SortTabWidths).SequenceEqual(changed), restarted.SortTabWidths);

        T.Check("text from an older version (12 values) gives the defaults", ColumnWidths.Parse("270,92,82,96,112,116,110,106,96,126,126,112").SequenceEqual(ColumnWidths.Defaults));
        T.Check("empty or missing text gives the defaults", ColumnWidths.Parse("").SequenceEqual(ColumnWidths.Defaults) && ColumnWidths.Parse(null).SequenceEqual(ColumnWidths.Defaults));
        var junk = ColumnWidths.Parse("abc," + string.Join(",", ColumnWidths.Defaults.Skip(1).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        T.Check("one unreadable value falls back to that column's default and keeps the rest", junk[0] == ColumnWidths.Defaults[0] && junk.Skip(1).SequenceEqual(ColumnWidths.Defaults.Skip(1)));
        var extreme = ColumnWidths.Parse(string.Join(",", Enumerable.Range(0, 15).Select(i => i % 2 == 0 ? "5" : "99999")));
        T.Check("too small / too large widths are kept between 56 and 520", extreme.All(w => w >= ColumnWidths.Min && w <= ColumnWidths.Max) && extreme[0] == 56 && extreme[1] == 520, string.Join(",", extreme));
        T.Check("NaN / infinity are not accepted", ColumnWidths.Parse(string.Join(",", Enumerable.Repeat("NaN", 15))).SequenceEqual(ColumnWidths.Defaults) && ColumnWidths.Parse(string.Join(",", Enumerable.Repeat("Infinity", 15))).SequenceEqual(ColumnWidths.Defaults));
        T.Check("saving a zero-width (not yet laid out) column stores the minimum, not 0", ColumnWidths.Serialize(new[] { 0d, double.NaN, 300.4 }) == "56,56,300");
        var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");
            T.Check("saved text does not depend on the Windows language (always '.' and ',')", ColumnWidths.Serialize(new[] { 250.5, 72d }) == "250,72" && ColumnWidths.Parse(ColumnWidths.DefaultText).SequenceEqual(ColumnWidths.Defaults));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
    }

    // ---------------------------------------------------------------- pieces
    static void PieceChecks()
    {
        // 10 pieces of 100 bytes, last piece only 50 bytes: total 950.
        const int pl = 100; const long total = 950;
        static Func<int, bool> Has(params int[] pieces) { var set = pieces.ToHashSet(); return p => set.Contains(p); }

        T.Check("a file with nothing verified has 0 verified bytes and 0 safe prefix", PieceMath.VerifiedBytes(0, 950, pl, total, Has()) == 0 && PieceMath.VerifiedPrefix(0, 950, pl, total, Has()) == 0);
        T.Check("everything verified: the whole file, including the short last piece", PieceMath.VerifiedBytes(0, 950, pl, total, Has(0, 1, 2, 3, 4, 5, 6, 7, 8, 9)) == 950 && PieceMath.VerifiedPrefix(0, 950, pl, total, Has(0, 1, 2, 3, 4, 5, 6, 7, 8, 9)) == 950);
        T.Check("verified start: the prefix is exactly the verified pieces (pieces 0-2 = 300 bytes)", PieceMath.VerifiedPrefix(0, 950, pl, total, Has(0, 1, 2)) == 300);
        T.Check("a gap stops the prefix but not the verified total (0,1,3 -> prefix 200, verified 300)", PieceMath.VerifiedPrefix(0, 950, pl, total, Has(0, 1, 3)) == 200 && PieceMath.VerifiedBytes(0, 950, pl, total, Has(0, 1, 3)) == 300);
        T.Check("pieces only at the end give no safe prefix (a player would read zeros)", PieceMath.VerifiedPrefix(0, 950, pl, total, Has(7, 8, 9)) == 0 && PieceMath.VerifiedBytes(0, 950, pl, total, Has(7, 8, 9)) == 250);

        // second file of a multi-file torrent: starts in the middle of piece 2, 420 bytes long (offset 250..670)
        T.Check("a file that starts inside a piece counts only its own bytes (pieces 2-3 -> 50 + 100)", PieceMath.VerifiedPrefix(250, 420, pl, total, Has(2, 3)) == 150);
        T.Check("the file's last, partly used piece counts only the part inside the file", PieceMath.VerifiedPrefix(250, 420, pl, total, Has(2, 3, 4, 5, 6)) == 420 && PieceMath.VerifiedBytes(250, 420, pl, total, Has(2, 3, 4, 5, 6)) == 420);
        T.Check("another file's verified pieces do not count (file at 250..670, only piece 0 and 9 verified)", PieceMath.VerifiedBytes(250, 420, pl, total, Has(0, 9)) == 0);
        T.Check("a zero-length file has 0", PieceMath.VerifiedBytes(300, 0, pl, total, Has(3)) == 0 && PieceMath.VerifiedPrefix(300, 0, pl, total, Has(3)) == 0);
        T.Check("the last, short piece is measured by its real size", PieceMath.VerifiedPrefix(900, 50, pl, total, Has(9)) == 50);
        T.Check("the result never exceeds the file length", PieceMath.VerifiedBytes(0, 120, pl, total, Has(0, 1, 2)) == 120);
        T.Check("bad layout numbers give 0 instead of crashing", PieceMath.VerifiedBytes(0, 10, 0, 0, Has(0)) == 0);

        var map = PieceMath.BucketMap(10, 5, Has(0, 1, 2, 3));
        T.Check("pieces bar: 10 pieces in 5 buckets (0-3 verified) = 1, 1, 0, 0, 0", map.SequenceEqual(new[] { 1d, 1d, 0d, 0d, 0d }), string.Join(",", map));
        T.Check("pieces bar: a half-verified bucket shows 0.5", PieceMath.BucketMap(10, 5, Has(0, 3))[0] == 0.5 && PieceMath.BucketMap(10, 5, Has(0, 3))[1] == 0.5);
        T.Check("pieces bar: more buckets than pieces still gives one value per bucket, each 0..1", PieceMath.BucketMap(3, 12, Has(1)).Length == 12 && PieceMath.BucketMap(3, 12, Has(1)).All(v => v is >= 0 and <= 1));
        T.Check("pieces bar: no pieces or no buckets does not crash", PieceMath.BucketMap(0, 5, Has()).Length == 5 && PieceMath.BucketMap(5, 0, Has()).Length == 1);
        T.Check("pieces bar: all verified is all 1", PieceMath.BucketMap(1000, 100, _ => true).All(v => v == 1));
    }

    // ---------------------------------------------------------------- remote
    static void RemoteChecks()
    {
        T.Check("URL: address, port and key", RemoteAccess.BuildUrl("192.168.1.20", 8990, "abc123") == "http://192.168.1.20:8990/?token=abc123");
        T.Check("URL: the key is escaped", RemoteAccess.BuildUrl("10.0.0.5", 9000, "a b&c") == "http://10.0.0.5:9000/?token=a%20b%26c");
        T.Check("URL: an IPv6 address gets brackets", RemoteAccess.BuildUrl("fe80::1", 8990, "k") == "http://[fe80::1]:8990/?token=k");
        T.Check("URL: an impossible port falls back to the default", RemoteAccess.BuildUrl("1.2.3.4", 0, "k") == "http://1.2.3.4:8990/?token=k" && RemoteAccess.BuildUrl("1.2.3.4", 70000, "k").Contains(":8990/"));
        T.Check("URL: no network gives the placeholder text", RemoteAccess.BuildUrl(null, 8990, "k").StartsWith("http://<this computer's IP>:8990/"));
        T.Check("a real address counts as an address, the placeholder and empty text do not", RemoteAccess.HasAddress("192.168.0.4") && !RemoteAccess.HasAddress(RemoteAccess.NoAddressPlaceholder) && !RemoteAccess.HasAddress("") && !RemoteAccess.HasAddress(null));
        T.Check("port text: valid, padded, wrong, out of range, empty", RemoteAccess.ParsePort("8990", 1) == 8990 && RemoteAccess.ParsePort(" 8080 ", 1) == 8080 && RemoteAccess.ParsePort("abc", 7) == 7 && RemoteAccess.ParsePort("0", 7) == 7 && RemoteAccess.ParsePort("65536", 7) == 7 && RemoteAccess.ParsePort("65535", 7) == 65535 && RemoteAccess.ParsePort("", 7) == 7);
        var a = RemoteAccess.NewToken(); var b = RemoteAccess.NewToken();
        T.Check("access keys are 18 hex characters and different each time", a.Length == 18 && a.All(Uri.IsHexDigit) && a == a.ToLowerInvariant() && a != b, a + " / " + b);
        var store = new Store(); var settings = new AppSettings(store);
        var first = settings.RemoteToken;
        T.Check("the key is made once and kept", first.Length == 18 && new AppSettings(store).RemoteToken == first);
        T.Check("the port is kept and a bad stored port gives 8990", (settings.RemotePort = 9123) == 9123 && new AppSettings(store).RemotePort == 9123 && Fresh("remote_port", "99999").RemotePort == 8990);

        var url = RemoteAccess.BuildUrl("192.168.1.20", 8990, first);
        var png = RemoteQr.Png(url);
        T.Check("the QR code is a real PNG picture", png.Length > 200 && png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', png.Length + " bytes");
        T.Check("the same address always gives the same picture, a different address a different one", png.SequenceEqual(RemoteQr.Png(url)) && !png.SequenceEqual(RemoteQr.Png(url + "x")));
        T.Check("a long address still fits in a QR code", RemoteQr.Png("http://192.168.100.200:65535/?token=" + new string('a', 120)).Length > 500);
    }

    static AppSettings Fresh(string key, string value) { var s = new Store(); s.Set(key, value); return new AppSettings(s); }

    // ---------------------------------------------------------------- identity
    sealed class MemSettings : ISettingsStore { readonly Dictionary<string, string> _d = new(); public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null; public void Set(string key, string value) => _d[key] = value; }

    static void ColumnLimitAndRetryMinutes()
    {
        // a column cannot be dragged wider than its longest text plus a little room
        T.Check("a column dragged far too wide stops at its longest text + room", ColumnLimit.Clamp(5000, 200) == 200 + ColumnLimit.Spare);
        T.Check("a width inside the limit is kept as it is", ColumnLimit.Clamp(150, 200) == 150);
        T.Check("a column cannot be dragged away to nothing", ColumnLimit.Clamp(2, 200) == ColumnLimit.MinColumnWidth);
        T.Check("a column with almost no text still keeps the minimum width", ColumnLimit.Clamp(900, 0) == ColumnLimit.MinColumnWidth && ColumnLimit.MaxWidth(0) == ColumnLimit.MinColumnWidth);
        T.Check("bad numbers do not break it", ColumnLimit.Clamp(double.NaN, 100) == ColumnLimit.MinColumnWidth && ColumnLimit.Clamp(double.PositiveInfinity, 100) == ColumnLimit.MinColumnWidth);

        // "retry a dropped network after" takes seconds too (0.5 minutes = 30 seconds)
        T.Check("typed minutes: 5, 0.5, 0,5 and ' 2 ' are understood", AppSettings.TryParseMinutes("5", out var a) && a == 5 && AppSettings.TryParseMinutes("0.5", out var b) && b == 0.5 && AppSettings.TryParseMinutes("0,5", out var c) && c == 0.5 && AppSettings.TryParseMinutes(" 2 ", out var d) && d == 2);
        T.Check("typed minutes: words, empty, zero and negative are refused", !AppSettings.TryParseMinutes("abc", out _) && !AppSettings.TryParseMinutes("", out _) && !AppSettings.TryParseMinutes(null, out _) && !AppSettings.TryParseMinutes("0", out _) && !AppSettings.TryParseMinutes("-3", out _));
        var settings = new AppSettings(new MemSettings());
        T.Check("the default is still 5 minutes", settings.MultiNetRetryMinutes == 5);
        settings.MultiNetRetryMinutes = 0.5;
        T.Check("0.5 minutes (30 seconds) is saved and read back as 0.5", settings.MultiNetRetryMinutes == 0.5 && AppSettings.FormatMinutes(settings.MultiNetRetryMinutes) == "0.5");
        settings.MultiNetRetryMinutes = 0.01;
        T.Check("the smallest is 15 seconds (0.25)", settings.MultiNetRetryMinutes == 0.25);
        settings.MultiNetRetryMinutes = 99999;
        T.Check("the largest is a day", settings.MultiNetRetryMinutes == 1440);
        var older = new MemSettings(); older.Set("multi_network_retry_minutes", "7");
        T.Check("a whole number saved by the old version still reads as before", new AppSettings(older).MultiNetRetryMinutes == 7);
    }

    static void IdentityChecks()
    {
        T.Check("V2 data folder is not V1's folder", ProductIdentity.DataDirectoryName != ProductIdentity.LegacyDataDirectoryName && ProductIdentity.LegacyDataDirectoryName == "MakanDownloadManager");
        T.Check("V2 keeps the program names of the public release, so it installs over it (exe, native host, updater, process)", ProductIdentity.ExecutableName == ProductIdentity.ProcessName + ".exe" && ProductIdentity.ProcessName == "EpsilonDownloadManager" && ProductIdentity.NativeHostExecutableName == "EpsilonNativeHost.exe" && ProductIdentity.UpdaterExecutableName == "EpsilonUpdater.exe");
        T.Check("V2 has its own single-instance name", ProductIdentity.SingleInstanceName.StartsWith(ProductIdentity.ProcessName) && ProductIdentity.SingleInstanceName != "EpsilonDownloadManager.SingleInstance" && ProductIdentity.SingleInstanceName != "MakanDownloadManager.SingleInstance");
        T.Check("V2 has its own private pipe, different from V1's 'com.makan.downloadmanager'", ProductIdentity.NativePipeName == "com.makan.downloadmanager.v2" && ProductIdentity.NativePipeName != "com.makan.downloadmanager");
        T.Check("the public native-host name stays the one the published browser extensions know", ProductIdentity.NativeHostName == "com.makan.downloadmanager");
        T.Check("V2's version is 2.x and cannot be mistaken for 1.7.1", ProductIdentity.Version.StartsWith("2.") && !ProductIdentity.ProductName.Any(char.IsDigit));
    }

    // ---------------------------------------------------------------- retries (live)
    public static async Task RunLive(string baseUrl, string dir, HttpClient http)
    {
        Console.WriteLine("\n== Automatic retries: the attempts and the time window from Settings are obeyed");
        await http.GetAsync(baseUrl + "/__reset");
        try
        {
            var settings = new AppSettings(new Store());
            settings.RetryAttempts = 2; settings.RetryWindowSeconds = 60;
            T.Check("the settings reach the engine as typed", settings.RetryAttempts == 2 && settings.RetryWindowSeconds == 60);

            async Task<(DownloadItem item, double seconds)> Fail(int attempts, int windowSeconds, int waitSeconds)
            {
                var sub = Path.Combine(dir, "rt" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(sub);
                using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { RetryAttempts = attempts, RetryWindow = TimeSpan.FromSeconds(windowSeconds) };
                var item = new DownloadItem { Url = baseUrl + "/down503.bin", FilePath = Path.Combine(sub, "down503.bin"), Connections = 1 };
                var sw = Stopwatch.StartNew();
                m.Enqueue(item);
                await Program.WaitStatusPublic(item, DownloadStatus.Failed, waitSeconds);
                return (item, sw.Elapsed.TotalSeconds);
            }

            var none = await Fail(0, 60, 20);
            T.Check("0 retries: a temporary error fails at once (no waiting)", none.item.Status == nameof(DownloadStatus.Failed) && none.seconds < 3 && none.item.RetryCount == 0, $"{none.item.Status} after {none.seconds:0.0}s, retries {none.item.RetryCount}");

            var two = await Fail(2, 60, 40);
            T.Check("2 retries: tries again twice (waits about 1 s + 2 s), then fails", two.item.Status == nameof(DownloadStatus.Failed) && two.item.RetryCount == 2 && two.seconds is > 2.5 and < 15, $"{two.item.Status} after {two.seconds:0.0}s, retries {two.item.RetryCount}");
            T.Check("the final message names the real error", (two.item.LastError ?? "").Contains("503"), two.item.LastError);

            var shortWindow = await Fail(50, 3, 40);
            T.Check("a short time window ends the retrying even when many attempts are allowed", shortWindow.item.Status == nameof(DownloadStatus.Failed) && shortWindow.item.RetryCount < 10 && shortWindow.seconds < 20, $"{shortWindow.item.Status} after {shortWindow.seconds:0.0}s, retries {shortWindow.item.RetryCount}");

            var hits = await Hits(http, baseUrl, "/down503.bin");
            T.Check("the server was really asked again each time (more requests than downloads)", hits > 6, hits.ToString());

            // a permanent error (404) is not retried at all, whatever the setting
            var sub404 = Path.Combine(dir, "rt" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(sub404);
            using var m404 = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { RetryAttempts = 10, RetryWindow = TimeSpan.FromSeconds(60) };
            var gone = new DownloadItem { Url = baseUrl + "/notfound", FilePath = Path.Combine(sub404, "notfound"), Connections = 1 };
            var sw404 = Stopwatch.StartNew(); m404.Enqueue(gone);
            await Program.WaitStatusPublic(gone, DownloadStatus.Failed, 20);
            T.Check("a 404 is never retried, even with 10 retries allowed", gone.Status == nameof(DownloadStatus.Failed) && sw404.Elapsed.TotalSeconds < 4 && gone.RetryCount == 0, $"{sw404.Elapsed.TotalSeconds:0.0}s, retries {gone.RetryCount}");
        }
        catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static async Task<int> Hits(HttpClient http, string baseUrl, string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(baseUrl + "/__stats"));
        return doc.RootElement.GetProperty("hits").TryGetProperty(path, out var v) ? v.GetInt32() : 0;
    }
}
