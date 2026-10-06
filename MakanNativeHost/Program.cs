using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MakanNativeHost;

/// <summary>Native messaging relay: [4-byte length][JSON] on stdin/stdout  &lt;-&gt;  one JSON line over a named pipe.</summary>
public static class Program
{
    // Must match NativeBridge.PipeName in the desktop app.
    public const string PipeName = "com.makan.downloadmanager";
    static string EffectivePipeName => Environment.GetEnvironmentVariable("EPSILON_NATIVE_PIPE") is { Length: > 0 } value ? value : PipeName;
    const int MaxMessageBytes = 8 * 1024 * 1024;
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(25);

    public static int Main(string[] args)
    {
        // The browser passes its own arguments (extension origin / manifest path); we never need them.
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        try
        {
            while (true)
            {
                var payload = ReadMessage(input);
                if (payload is null) return 0; // browser closed the pipe
                WriteMessage(output, Relay(payload));
            }
        }
        catch (IOException) { return 0; }
    }

    public static string Relay(byte[] payload)
    {
        string line, kind;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Error("Invalid request.");
            kind = doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            // Re-emit compactly so the request is always exactly one line.
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms)) doc.RootElement.WriteTo(writer);
            line = Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException) { return Error("Invalid request."); }

        var reply = TryPipe(line, connectMs: 1500);
        if (reply is not null) return reply;

        // "ping" is a status question: never launch the app just to answer it.
        if (kind.Equals("ping", StringComparison.OrdinalIgnoreCase))
            return "{\"ok\":false,\"running\":false,\"hostInstalled\":true,\"error\":\"Epsilon Download Manager is not running.\"}";

        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "MakanDownloadManager.exe" : "MakanDownloadManager");
        if (!File.Exists(exe)) return Error("MakanDownloadManager was not found next to the native host: " + exe);
        try { Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception ex) { return Error("Could not start Epsilon Download Manager: " + ex.Message); }

        return TryPipe(line, connectMs: 12000) ?? Error("Epsilon Download Manager did not respond.");
    }

    static string? TryPipe(string line, int connectMs)
    {
        try
        {
            var task = Task.Run(() =>
            {
                using var pipe = new NamedPipeClientStream(".", EffectivePipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(connectMs);
                var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                return reader.ReadLine();
            });
            return task.Wait(ReplyTimeout + TimeSpan.FromMilliseconds(connectMs)) && !string.IsNullOrWhiteSpace(task.Result) ? task.Result : null;
        }
        catch { return null; }
    }

    static string Error(string message) => "{\"ok\":false,\"error\":\"" + JsonEncodedText.Encode(message) + "\"}"; // no reflection-based serializer: keeps the exe trim-friendly

    static byte[]? ReadMessage(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = input.Read(header[read..]);
            if (n == 0) return null;
            read += n;
        }
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) return null;
        var buffer = new byte[length];
        input.ReadExactly(buffer);
        return buffer;
    }

    static void WriteMessage(Stream output, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        output.Write(BitConverter.GetBytes(bytes.Length));
        output.Write(bytes);
        output.Flush();
    }
}
