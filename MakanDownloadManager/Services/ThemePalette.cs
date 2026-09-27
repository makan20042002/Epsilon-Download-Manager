using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Colours of the app's themes: light, dark "blue grey", orange/bone-white, and Makan Luxury (epsilon).</summary>
public static class ThemePalette
{
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["Bg"] = "#F3F5F9", ["Panel"] = "#F3F5F9", ["Panel2"] = "#E8EDF6", ["Surface"] = "#FFFFFF", ["Border"] = "#E1E6EE",
        ["Text"] = "#1B2430", ["Muted"] = "#5F6B7A", ["Faint"] = "#8A93A3",
        ["Accent"] = "#2F6FEB", ["AccentDark"] = "#1F56C4", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#EDF2FC", ["Selected"] = "#DCE7FC",
        ["Success"] = "#15803D", ["Danger"] = "#C62828", ["Warning"] = "#B45309", ["SuccessBg"] = "#E7F6EC",
        ["HeaderBg"] = "#F8FAFD", ["Track"] = "#E3E8F1", ["Input"] = "#FFFFFF", ["InputBorder"] = "#CBD2DE", ["Popup"] = "#FFFFFF",
        ["IconGrey"] = "#5B6577", ["IconPurple"] = "#7C3AED", ["IconTeal"] = "#0E7490", ["IconAmber"] = "#B45309",
        ["ScrollThumb"] = "#C3CAD6", ["ScrollThumbHover"] = "#A7B0BF", ["Disabled"] = "#9AA3B0"
    };

    public static readonly IReadOnlyDictionary<string, string> Dark = new Dictionary<string, string>
    {
        ["Bg"] = "#07111F", ["Panel"] = "#0B1728", ["Panel2"] = "#101D30", ["Surface"] = "#14243A", ["Border"] = "#213650",
        ["Text"] = "#F4F8FF", ["Muted"] = "#A9B8CC", ["Faint"] = "#667A94",
        ["Accent"] = "#39F5B0", ["AccentDark"] = "#38D9FF", ["OnAccent"] = "#07111F",
        ["Hover"] = "#162B46", ["Selected"] = "#1C3556",
        ["Success"] = "#39F5B0", ["Danger"] = "#FF5C7A", ["Warning"] = "#F6C453", ["SuccessBg"] = "#0D2A22",
        ["HeaderBg"] = "#0B1728", ["Track"] = "#101D30", ["Input"] = "#0B1728", ["InputBorder"] = "#213650", ["Popup"] = "#101D30",
        ["IconGrey"] = "#A9B8CC", ["IconPurple"] = "#A78BFA", ["IconTeal"] = "#38D9FF", ["IconAmber"] = "#F6C453",
        ["ScrollThumb"] = "#213650", ["ScrollThumbHover"] = "#38D9FF", ["Disabled"] = "#4A5668"
    };

    public static readonly IReadOnlyDictionary<string, string> Orange = new Dictionary<string, string>
    {
        ["Bg"] = "#FAF6EF", ["Panel"] = "#FAF6EF", ["Panel2"] = "#F2EBDD", ["Surface"] = "#FFFFFF", ["Border"] = "#E6DCC8",
        ["Text"] = "#3A2E1F", ["Muted"] = "#7A6A54", ["Faint"] = "#9C8D77",
        ["Accent"] = "#D6690A", ["AccentDark"] = "#B85400", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#F5EADA", ["Selected"] = "#F7DDBB",
        ["Success"] = "#2E7D32", ["Danger"] = "#C62828", ["Warning"] = "#B45309", ["SuccessBg"] = "#E7F3E8",
        ["HeaderBg"] = "#F5EFE3", ["Track"] = "#E6DCC8", ["Input"] = "#FFFFFF", ["InputBorder"] = "#D8CBB0", ["Popup"] = "#FFFFFF",
        ["IconGrey"] = "#7A6A54", ["IconPurple"] = "#8E5CD9", ["IconTeal"] = "#0E7490", ["IconAmber"] = "#B45309",
        ["ScrollThumb"] = "#D8CBB0", ["ScrollThumbHover"] = "#C4B292", ["Disabled"] = "#B3A48C"
    };

    public static readonly IReadOnlyDictionary<string, string> Epsilon = new Dictionary<string, string>
    {
        ["Bg"] = "#07111F", ["Panel"] = "#0B1728", ["Panel2"] = "#101D30", ["Surface"] = "#14243A", ["Border"] = "#213650",
        ["Text"] = "#F4F8FF", ["Muted"] = "#A9B8CC", ["Faint"] = "#667A94",
        ["Accent"] = "#39F5B0", ["AccentDark"] = "#38D9FF", ["OnAccent"] = "#07111F",
        ["Hover"] = "#162B46", ["Selected"] = "#1C3556",
        ["Success"] = "#39F5B0", ["Danger"] = "#FF5C7A", ["Warning"] = "#F6C453", ["SuccessBg"] = "#0D2A22",
        ["HeaderBg"] = "#0B1728", ["Track"] = "#101D30", ["Input"] = "#0B1728", ["InputBorder"] = "#213650", ["Popup"] = "#101D30",
        ["IconGrey"] = "#A9B8CC", ["IconPurple"] = "#A78BFA", ["IconTeal"] = "#38D9FF", ["IconAmber"] = "#F6C453",
        ["ScrollThumb"] = "#213650", ["ScrollThumbHover"] = "#38D9FF", ["Disabled"] = "#4A5668"
    };

    public static string Resolve(string? mode, bool windowsIsDark) => mode switch { "orange" => "orange", "epsilon" => "epsilon", "dark" => "dark", "auto" when windowsIsDark => "dark", _ => "light" };

    public static IReadOnlyDictionary<string, string> For(string resolved) => resolved switch { "dark" => Dark, "orange" => Orange, "epsilon" => Epsilon, _ => Light };

    public static (byte R, byte G, byte B) Parse(string hex)
    {
        var h = hex.TrimStart('#');
        return (byte.Parse(h[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture), byte.Parse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG contrast ratio between two colours (1 = none, 21 = black on white).</summary>
    public static double Contrast(string a, string b)
    {
        static double Lum((byte R, byte G, byte B) c)
        {
            static double Channel(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        var la = Lum(Parse(a)); var lb = Lum(Parse(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }
}

