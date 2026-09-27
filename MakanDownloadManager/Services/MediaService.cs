using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MakanDownloadManager.Services;

public sealed record MediaStreamInfo(string Id, string Type, string Quality, string Codec, string Url, long? Bitrate, int? Width, int? Height, string? Language, string? AudioUrl = null, double? DurationSeconds = null);

public sealed class MediaService
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<MediaStreamInfo>> GetStreamsAsync(string url, string? cookie = null, string? referrer = null, string? userAgent = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Input must be an HTTP/HTTPS URL.", nameof(url));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? "Mozilla/5.0 MakanDownloadManager/10" : userAgent);
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(referrer)) request.Headers.TryAddWithoutValidation("Referer", referrer);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            return ParseHls(uri, text);
        if (uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) || text.Contains("<MPD", StringComparison.OrdinalIgnoreCase))
            return ParseDash(uri, text);
        throw new InvalidOperationException("The URL is not an HLS or DASH manifest.");
    }

    /// <summary>Total length of a finished HLS media playlist (null when unknown, live, or a master playlist).</summary>
    public async Task<double?> GetHlsDurationAsync(string playlistUrl, string? cookie, string? referrer, string? userAgent, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, playlistUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? "Mozilla/5.0 MakanDownloadManager/10" : userAgent);
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (!string.IsNullOrWhiteSpace(referrer)) request.Headers.TryAddWithoutValidation("Referer", referrer);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!HlsPlaylist.LooksLikePlaylist(text)) return null;
        var playlist = HlsPlaylist.Parse(response.RequestMessage?.RequestUri ?? new Uri(playlistUrl), text);
        return !playlist.IsMaster && playlist.IsEndList && playlist.Segments.Count > 0 ? playlist.TotalDuration : null;
    }

    /// <summary>Finds ffmpeg: the configured path, next to the app, or anywhere on PATH.</summary>
    public static string ResolveFfmpeg(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "ffmpeg.exe" : configured.Trim().Trim('"');
        if (File.Exists(name)) return Path.GetFullPath(name);
        var file = Path.GetFileName(name);
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, file), Path.Combine(AppContext.BaseDirectory, "ffmpeg", file) };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => { try { return Path.Combine(dir.Trim().Trim('"'), file); } catch { return ""; } }));
        var found = candidates.FirstOrDefault(c => c.Length > 0 && File.Exists(c));
        return found ?? throw new FileNotFoundException("FFmpeg was not found. Install ffmpeg (or put ffmpeg.exe next to Makan) and set its path here.", name);
    }

    public async Task RunFfmpegAsync(string ffmpeg, string input, string output, string? cookie = null, string? referrer = null, string? userAgent = null, string? audioInput = null, CancellationToken ct = default)
    {
        ffmpeg = ResolveFfmpeg(ffmpeg);
        foreach (var value in new[] { input, audioInput })
            if (value != null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                throw new ArgumentException("Input must be an HTTP/HTTPS URL.", nameof(input));

        var psi = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");

        // ffmpeg honours only ONE -headers option per input, so both headers go in a single value.
        var headers = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(cookie)) headers.Append("Cookie: ").Append(cookie.Trim()).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(referrer)) headers.Append("Referer: ").Append(referrer.Trim()).Append("\r\n");
        void AddInput(string url)
        {
            if (headers.Length > 0) { psi.ArgumentList.Add("-headers"); psi.ArgumentList.Add(headers.ToString()); }
            if (!string.IsNullOrWhiteSpace(userAgent)) { psi.ArgumentList.Add("-user_agent"); psi.ArgumentList.Add(userAgent); }
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);
        }
        AddInput(input);
        if (audioInput != null)
        {
            // HLS often keeps audio in a separate rendition; without this the saved video would be silent.
            AddInput(audioInput);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0?");
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a:0?");
        }
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        // Drain both pipes while ffmpeg runs: it is chatty on stderr, and an unread pipe fills up and freezes it forever.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        var error = await stderrTask; await stdoutTask;
        if (process.ExitCode != 0)
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            var tail = string.Join(Environment.NewLine, error.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(6));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(tail) ? "FFmpeg failed." : tail);
        }
    }

    /// <summary>Stream-copies local files (TS / fragmented MP4 parts) into one container, optionally merging a separate audio file.</summary>
    public static async Task RemuxAsync(string ffmpeg, IReadOnlyList<string> inputs, string output, string format, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-hide_banner");
        foreach (var input in inputs) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(input); }
        if (inputs.Count == 2) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0?"); psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a:0?"); }
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(format);
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(ct);
        var error = await stderrTask; await stdoutTask;
        if (process.ExitCode != 0)
        {
            var tail = string.Join(" ", error.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(3));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(tail) ? "FFmpeg failed." : tail);
        }
    }

    static IReadOnlyList<MediaStreamInfo> ParseHls(Uri baseUri, string text)
    {
        var lines = text.Split('\n').Select(x => x.Trim()).ToArray();
        var result = new List<MediaStreamInfo>();

        // Separate audio renditions (#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=...,URI=...), preferring the DEFAULT one of each group.
        var audioGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)))
        {
            var media = ParseAttrs(line[13..]);
            if (!string.Equals(media.GetValueOrDefault("TYPE"), "AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
            var group = media.GetValueOrDefault("GROUP-ID"); var uriValue = media.GetValueOrDefault("URI");
            if (group == null || string.IsNullOrWhiteSpace(uriValue)) continue;
            var isDefault = string.Equals(media.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase);
            if (isDefault || !audioGroups.ContainsKey(group)) audioGroups[group] = new Uri(baseUri, uriValue).ToString();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase) || i + 1 >= lines.Length) continue;
            var attrs = ParseAttrs(lines[i][18..]);
            var raw = lines[++i]; if (raw.StartsWith('#')) continue;
            var uri = new Uri(baseUri, raw).ToString();
            var resolution = attrs.GetValueOrDefault("RESOLUTION") ?? "Auto";
            var quality = resolution;
            var codec = attrs.GetValueOrDefault("CODECS") ?? "Unknown";
            long? bitrate = long.TryParse(attrs.GetValueOrDefault("BANDWIDTH"), out var br) ? br : null;
            int? width = null, height = null;
            var m = Regex.Match(resolution, @"^(\d+)x(\d+)$"); if (m.Success) { width = int.Parse(m.Groups[1].Value); height = int.Parse(m.Groups[2].Value); }
            var audioGroup = attrs.GetValueOrDefault("AUDIO");
            string? audioUrl = audioGroup != null && audioGroups.TryGetValue(audioGroup, out var au) ? au : null;
            result.Add(new MediaStreamInfo($"hls-{result.Count}", "Video", quality, codec, uri, bitrate, width, height, audioGroup, audioUrl));
        }
        if (result.Count > 0) return result;
        double? duration = null;
        try { var media = HlsPlaylist.Parse(baseUri, text); if (media.IsEndList && media.Segments.Count > 0) duration = media.TotalDuration; } catch (Exception) { /* duration is only a nicety */ }
        return new[] { new MediaStreamInfo("hls-source", "Stream", "Source", "Unknown", baseUri.ToString(), null, null, null, null, null, duration) };
    }

    static IReadOnlyList<MediaStreamInfo> ParseDash(Uri baseUri, string text)
    {
        var manifest = DashManifest.Parse(baseUri, text);
        if (manifest.IsDynamic) throw new NotSupportedException("This is a live DASH stream; live streams can't be downloaded yet.");
        var audio = manifest.BestAudio();
        var videos = manifest.Representations.Where(r => r.Kind == "video" && !r.Encrypted).OrderBy(r => r.Height ?? 0).ThenBy(r => r.Bandwidth).ToList();
        if (videos.Count == 0 && audio != null)
            return manifest.Representations.Where(r => r.Kind == "audio" && !r.Encrypted).OrderBy(r => r.Bandwidth)
                .Select(r => new MediaStreamInfo(r.Id, "Audio", $"{r.Bandwidth / 1000} kbps", r.Codecs ?? "Unknown", DashManifest.WithSelection(baseUri, r.Id, null), r.Bandwidth, null, null, r.Lang)).ToList();
        if (videos.Count == 0) throw new NotSupportedException(manifest.Representations.Any(r => r.Encrypted) ? "This stream is protected with DRM and can't be downloaded." : "The manifest lists no video.");
        return videos.Select(v => new MediaStreamInfo(v.Id, "Video", v.Width.HasValue && v.Height.HasValue ? $"{v.Width}x{v.Height}" : $"{v.Bandwidth / 1000} kbps",
            v.Codecs ?? "Unknown", DashManifest.WithSelection(baseUri, v.Id, audio?.Id), v.Bandwidth, v.Width, v.Height, null, null, manifest.DurationSeconds)).ToList();
    }

    /// <summary>Parses HLS attribute lists (KEY=value,KEY="quoted, with commas"); commas inside quotes do not split.</summary>
    internal static Dictionary<string, string> ParseAttrs(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < input.Length)
        {
            var eq = input.IndexOf('=', i);
            if (eq < 0) break;
            var key = input[i..eq].Trim().TrimStart(',').Trim();
            i = eq + 1;
            string value;
            if (i < input.Length && input[i] == '"')
            {
                var close = input.IndexOf('"', i + 1);
                if (close < 0) close = input.Length;
                value = input[(i + 1)..close];
                i = Math.Min(input.Length, close + 1);
            }
            else
            {
                var comma = input.IndexOf(',', i);
                if (comma < 0) comma = input.Length;
                value = input[i..comma].Trim();
                i = comma;
            }
            if (key.Length > 0) result[key] = value;
            while (i < input.Length && (input[i] == ',' || input[i] == ' ')) i++;
        }
        return result;
    }
    public static bool IsStream(string url) => url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || url.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
}
