using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services.Torrent;

namespace MakanDownloadManager.Services;

/// <summary>
/// A small, opt-in HTTP server so a phone on the same Wi-Fi/LAN can see progress and add a download without a native
/// app - the realistic middle ground between "no phone access at all" and building and maintaining a whole separate
/// Android app. Off by default; requires the shared token in every request, since anyone who can reach the port can
/// otherwise see and add downloads. Not encrypted (plain HTTP) - it is meant for a home network, not the open internet;
/// nothing about it should ever be exposed through a router's port forwarding.
/// </summary>
public sealed class RemoteStatusServer : IDisposable
{
    readonly DownloadManager _manager;
    readonly Func<string> _defaultFolder;
    readonly Func<string> _torrentFolder;
    HttpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _loop;

    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public bool IsRunning => _listener != null;

    public RemoteStatusServer(DownloadManager manager, Func<string> defaultFolder, Func<string>? torrentFolder = null)
    {
        _manager = manager;
        _defaultFolder = defaultFolder;
        _torrentFolder = torrentFolder ?? defaultFolder;
    }

    /// <summary>Starts listening on every local address at the given port (so a phone on the same network can reach
    /// it via the computer's LAN IP), guarded by <paramref name="token"/>. Throws if the port is already in use.</summary>
    public void Start(int port, string token)
    {
        if (_listener != null) return;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        try { listener.Start(); }
        catch (HttpListenerException)
        {
            // "+": needs a URL ACL reservation or admin rights on some systems; localhost-only still works without either,
            // and still serves a phone if the phone happens to be the same machine, but a real phone on Wi-Fi needs the
            // broader binding above - if that failed, this fallback at least keeps the feature from refusing to start.
            try { listener.Close(); } catch (Exception) { }
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
        }
        _listener = listener; Port = port; Token = token;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
    }

