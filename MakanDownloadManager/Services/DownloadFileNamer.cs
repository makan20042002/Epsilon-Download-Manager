using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

public static class DownloadFileNamer
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    /// <summary>Returns a path that does not collide with files on disk (including partial files) or with other queued downloads.</summary>
    public static string MakeUnique(string requestedPath, Func<string, bool>? isClaimed = null)
    {
        var full = Path.GetFullPath(requestedPath);
        var directory = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        Directory.CreateDirectory(directory);

        bool Taken(string p) => File.Exists(p) || File.Exists(p + ".part") || File.Exists(p + ".seg") || (isClaimed?.Invoke(p) ?? false);

        if (!Taken(full)) return full;
        for (var i = 1; i <= 9999; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!Taken(candidate)) return candidate;
        }
        throw new IOException("Unable to create a unique destination filename.");
    }

    /// <summary>Makes a string safe to use as a single Windows file name.</summary>
    public static string Sanitize(string? name, string fallback = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        name = Path.GetFileName(name.Replace('\\', '/'));
        // Windows rules on every OS (a file saved on Linux/macOS may end up on a Windows disk).
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToArray();
        var chars = name.Select(ch => invalid.Contains(ch) || ch < 32 ? '_' : ch).ToArray();
        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        if (Reserved.Contains(stem)) name = "_" + name;
        if (name.Length > 200)
        {
            ext = ext.Length > 20 ? ext[..20] : ext;
            name = name[..Math.Max(1, 200 - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>Best file name the server suggests: Content-Disposition first, then the final (post-redirect) URL.</summary>
    public static string? FromResponse(HttpContentHeaders headers, Uri? finalUri)
    {
        var cd = headers.ContentDisposition;
        var fromHeader = cd?.FileNameStar ?? cd?.FileName;
        if (string.IsNullOrWhiteSpace(fromHeader) && headers.TryGetValues("Content-Disposition", out var raw))
        {
            var m = Regex.Match(string.Join(";", raw), "filename\\*?=(?:UTF-8'[^']*')?\"?([^\";]+)", RegexOptions.IgnoreCase);
            if (m.Success) fromHeader = Uri.UnescapeDataString(m.Groups[1].Value);
        }
        fromHeader = fromHeader?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(fromHeader)) return Sanitize(fromHeader);

        if (finalUri != null)
        {
            var last = Path.GetFileName(Uri.UnescapeDataString(finalUri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(last) && Path.HasExtension(last)) return Sanitize(last);
        }
        return null;
    }
}
