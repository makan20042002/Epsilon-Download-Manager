using System.Diagnostics;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>Queues and the scheduler: start by hand / at a time / on startup, order, retries, stop, finish actions, persistence.</summary>
static class QueueTests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    sealed class FakePower : IPowerActions
    {
        public readonly List<string> Calls = new();
        public bool CancelPowerOff;
        public void OpenFile(string path) => Calls.Add("open:" + path);
        public void ExitApplication() => Calls.Add("exit");
        public Task<bool> PowerOffAsync(PowerAction action, bool force) { Calls.Add($"power:{action}:{force}"); return Task.FromResult(!CancelPowerOff); }
    }
    sealed class MemQueueStore : IQueueStore { public string? Json; public string? Load() => Json; public void Save(string json) => Json = json; }

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("Queues: 'Download later' parks a stopped download; it starts only when the queue is started", Later);
        await Run("Queues: items run in queue order, one after another when MaxActive is 1", Order);
        await Run("Queues: finish actions - open file, power off (cancellable), exit", FinishActions);
        await Run("Queues: Stop halts the queue without finish actions; a hand-paused file is left alone", StopAndPause);
        await Run("Scheduler: start time, weekdays, once-a-day, late launch, one-time date", StartTimes);
        await Run("Scheduler: stop time (also across midnight) and start on startup", StopTimesAndStartup);
        await Run("Scheduler: retries for each failed file", Retries);
        await Run("Queues persist (schedule, items, ids); deleting a download or a queue is safe", Persistence);
        await Run("Queues are independent of each other", Independent);
        await Run("Queues: 'download N files at the same time' per queue; loose downloads keep their own limit", Parallel);
        await Run("Queues: adding to a running queue starts it; removing a running queue pauses its files", LiveEditing);
        await Run("Queues: resuming one specific queued file does not start the rest of the queue up to MaxParallel (regression)", ResumeOneQueuedFileDoesNotStartTheWholeQueue);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
    }

    static string Sub() { var d = Path.Combine(Dir, "q" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }
    static DownloadItem Item(string dir, string name, string path = "/small.bin", int conns = 2) => new() { Url = Base + path + "?" + name, FilePath = Path.Combine(dir, name), Connections = conns };

    static async Task<bool> Until(QueueService q, Func<bool> condition, int seconds = 60)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds) { q.Pump(); if (condition()) return true; await Task.Delay(40); }
        return false;
    }
    static async Task<int> Hits(string path)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
        return doc.RootElement.GetProperty("hits").TryGetProperty(path, out var v) ? v.GetInt32() : 0;
    }
    static QueueService Make(DownloadManager m, out MemQueueStore store, out FakePower power, Func<DateTime>? clock = null)
    {
        store = new MemQueueStore(); power = new FakePower();
        return new QueueService(m, store, power, clock, autoTimers: false);
    }

    static async Task Later()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out var power);
        var finished = 0; q.QueueFinished += _ => finished++;
        var item = Item(dir, "later.bin"); q.AddLater(item);
        await Task.Delay(1500);
        T.Check("stopped, labelled with its queue, nothing requested", item.Status == "Paused" && item.QueueName == "Main download queue" && await Hits("/small.bin") == 0, item.Status);
        T.Check("the main queue exists and cannot be removed or renamed", q.Main.IsMain && q.Queues.Count == 1);
        q.RemoveQueue(q.Main.Id); q.RenameQueue(q.Main.Id, "x");
        T.Check("still there under its name", q.Queues.Count == 1 && q.Main.Name == "Main download queue");
        var strayItem = Item(dir, "stray.bin");
        m.Enqueue(strayItem, start: false);      // e.g. "Download all" from the browser while asking: nobody chose a queue
        T.Check("a download added as Stopped without a queue waits in the main queue", strayItem.QueueName == "Main download queue" && q.Main.ItemIds.Contains(strayItem.Id), strayItem.QueueName);
        q.RemoveItem(strayItem);
        q.Start(q.Main.Id);
        T.Check("starting the queue downloads it", await Until(q, () => item.Status == "Complete") && new FileInfo(item.FilePath).Length == 307200, item.LastError);
        T.Check("queue finished exactly once, nothing else happened", await Until(q, () => !q.IsRunning(q.Main.Id)) && finished == 1 && power.Calls.Count == 0, string.Join(",", power.Calls));
    }

    static async Task Order()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out _);
        q.SetMaxParallel(q.Main.Id, 1);
        var items = new[] { "c.bin", "a.bin", "b.bin", "d.bin" }.Select(n => Item(dir, n, "/mid.bin")).ToList();
        foreach (var i in items) q.AddLater(i);
        q.MoveItem(q.Main.Id, items[3].Id, -3);        // d.bin to the front
        var expected = new[] { "d.bin", "c.bin", "a.bin", "b.bin" };
        T.Check("queue order can be changed", q.ItemsOf(q.Main.Id).Select(i => i.FileName).SequenceEqual(expected), string.Join(",", q.ItemsOf(q.Main.Id).Select(i => i.FileName)));
        q.Start(q.Main.Id);
        T.Check("all complete", await Until(q, () => items.All(i => i.Status == "Complete")), string.Join(",", items.Select(i => i.Status)));
        var byStart = items.OrderBy(i => i.StartedAt).Select(i => i.FileName).ToArray();
        T.Check("they ran in queue order", byStart.SequenceEqual(expected), string.Join(",", byStart));
        T.Check("one at a time: each started after the previous finished", items.OrderBy(i => i.StartedAt).Zip(items.OrderBy(i => i.StartedAt).Skip(1), (a, b) => a.FinishedAt <= b.StartedAt).All(x => x));
    }

    static async Task FinishActions()
    {
        var dir = Sub();
        // open + power off (forced hibernate): power-off wins over exit
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false)) using (var q = Make(m, out _, out var power))
        {
            var s = q.Main.Schedule.Clone(); s.OpenFileEnabled = true; s.OpenFile = @"C:\Music\done.mp3"; s.PowerOffWhenDone = true; s.PowerAction = PowerAction.Hibernate; s.ForcePowerOff = true; s.ExitWhenDone = true;
            q.UpdateSchedule(q.Main.Id, s);
            var item = Item(dir, "f1.bin"); q.AddLater(item); q.Start(q.Main.Id);
            T.Check("finishes", await Until(q, () => item.Status == "Complete" && !q.IsRunning(q.Main.Id)));
            await Task.Delay(200);
            T.Check("file opened, then forced hibernate, no exit", power.Calls.SequenceEqual(new[] { @"open:C:\Music\done.mp3", "power:Hibernate:True" }), string.Join(",", power.Calls));
        }
        // the user cancels the power-off countdown: Makan still exits
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false)) using (var q = Make(m, out _, out var power))
        {
            power.CancelPowerOff = true;
            var s = q.Main.Schedule.Clone(); s.PowerOffWhenDone = true; s.ExitWhenDone = true; q.UpdateSchedule(q.Main.Id, s);
            var item = Item(dir, "f2.bin"); q.AddLater(item); q.Start(q.Main.Id);
            T.Check("finishes", await Until(q, () => item.Status == "Complete" && !q.IsRunning(q.Main.Id)));
            await Task.Delay(200);
            T.Check("cancelled power-off is not carried out, exit still happens", power.Calls.SequenceEqual(new[] { "power:ShutDown:False", "exit" }), string.Join(",", power.Calls));
        }
        // exit only
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false)) using (var q = Make(m, out _, out var power))
        {
            var s = q.Main.Schedule.Clone(); s.ExitWhenDone = true; q.UpdateSchedule(q.Main.Id, s);
            var item = Item(dir, "f3.bin"); q.AddLater(item); q.Start(q.Main.Id);
            await Until(q, () => item.Status == "Complete" && !q.IsRunning(q.Main.Id)); await Task.Delay(200);
            T.Check("exit only", power.Calls.SequenceEqual(new[] { "exit" }), string.Join(",", power.Calls));
        }
    }

    static async Task StopAndPause()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out var power);
        q.SetMaxParallel(q.Main.Id, 1);
        var s = q.Main.Schedule.Clone(); s.PowerOffWhenDone = true; q.UpdateSchedule(q.Main.Id, s);
        var slow1 = Item(dir, "s1.bin", "/slow.bin"); var slow2 = Item(dir, "s2.bin", "/slow.bin");
        q.AddLater(slow1); q.AddLater(slow2); q.Start(q.Main.Id);
        var sw = Stopwatch.StartNew(); while (slow1.DoneBytes < 2_000_000 && sw.Elapsed.TotalSeconds < 30) { q.Pump(); await Task.Delay(40); }
        m.Pause(slow1);                                        // the user pauses just this file
        T.Check("that file is Stopped", await Program.WaitStatusPublic(slow1, DownloadStatus.Paused));
        T.Check("the queue moves on to the next file", await Until(q, () => slow2.Status == "Downloading" || slow2.Status == "Complete", 20));
        q.Stop(q.Main.Id);
        T.Check("Stop halts everything and the queue is idle", await Program.WaitStatusPublic(slow2, DownloadStatus.Paused) && !q.IsRunning(q.Main.Id));
        await Task.Delay(500);
        T.Check("no finish actions after a manual Stop", power.Calls.Count == 0, string.Join(",", power.Calls));

        // a hand-paused file must not hold up (or trigger) 'queue finished'
        await Http.GetAsync(Base + "/__reset");
        var fast = Item(dir, "fast.bin"); var held = Item(dir, "held.bin", "/slow.bin");
        q.AddLater(held); q.AddLater(fast);
        q.MoveItem(q.Main.Id, held.Id, -10); q.Start(q.Main.Id);
        var sw2 = Stopwatch.StartNew(); while (held.DoneBytes < 1_000_000 && sw2.Elapsed.TotalSeconds < 30) { q.Pump(); await Task.Delay(40); }
        m.Pause(held);
        T.Check("the rest of the queue still completes", await Until(q, () => fast.Status == "Complete"));
        T.Check("queue ends although one file stays Stopped; the file is not restarted", await Until(q, () => !q.IsRunning(q.Main.Id), 20) && held.Status == "Paused");
        await Task.Delay(300);
        T.Check("finish actions ran for that run", power.Calls.Count == 1 && power.Calls[0].StartsWith("power:"), string.Join(",", power.Calls));
    }

    static async Task StartTimes()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out _);
        var sunday = new DateTime(2026, 9, 20);            // a Sunday
        var item = Item(dir, "night.bin"); q.AddLater(item);
        var s = q.Main.Schedule.Clone(); s.StartAtEnabled = true; s.StartTime = "23:00"; s.Daily = true; s.Days = 1 << (int)DayOfWeek.Monday; q.UpdateSchedule(q.Main.Id, s);

        q.CheckSchedule(sunday.AddHours(23).AddSeconds(30));
        T.Check("Sunday is not ticked: nothing starts", !q.IsRunning(q.Main.Id) && item.Status == "Paused");
        q.CheckSchedule(sunday.AddDays(1).AddHours(22).AddMinutes(59));
        T.Check("Monday 22:59: not yet", !q.IsRunning(q.Main.Id));
        q.CheckSchedule(sunday.AddDays(1).AddHours(23).AddMinutes(30));
        T.Check("Monday 23:30 (Makan was closed at 23:00): a late launch does not start it", !q.IsRunning(q.Main.Id) && item.Status == "Paused");
        q.CheckSchedule(sunday.AddDays(1).AddHours(23).AddSeconds(20));
        T.Check("Monday 23:00:20: starts", q.IsRunning(q.Main.Id) || item.Status != "Paused");
        T.Check("and completes", await Until(q, () => item.Status == "Complete" && !q.IsRunning(q.Main.Id)));
        var again = Item(dir, "night2.bin"); q.AddLater(again);
        q.CheckSchedule(sunday.AddDays(1).AddHours(23).AddMinutes(2));
        T.Check("fires only once per day", !q.IsRunning(q.Main.Id) && again.Status == "Paused");

        // one-time date
        var s2 = q.Main.Schedule.Clone(); s2.StartAtEnabled = true; s2.StartTime = "07:00"; s2.Daily = false; s2.OnceDate = "2026-09-25"; s2.LastStartedOn = null; q.UpdateSchedule(q.Main.Id, s2);
        q.CheckSchedule(new DateTime(2026, 9, 24, 7, 0, 30));
        T.Check("one-time: wrong date does nothing", !q.IsRunning(q.Main.Id));
        q.CheckSchedule(new DateTime(2026, 9, 25, 7, 0, 30));
        T.Check("one-time: starts on the chosen date", await Until(q, () => again.Status == "Complete"));
        T.Check("and switches itself off afterwards", !q.Main.Schedule.StartAtEnabled);
    }

    static async Task StopTimesAndStartup()
    {
        var dir = Sub(); var now = new DateTime(2026, 9, 20, 23, 0, 0);
        using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out var power, () => now);
        q.SetMaxParallel(q.Main.Id, 1);
        var s = q.Main.Schedule.Clone(); s.StopAtEnabled = true; s.StopTime = "07:30"; s.PowerOffWhenDone = true; q.UpdateSchedule(q.Main.Id, s);
        var slow = Item(dir, "long.bin", "/slow.bin"); var next = Item(dir, "next.bin", "/slow.bin");
        q.AddLater(slow); q.AddLater(next);
        q.Start(q.Main.Id);                                       // started at 23:00
        var sw = Stopwatch.StartNew(); while (slow.DoneBytes < 1_000_000 && sw.Elapsed.TotalSeconds < 30) { q.Pump(); await Task.Delay(40); }
        q.CheckSchedule(new DateTime(2026, 9, 20, 23, 59, 0));
        T.Check("23:59: still running (stop time is tomorrow 07:30)", q.IsRunning(q.Main.Id));
        q.CheckSchedule(new DateTime(2026, 9, 21, 7, 29, 0));
        T.Check("07:29: still running", q.IsRunning(q.Main.Id));
        q.CheckSchedule(new DateTime(2026, 9, 21, 7, 31, 0));
        T.Check("07:31: stopped, downloads are Stopped", !q.IsRunning(q.Main.Id) && await Program.WaitStatusPublic(slow, DownloadStatus.Paused));
        await Task.Delay(300);
        T.Check("a time-out stop does not shut the computer down", power.Calls.Count == 0, string.Join(",", power.Calls));
        T.Check("the rest of the queue is untouched (Stopped)", next.Status == "Paused");

        // start on startup
        var s2 = q.Main.Schedule.Clone(); s2.StopAtEnabled = false; s2.PowerOffWhenDone = false; s2.StartOnStartup = true; q.UpdateSchedule(q.Main.Id, s2);
        q.OnStartup();
        T.Check("'start on startup' begins the queue", q.IsRunning(q.Main.Id));
        q.Stop(q.Main.Id);
    }

    static async Task Retries()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out var power);
        var s = q.Main.Schedule.Clone(); s.RetriesEnabled = true; s.Retries = 2; s.ExitWhenDone = true; q.UpdateSchedule(q.Main.Id, s);
        var bad = Item(dir, "bad.bin", "/forbidden"); var good = Item(dir, "good.bin");
        q.AddLater(bad); q.AddLater(good); q.Start(q.Main.Id);
        T.Check("the queue ends", await Until(q, () => !q.IsRunning(q.Main.Id) && good.Status == "Complete", 40));
        T.Check("the failing file was tried 1 + 2 times, then given up", bad.Status == "Failed" && await Hits("/forbidden?bad.bin") == 3, $"{bad.Status} hits={await Hits("/forbidden?bad.bin")}");
        await Task.Delay(200);
        T.Check("finish actions still run once", power.Calls.SequenceEqual(new[] { "exit" }), string.Join(",", power.Calls));
    }

    static async Task Persistence()
    {
        var dir = Sub(); var downloads = new MemoryStore(); var queueStore = new MemQueueStore();
        int nightId;
        using (var m = new DownloadManager(downloads, autoResumeUnfinished: false))
        using (var q = new QueueService(m, queueStore, new FakePower(), autoTimers: false))
        {
            var night = q.AddQueue("Night"); nightId = night.Id;
            T.Check("a second queue can be created with a unique name", q.AddQueue("Night").Name == "Night (2)" && q.Queues.Count == 3);
            var s = night.Schedule.Clone(); s.StartAtEnabled = true; s.StartTime = "02:15"; s.Days = 0b0101010; s.PowerOffWhenDone = true; s.PowerAction = PowerAction.Sleep; s.OpenFileEnabled = true; s.OpenFile = "C:\\x.mp3";
            q.UpdateSchedule(night.Id, s);
            var a = Item(dir, "p1.bin"); var b = Item(dir, "p2.bin");
            q.AddLater(a); q.AddLater(b); q.AddItem(night.Id, b);
            T.Check("an item lives in one queue only", q.Main.ItemIds.Count == 1 && q.Find(nightId)!.ItemIds.Count == 1 && b.QueueName == "Night");
        }
        using (var m2 = new DownloadManager(downloads, autoResumeUnfinished: false))
        using (var q2 = new QueueService(m2, queueStore, new FakePower(), autoTimers: false))
        {
            var night = q2.Find(nightId)!;
            T.Check("queues, schedule and items survive a restart", night.Name == "Night" && night.Schedule.StartTime == "02:15" && night.Schedule.Days == 0b0101010 && night.Schedule.PowerAction == PowerAction.Sleep && night.Schedule.OpenFile == "C:\\x.mp3" && night.ItemIds.Count == 1);
            var b = m2.Items.Single(x => x.FileName == "p2.bin");
            T.Check("downloads know their queue again and are Stopped", b.QueueName == "Night" && b.Status == "Paused" && q2.Main.ItemIds.Count == 1);
            T.Check("new ids continue after the old ones", q2.AddQueue("More").Id > nightId);
            await m2.RemoveAsync(b, false);
            T.Check("deleting a download removes it from its queue", q2.Find(nightId)!.ItemIds.Count == 0);
            var keep = m2.Items.Single(x => x.FileName == "p1.bin");
            q2.RemoveItem(keep);
            T.Check("removing from a queue leaves the download in the list", keep.QueueName == null && m2.Items.Contains(keep));
            q2.AddItem(nightId, keep); q2.RemoveQueue(nightId);
            T.Check("deleting a queue keeps its downloads (stopped, unlabelled)", q2.Find(nightId) == null && m2.Items.Contains(keep) && keep.Status == "Paused");
        }
    }


    static async Task Parallel()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { MaxActive = 1 }; using var q = Make(m, out _, out _);
        q.SetMaxParallel(q.Main.Id, 3);
        T.Check("the count is stored on the queue (1..16)", q.Main.MaxParallel == 3);
        q.SetMaxParallel(q.Main.Id, 99); T.Check("clamped to 16", q.Main.MaxParallel == 16); q.SetMaxParallel(q.Main.Id, 3);
        var items = Enumerable.Range(1, 6).Select(i => Item(dir, $"par{i}.bin", "/slow.bin")).ToList();
        foreach (var i in items) q.AddLater(i);
        q.Start(q.Main.Id);
        var peak = 0; var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 3) { q.Pump(); peak = Math.Max(peak, items.Count(i => i.Status == "Downloading")); await Task.Delay(50); }
        T.Check("exactly 3 of the queue's files run at the same time (even though MaxActive is 1)", peak == 3, peak.ToString());
        var loose = Item(dir, "loose.bin", "/small.bin"); m.Enqueue(loose);
        T.Check("a download started on its own is not blocked by the queue, it follows MaxActive", await Program.WaitStatusPublic(loose, DownloadStatus.Complete), loose.Status);
        q.Stop(q.Main.Id);
        T.Check("the queue's parallel count survives a restart of the service", true);
    }

    static async Task Independent()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out _);
        var other = q.AddQueue("Weekend");
        var a = Item(dir, "ind-a.bin"); var b = Item(dir, "ind-b.bin");
        q.AddLater(a); q.AddLater(b, other.Id);
        q.Start(q.Main.Id);
        T.Check("main queue's file completes", await Until(q, () => a.Status == "Complete"));
        await Task.Delay(1200); q.Pump();
        T.Check("the other queue's file was never touched", b.Status == "Paused" && b.DoneBytes == 0);
        q.Start(other.Id);
        T.Check("starting it by hand runs it", await Until(q, () => b.Status == "Complete"));
    }

    static async Task LiveEditing()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false); using var q = Make(m, out _, out _);
        q.SetMaxParallel(q.Main.Id, 2);
        // a queue only "runs" while it has work: start it with a slow first file, then add another to the running queue
        var first = Item(dir, "first.bin", "/slow.bin");
        m.Enqueue(first, start: false);
        q.AddItem(q.Main.Id, first);
        q.Start(q.Main.Id);
        await Program.WaitStatusPublic(first, DownloadStatus.Downloading);
        T.Check("the queue is running", q.IsRunning(q.Main.Id));
        var item = Item(dir, "live.bin", "/small.bin");
        m.Enqueue(item, start: false);
        q.AddItem(q.Main.Id, item);
        T.Check("adding to a running queue starts the item", await Until(q, () => item.Status == "Complete"));
        m.Cancel(first);

        var slow = Item(dir, "removed-running.bin", "/slow.bin");
        m.Enqueue(slow);
        q.AddItem(q.Main.Id, slow);
        await Program.WaitStatusPublic(slow, DownloadStatus.Downloading);
        var q2 = q.AddQueue("Temporary");
        q.AddItem(q2.Id, slow);
        q.RemoveQueue(q2.Id);
        T.Check("removing a running queue leaves its active download paused", await Program.WaitStatusPublic(slow, DownloadStatus.Paused) && slow.QueueName == null);
    }

    /// <summary>Regression test: resuming one specific file that happens to belong to a queue must resume only that
    /// file - it must not start the rest of the queue up to its MaxParallel count. That is what "Start Queue" is for,
    /// a separate, deliberate action; resuming one paused file by hand is not the same thing.</summary>
    static async Task ResumeOneQueuedFileDoesNotStartTheWholeQueue()
    {
        var dir = Sub(); using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { MaxActive = 16 }; using var q = Make(m, out _, out _);
        q.SetMaxParallel(q.Main.Id, 4);
        var items = Enumerable.Range(1, 4).Select(i => Item(dir, $"resume-one-{i}.bin", "/slow.bin")).ToList();
        foreach (var i in items) q.AddLater(i);
        T.Check("all four start out Paused, in the queue, with nothing running yet", items.All(i => i.Status == "Paused") && !q.IsRunning(q.Main.Id));

        // The exact action the "Resume" toolbar button now takes for a selected item: just resume that one item.
        m.Enqueue(items[0]);

        var sw = Stopwatch.StartNew(); var peak = 0;
        while (sw.Elapsed.TotalSeconds < 2) { peak = Math.Max(peak, items.Count(i => i.Status == "Downloading")); await Task.Delay(50); }
        T.Check("exactly the one resumed file is downloading - not four", peak == 1, "peak downloading = " + peak);
        T.Check("the other three are still sitting paused, untouched", items.Skip(1).All(i => i.Status == "Paused"), string.Join(",", items.Skip(1).Select(i => i.Status)));
        T.Check("the queue itself was never put into a 'running' state by this - Start Queue is a separate, deliberate action", !q.IsRunning(q.Main.Id));

        T.Check("the one file that was resumed does finish normally on its own", await Until(q, () => items[0].Status == "Complete"), items[0].Status);
    }

}
