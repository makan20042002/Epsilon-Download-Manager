using System.Net;

namespace MakanDownloadManager.Services;

/// <summary>
/// Follows redirects itself so that credentials never leave the site they were meant for.
/// The built-in redirect handling forwards a manually-set Cookie header to whatever host the server redirects to
/// (a login-protected link that bounces to a third-party CDN would leak the session cookie). Browsers don't do that, and neither do we:
/// when a redirect changes scheme, host or port, the Cookie and Authorization headers are dropped.
/// </summary>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    const int MaxHops = 10;

    public SafeRedirectHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            var response = await base.SendAsync(current, ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                response.RequestMessage = current; // callers read the FINAL url from here (file naming)
                return response;
            }

            var target = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
            if (target.Scheme is not ("http" or "https")) { response.RequestMessage = current; return response; }
            if (hop >= MaxHops) { response.Dispose(); throw new HttpRequestException("Too many redirects."); }

            var sameOrigin = string.Equals(target.Scheme, current.RequestUri!.Scheme, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(target.Host, current.RequestUri.Host, StringComparison.OrdinalIgnoreCase) &&
                             target.Port == current.RequestUri.Port;

            var next = new HttpRequestMessage(current.Method, target) { Version = current.Version, VersionPolicy = current.VersionPolicy };
            foreach (var header in current.Headers)
            {
                if (!sameOrigin && (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                    continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Dispose();
            current = next;
        }
    }

    static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
