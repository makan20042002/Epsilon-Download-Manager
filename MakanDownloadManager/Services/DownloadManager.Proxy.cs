using System.Net;

namespace MakanDownloadManager.Services;

public sealed partial class DownloadManager
{
    // ---- Proxy / SOCKS (like IDM's Proxy/Socks tab). Regular HTTP(S) downloads only - a torrent's connections are raw
    // TCP sockets the engine manages itself, and yt-dlp is a separate process with its own --proxy flag; neither goes
    // through this. Plain properties (not a whole AppSettings reference) to match how every other live-tunable setting
    // in this class already works (the torrent schedule, speed limits, the network boost) - App.ApplySettings() pushes
    // these in the same way.
    public string ProxyMode { get; set; } = "none";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public bool ProxyUseForHttp { get; set; } = true;
    public bool ProxyUseForHttps { get; set; } = true;
    public string ProxyUsername { get; set; } = "";
    public string ProxyPassword { get; set; } = "";

    /// <summary>Reads the owning DownloadManager's current proxy properties on every call, rather than capturing them
    /// once - so a change made in Options while downloads are already running takes effect on the very next request,
    /// with no need to tear down and recreate the shared HttpClient.</summary>
    internal sealed class LiveProxy : IWebProxy
    {
        readonly DownloadManager _owner;
        public LiveProxy(DownloadManager owner) => _owner = owner;

        public ICredentials? Credentials
        {
            get => string.IsNullOrEmpty(_owner.ProxyUsername) ? null : new NetworkCredential(_owner.ProxyUsername, _owner.ProxyPassword);
            set { /* required by IWebProxy; always derived fresh from ProxyUsername/ProxyPassword instead */ }
        }

        public Uri? GetProxy(Uri destination)
        {
            switch (_owner.ProxyMode)
            {
                case "system":
                    try { return HttpClient.DefaultProxy.GetProxy(destination); }
                    catch (Exception) { return null; }   // a broken system proxy setting should not take downloads down with it
                case "manual":
                    if (string.IsNullOrWhiteSpace(_owner.ProxyHost)) return null;
                    if (destination.Scheme == Uri.UriSchemeHttps && !_owner.ProxyUseForHttps) return null;
                    if (destination.Scheme == Uri.UriSchemeHttp && !_owner.ProxyUseForHttp) return null;
                    return new UriBuilder(Uri.UriSchemeHttp, _owner.ProxyHost, _owner.ProxyPort).Uri;
                default: return null;   // "none" (or anything unrecognised): connect directly, same as if no proxy existed
            }
        }

        public bool IsBypassed(Uri host) => GetProxy(host) == null;
    }
}