    async Task RunAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false); }
            catch (Exception) { return; }   // stopped, or the listener was disposed
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            // Never let a browser cache or sniff these responses, and never leak the address (it carries the token) in a Referer header.
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            if (!IsTrustedHost(ctx.Request))
            {
                // DNS-rebinding guard: the page is only ever opened by IP address (or localhost), never by a domain name.
                await WriteJsonAsync(ctx, 403, new { ok = false, error = "Open this page by the computer's IP address." }).ConfigureAwait(false);
                return;
            }
            if (path == "/")
            {
                await WriteAsync(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page)).ConfigureAwait(false);
                return;
            }
            if (!TokenMatches(ctx.Request.QueryString["token"]))
            {
                await WriteJsonAsync(ctx, 401, new { ok = false, error = "Wrong or missing token." }).ConfigureAwait(false);
                return;
            }
            if (!IsSameOrigin(ctx.Request))
            {
                // A different website open in the same browser must not be able to drive this API.
                await WriteJsonAsync(ctx, 403, new { ok = false, error = "Cross-site requests are not allowed." }).ConfigureAwait(false);
                return;
            }
            if (path == "/api/status" && ctx.Request.HttpMethod == "GET") { await Status(ctx).ConfigureAwait(false); return; }
            if (path == "/api/add" && ctx.Request.HttpMethod == "POST") { await Add(ctx).ConfigureAwait(false); return; }
            await WriteJsonAsync(ctx, 404, new { ok = false, error = "Not found." }).ConfigureAwait(false);
        }
        catch (Exception) { try { ctx.Response.Close(); } catch (Exception) { } }
    }

    /// <summary>Compares in constant time, so the token cannot be guessed one character at a time from response timing.</summary>
    bool TokenMatches(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(Token)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(Token));
    }

    /// <summary>True when the request was addressed to an IP address or localhost (what the Options window shows), not a domain name.</summary>
    static bool IsTrustedHost(HttpListenerRequest request)
    {
        var host = request.Url?.Host;
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return request.Url!.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
    }

    /// <summary>Browsers send Origin on cross-site requests (and on same-site POSTs); when present it must be this very page.</summary>
    static bool IsSameOrigin(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;   // not a browser cross-site request (same-origin GET, or a non-browser client)
        var self = request.Url;
        return self != null && Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Host, self.Host, StringComparison.OrdinalIgnoreCase) && o.Port == self.Port;
    }

    async Task Status(HttpListenerContext ctx)
    {
        var items = _manager.Items.OrderByDescending(i => i.Id).Take(200).Select(i => new
        {
            id = i.Id, name = i.FileName, status = i.Status, statusText = i.StatusText,
            progress = Math.Round(i.Progress, 1), speed = i.SpeedText, size = i.SizeDisplay
        });
        await WriteJsonAsync(ctx, 200, new { ok = true, items }).ConfigureAwait(false);
    }

    async Task Add(HttpListenerContext ctx)
    {
        const int MaxBodyChars = 16 * 1024;   // a link or magnet is far smaller; refuse anything trying to fill memory
        if (ctx.Request.ContentLength64 > MaxBodyChars * 4L) { await WriteJsonAsync(ctx, 413, new { ok = false, error = "Request too large." }).ConfigureAwait(false); return; }
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            var buffer = new char[MaxBodyChars + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await reader.ReadAsync(buffer.AsMemory(read, buffer.Length - read)).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            if (read > MaxBodyChars) { await WriteJsonAsync(ctx, 413, new { ok = false, error = "Request too large." }).ConfigureAwait(false); return; }
            body = new string(buffer, 0, read);
        }
        string? url;
        try { url = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("url").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { await WriteJsonAsync(ctx, 400, new { ok = false, error = "Send JSON like {\"url\": \"...\"}." }).ConfigureAwait(false); return; }
        if (string.IsNullOrWhiteSpace(url)) { await WriteJsonAsync(ctx, 400, new { ok = false, error = "No address given." }).ConfigureAwait(false); return; }
        url = url.Trim();

        if (DownloadManager.IsTorrentUrl(url))
        {
            var result = _manager.AddTorrent(url, _torrentFolder());
            await WriteJsonAsync(ctx, result.Error != null ? 400 : 200, new { ok = result.Error == null, error = result.Error, duplicate = result.Duplicate, id = result.Item?.Id }).ConfigureAwait(false);
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await WriteJsonAsync(ctx, 400, new { ok = false, error = "Only http(s) links and magnet/.torrent addresses are accepted." }).ConfigureAwait(false);
            return;
        }
        var existing = _manager.FindActive(url);
        if (existing != null) { await WriteJsonAsync(ctx, 200, new { ok = true, duplicate = true, id = existing.Id }).ConfigureAwait(false); return; }

        var name = DownloadFileNamer.Sanitize(Uri.UnescapeDataString(uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "download"));
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        var item = new DownloadItem { Url = url, FilePath = Path.Combine(_defaultFolder(), name) };
        _manager.Enqueue(item);
        await WriteJsonAsync(ctx, 200, new { ok = true, id = item.Id }).ConfigureAwait(false);
    }

    static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await WriteAsync(ctx, status, "application/json; charset=utf-8", bytes).ConfigureAwait(false);
    }

    static async Task WriteAsync(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        try { await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false); } finally { ctx.Response.Close(); }
    }

    const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Epsilon Download Manager</title>
        <style>
          body { font-family: system-ui, sans-serif; margin: 0; padding: 16px; background: #0b1220; color: #e8eef6; }
          h1 { font-size: 18px; } .row { padding: 10px 0; border-bottom: 1px solid #26314c; }
          .name { font-weight: 600; } .meta { font-size: 12px; color: #93a2c0; margin-top: 3px; }
          .bar { height: 4px; background: #26314c; border-radius: 2px; margin-top: 6px; overflow: hidden; }
          .fill { height: 100%; background: #3794ff; }
          input, button { font-size: 14px; padding: 8px; border-radius: 6px; border: 1px solid #26314c; background: #101a2e; color: #e8eef6; }
          button { background: #0e639c; border: none; margin-left: 6px; }
        </style></head><body>
        <h1>Epsilon Download Manager</h1>
        <div><input id="url" placeholder="Paste a link or magnet..." style="width:70%"><button onclick="add()">Add</button></div>
        <p id="msg" class="meta"></p>
        <div id="list"></div>
        <script>
          const token = new URLSearchParams(location.search).get("token") || "";
          // File names come from the internet: always escape them before putting them into the page.
          const esc = s => { const d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; };
          const pct = v => Math.max(0, Math.min(100, Number(v) || 0));
          async function refresh() {
            try {
              const r = await fetch("/api/status?token=" + encodeURIComponent(token));
              const d = await r.json();
              document.getElementById("list").innerHTML = (d.items || []).map(i =>
                `<div class="row"><div class="name">${esc(i.name)}</div><div class="meta">${esc(i.statusText)} ${esc(i.speed || "")} ${esc(i.size || "")}</div>
                 <div class="bar"><div class="fill" style="width:${pct(i.progress)}%"></div></div></div>`).join("");
            } catch (e) { /* try again next tick */ }
          }
          async function add() {
            const url = document.getElementById("url").value.trim();
            if (!url) return;
            const r = await fetch("/api/add?token=" + encodeURIComponent(token), { method: "POST", body: JSON.stringify({ url }) });
            const d = await r.json();
            document.getElementById("msg").textContent = d.ok ? (d.duplicate ? "Already in the list." : "Added.") : (d.error || "Could not add it.");
            if (d.ok) document.getElementById("url").value = "";
            refresh();
          }
          refresh(); setInterval(refresh, 2000);
        </script></body></html>
        """;

    public void Dispose() => Stop();
}
