using System.Text;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

public enum WildcardKind { Numbers, Letters }

/// <summary>Plain-text helpers for IDM's batch download (wildcards) and for exporting / importing address lists as ordinary .txt files.</summary>
public static class UrlText
{
    public const int MaxBatch = 5000;

    /// <summary>
    /// Expands "http://site/file*.zip" into many addresses. Every '*' is replaced by the same value:
    /// numbers (from..to step, optionally zero-filled to the width of the largest number) or letters (a..z).
    /// </summary>
    public static IReadOnlyList<string> ExpandWildcard(string pattern, WildcardKind kind, int from, int to, int step, bool zeroFill, char letterFrom = 'a', char letterTo = 'z')
    {
        if (string.IsNullOrWhiteSpace(pattern) || !pattern.Contains('*')) throw new ArgumentException("The address must contain a * where the numbers or letters go.");
        var values = new List<string>();
        if (kind == WildcardKind.Numbers)
        {
            if (step <= 0) throw new ArgumentException("The step must be 1 or more.");
            var width = zeroFill ? Math.Max(from, to).ToString().Length : 0;
            if (to < from) throw new ArgumentException("'To' must not be smaller than 'From'.");
            for (long n = from; n <= to && values.Count <= MaxBatch; n += step) values.Add(n.ToString().PadLeft(width, '0'));
        }
        else
        {
            var upper = char.IsUpper(letterFrom);
            var a = char.ToLowerInvariant(letterFrom); var z = char.ToLowerInvariant(letterTo);
            if (a < 'a' || z > 'z' || z < a) throw new ArgumentException("Letters must go from a to z (or A to Z).");
            for (var c = a; c <= z; c++) values.Add((upper ? char.ToUpperInvariant(c) : c).ToString());
        }
        if (values.Count > MaxBatch) throw new ArgumentException($"That would be more than {MaxBatch} addresses.");
        return values.Select(v => pattern.Replace("*", v)).ToList();
    }

    /// <summary>Every http(s) address in a piece of text (a file, the clipboard), in order, without duplicates.</summary>
    public static IReadOnlyList<string> ExtractUrls(string text, int max = 5000)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (Match m in Regex.Matches(text ?? "", @"https?://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) continue;
            if (seen.Add(url)) list.Add(url);
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>One address per line (UTF-8, no byte-order mark). Internal selection details after '#' are dropped.</summary>
    public static string ToTextFile(IEnumerable<string> urls)
    {
        var sb = new StringBuilder();
        foreach (var url in urls) sb.Append(url.Split('#')[0]).Append("\r\n");
        return sb.ToString();
    }
}
