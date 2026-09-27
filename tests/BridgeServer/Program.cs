using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

// usage: BridgeServer <downloadDir> [direct|ask]
//   direct: nothing hooked up, downloads start immediately (what happens with "always ask" switched off)
//   ask:    behaves like the desktop app with "always ask" on: prompts are recorded to prompts.jsonl instead of starting anything
var dir = args[0];
var ask = args.Length > 1 && args[1] == "ask";
Directory.CreateDirectory(dir);
var promptLog = Path.Combine(dir, "prompts.jsonl");
var stateFile = Path.Combine(dir, "state.json");
var json = new JsonSerializerOptions { WriteIndented = false };
var gate = new object();

using var manager = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false);
using var bridge = new NativeBridge(manager, () => dir);
if (ask)
{
    void Log(string kind, object payload) { lock (gate) File.AppendAllText(promptLog, JsonSerializer.Serialize(new { kind, payload }, json) + "\n"); }
    bridge.AskBeforeStart = () => true;
    bridge.DownloadPrompt = p => { Log("download", p); return true; };
    bridge.BatchPrompt = b => { Log("batch", b); return true; };
    bridge.StreamPrompt = s => { Log("stream", s); return true; };
    bridge.LinksPrompt = l => { Log("links", l); return true; };
}

using var stop = new CancellationTokenSource();
var writer = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            var snapshot = manager.Items.Select(i => new { i.Id, Url = i.Url.Split('#')[0], i.FilePath, i.Status, Error = i.LastError, i.Progress, i.TotalBytes }).ToList();
            var tmp = stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, json));
            File.Move(tmp, stateFile, true);
        }
        catch (IOException) { }
        await Task.Delay(200);
    }
});

Console.WriteLine("ready");
Console.Out.Flush();
await Task.Run(() => Console.In.ReadToEnd());   // the test closes stdin to stop us
stop.Cancel();
await writer;
