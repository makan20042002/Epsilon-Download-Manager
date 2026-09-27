using System.Globalization;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

/// <summary>
/// Two interface languages: English (the text written in the code and the XAML is the key) and Persian.
/// <see cref="T"/> returns the Persian text for an English string, also for sentences with numbers inside
/// ("Added 3 download(s)" is matched by the entry "Added {0} download(s)"). Unknown strings stay English.
/// </summary>
public static class Loc
{
    static volatile string _language = "en";
    static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    static List<(Regex Pattern, string Template)> _patterns = new();

    public static string Language => _language;
    public static bool IsPersian => _language == "fa";
    /// <summary>Persian is written right-to-left: windows are mirrored.</summary>
    public static bool IsRtl => IsPersian;

    public static void SetLanguage(string? language)
    {
        _language = language == "fa" ? "fa" : "en";
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<(Regex, string)>();
        if (_language == "fa")
            foreach (var (english, persian) in LocFa.Entries)
            {
                if (english.Contains("{0}"))
                {
                    var regex = "^" + Regex.Escape(english) + "$";
                    for (var i = 0; i < 6; i++) regex = regex.Replace(Regex.Escape("{" + i + "}"), "(.+?)");
                    patterns.Add((new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Singleline), persian));
                }
                else exact[english] = persian;
            }
        patterns.Sort((x, y) => y.Item2.Length.CompareTo(x.Item2.Length));   // the most specific sentence first
        _exact = exact;
        _patterns = patterns;
    }

    /// <summary>The text in the current language (English text is returned unchanged).</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || _language != "fa") return text ?? "";
        if (_exact.TryGetValue(text, out var hit)) return hit;
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && _exact.TryGetValue(trimmed, out hit)) return text.Replace(trimmed, hit);
        foreach (var (pattern, template) in _patterns)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            var groups = new object[m.Groups.Count - 1];
            for (var i = 1; i < m.Groups.Count; i++) groups[i - 1] = T(m.Groups[i].Value);   // "Stopped 45%" style parts may translate too
            try { return string.Format(CultureInfo.InvariantCulture, template, groups); } catch (FormatException) { return text; }
        }
        return text;
    }

    /// <summary>Formats with the translated pattern: <c>Loc.F("Added {0} download(s)", 3)</c>.</summary>
    public static string F(string english, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(english), args);
}
