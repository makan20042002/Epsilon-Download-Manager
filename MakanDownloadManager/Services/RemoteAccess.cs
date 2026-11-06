using System.Globalization;
using System.Security.Cryptography;

namespace MakanDownloadManager.Services;

/// <summary>The address a phone opens to control Epsilon on this PC (the text under the QR code).</summary>
public static class RemoteAccess
{
    public const int DefaultPort = 8990;
    public const string NoAddressPlaceholder = "<this computer's IP>";

    /// <summary>True when <paramref name="address"/> is a real network address (not the placeholder shown when no network is connected).</summary>
    public static bool HasAddress(string? address) => !string.IsNullOrWhiteSpace(address) && !address.TrimStart().StartsWith('<');

    /// <summary>A port from text typed by the user. Anything that is not 1-65535 gives <paramref name="fallback"/>.</summary>
    public static int ParsePort(string? text, int fallback)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536 ? port : fallback;

    /// <summary>http://address:port/?token=... (IPv6 addresses get brackets, the token is escaped).</summary>
    public static string BuildUrl(string? address, int port, string? token)
    {
        var host = string.IsNullOrWhiteSpace(address) ? NoAddressPlaceholder : address.Trim();
        if (host.Contains(':') && !host.StartsWith('[') && !host.StartsWith('<')) host = "[" + host + "]";
        if (port is < 1 or > 65535) port = DefaultPort;
        return $"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/?token={Uri.EscapeDataString(token ?? "")}";
    }

    /// <summary>A new random access key: 18 lowercase hex characters (72 bits).</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();
}
