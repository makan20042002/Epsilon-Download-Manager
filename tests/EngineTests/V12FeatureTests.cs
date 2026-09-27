using MakanDownloadManager.Models;
using MakanDownloadManager.Services;



public static class V12FeatureTests
{
    public static void Run()
    {
        SmartPlanner();
        RuleEngine();
        Basket();
        SecurityHelpers();
        Statistics();
    }

    static void SmartPlanner()
    {
        var p = new SmartDownloadAnalyzer().Plan("https://example.com/video.mp4", null, Path.GetTempPath(), 8, DownloadPerformanceMode.ServerFriendly);
        if (p.Category != "Video" || p.Connections != 2 || !p.Resumable) throw new Exception("V12 smart planner failed.");
    }

    static void RuleEngine()
    {
        var e = new DownloadRuleEngine().Add(new DownloadRule { Pattern = "*.iso", Priority = 9, Connections = 12 });
        var x = new DownloadItem { FilePath = "C:\\Downloads\\Ubuntu.iso", Url = "https://example.com/Ubuntu.iso" };
        e.Apply(x);
        if (x.Priority != 9 || x.Connections != 12) throw new Exception("V12 rule engine failed.");
    }

    static void Basket()
    {
        var b = new DownloadBasket(); b.Add("https://example.com/a.zip"); b.Add("https://example.com/a.zip");
        if (b.Items.Count != 1) throw new Exception("V12 basket duplicate handling failed.");
        var json = b.ExportJson(); var c = new DownloadBasket(); c.ImportJson(json);
        if (c.Items.Count != 1) throw new Exception("V12 basket persistence failed.");
        var threw = false;
        try { new DownloadBasket().ImportJson("not json at all"); } catch (System.Text.Json.JsonException) { threw = true; }
        if (!threw) throw new Exception("V12 basket: corrupted saved data should be a visible failure, not silently ignored.");
    }

    static void SecurityHelpers()
    {
        var p = Path.Combine(Path.GetTempPath(), "makan-v12-test.txt"); File.WriteAllText(p, "makan-v12");
        var hash = DownloadSecurityService.Sha256Async(p).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(hash) || !DownloadSecurityService.VerifySha256(p, hash)) throw new Exception("V12 SHA-256 helper failed.");
        File.Delete(p);
    }

    static void Statistics()
    {
        var items = new[] { new DownloadItem { Status = nameof(DownloadStatus.Complete), DoneBytes = 100, Category = "Documents" }, new DownloadItem { Status = nameof(DownloadStatus.Downloading), DoneBytes = 50, Category = "Video", SpeedBytesPerSec = 20 } };
        var s = StatisticsSnapshotBuilder.Build(items);
        if (s.CompletedCount != 1 || s.ActiveCount != 1 || s.TotalBytes != 150) throw new Exception("V12 statistics failed.");
    }
}
