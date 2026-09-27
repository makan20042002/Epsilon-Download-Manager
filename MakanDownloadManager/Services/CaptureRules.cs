using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

/// <summary>
/// What Makan takes over from the browser (IDM's "File types" and "Capture downloads from the following browsers" options):
/// which file types, which browsers, and which sites / addresses are always left to the browser.
/// </summary>
public sealed class CaptureRules
{
    public const string DefaultFileTypes =
        "3GP 7Z AAC ACE AIF APK ARJ ASF AVI BIN BZ2 EXE GZ GZIP IMG ISO LZH M4A M4V MKV MOV MP3 MP4 MPA MPE MPEG MPG MSI MSU OGG OGV PDF PLJ PPS PPT QT R0* R1* RA RAR RM RMVB SEA SIT SITX TAR TIF TIFF WAV WMA WMV Z ZIP " +
        "WEBM FLAC OPUS XZ TGZ DMG DEB RPM APPX MSIX CAB";
    public const string DefaultExcludedSites = "*.update.microsoft.com download.windowsupdate.com siteseal.thawte.com ecom.cimetz.com *.voice2page.com";
    public static readonly string[] AllBrowsers = { "chrome", "edge", "firefox", "opera", "vivaldi", "other" };

    public string FileTypes { get; set; } = DefaultFileTypes;
    public string ExcludedSites { get; set; } = DefaultExcludedSites;
    public string ExcludedAddresses { get; set; } = "";
    public string Browsers { get; set; } = string.Join(",", AllBrowsers);

    public readonly record struct Decision(bool Capture, string Reason);

    static IEnumerable<string> Words(string text) => (text ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"chrome" | "edge" | "firefox" | "opera" | "vivaldi" | "other" from a browser's User-Agent (Brave and other Chromium browsers look like Chrome).</summary>
    public static string BrowserOf(string? userAgent)
    {
        var ua = userAgent ?? "";
        if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)) return "firefox";
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) || ua.Contains("EdgA/", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
        if (ua.Contains("Vivaldi/", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
        if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) || ua.Contains("Chromium/", StringComparison.OrdinalIgnoreCase)) return "chrome";
        return "other";
    }

    /// <summary>Case-insensitive match where * stands for any run of characters.</summary>
    public static bool Wildcard(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(text ?? "", regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static string ExtensionOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var ext = Path.GetExtension(name.Split('?')[0].Replace('\\', '/'));
        return ext.TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// Decides whether a download the browser started should be taken over. <paramref name="fileName"/> is the name the browser or server
    /// gave (may be null); the address' own extension is used when there is none.
    /// </summary>
    public Decision Evaluate(string url, string? fileName, string? userAgent)
    {
        var browser = BrowserOf(userAgent);
        var allowed = Words(Browsers).Select(b => b.ToLowerInvariant()).ToHashSet();
        if (allowed.Count > 0 && !allowed.Contains(browser)) return new(false, $"Makan is set not to take over downloads from this browser ({browser}). See Options > General.");

        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        var host = uri?.Host ?? "";
        if (Words(ExcludedSites).Any(p => Wildcard(p, host))) return new(false, $"Downloads from {host} are left to the browser (Options > File types).");
        if (ExcludedAddresses.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Any(p => p.Length > 0 && Wildcard(p, url)))
            return new(false, "This address is on the list of addresses left to the browser (Options > File types).");

        var ext = ExtensionOf(fileName);
        if (ext.Length == 0) ext = ExtensionOf(uri?.AbsolutePath);
        var types = Words(FileTypes).Select(t => t.TrimStart('.').ToLowerInvariant()).ToList();
        if (types.Count > 0 && ext.Length > 0 && !types.Any(t => Wildcard(t, ext)))
            return new(false, $"Files of type .{ext} are left to the browser (Options > File types).");
        return new(true, "");
    }
}
