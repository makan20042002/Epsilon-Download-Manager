using System.Net;
using System.Text.RegularExpressions;

namespace MakanDownloadManager.Services;

public sealed class SiteGrabber
{
    readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<List<string>> ExtractAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https")) throw new ArgumentException("Only HTTP/HTTPS pages are supported.");
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        request.Headers.TryAddWithoutValidation("User-Agent", "MakanDownloadManager/10");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(html, "(?:src|href|poster|data-src)\\s*=\\s*[\"']([^\"'#>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) Add(results, baseUri, match.Groups[1].Value);
        foreach (Match match in Regex.Matches(html, "(?:srcset)\\s*=\\s*[\"']([^\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            foreach (var candidate in match.Groups[1].Value.Split(',')) Add(results, baseUri, candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");

        return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Add(HashSet<string> set, Uri baseUri, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("#") || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        try { var uri = new Uri(baseUri, WebUtility.HtmlDecode(value)); if (uri.Scheme is "http" or "https") set.Add(uri.ToString()); } catch { }
    }
}
