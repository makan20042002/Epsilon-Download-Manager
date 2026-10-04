using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Colours of every built-in application theme.</summary>
public static class ThemePalette
{
    public static readonly IReadOnlyDictionary<string, string> Light = new Dictionary<string, string>
    {
        ["Bg"] = "#F3F5F9", ["Panel"] = "#F3F5F9", ["Panel2"] = "#E8EDF6", ["Surface"] = "#FFFFFF", ["Border"] = "#E1E6EE",
        ["Text"] = "#1B2430", ["Muted"] = "#5F6B7A", ["Faint"] = "#8A93A3", ["TopBarText"] = "#FFFFFF",
        ["Accent"] = "#2F6FEB", ["AccentDark"] = "#1F56C4", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#EDF2FC", ["Selected"] = "#DCE7FC",
        ["Success"] = "#15803D", ["Danger"] = "#C62828", ["Warning"] = "#B45309", ["SuccessBg"] = "#E7F6EC",
        ["HeaderBg"] = "#F8FAFD", ["Track"] = "#E3E8F1", ["Input"] = "#FFFFFF", ["InputBorder"] = "#CBD2DE", ["Popup"] = "#FFFFFF",
        ["IconGrey"] = "#5B6577", ["IconPurple"] = "#7C3AED", ["IconTeal"] = "#0E7490", ["IconAmber"] = "#B45309",
        ["ScrollThumb"] = "#C3CAD6", ["ScrollThumbHover"] = "#A7B0BF", ["Disabled"] = "#9AA3B0"
    };

    public static readonly IReadOnlyDictionary<string, string> Orange = new Dictionary<string, string>
    {
        ["Bg"] = "#FAF6EF", ["Panel"] = "#FAF6EF", ["Panel2"] = "#F2EBDD", ["Surface"] = "#FFFFFF", ["Border"] = "#E6DCC8",
        ["Text"] = "#3A2E1F", ["Muted"] = "#7A6A54", ["Faint"] = "#9C8D77", ["TopBarText"] = "#2A1405",
        ["Accent"] = "#D6690A", ["AccentDark"] = "#B85400", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#F5EADA", ["Selected"] = "#F7DDBB",
        ["Success"] = "#2E7D32", ["Danger"] = "#C62828", ["Warning"] = "#B45309", ["SuccessBg"] = "#E7F3E8",
        ["HeaderBg"] = "#F5EFE3", ["Track"] = "#E6DCC8", ["Input"] = "#FFFFFF", ["InputBorder"] = "#D8CBB0", ["Popup"] = "#FFFFFF",
        ["IconGrey"] = "#7A6A54", ["IconPurple"] = "#8E5CD9", ["IconTeal"] = "#0E7490", ["IconAmber"] = "#B45309",
        ["ScrollThumb"] = "#D8CBB0", ["ScrollThumbHover"] = "#C4B292", ["Disabled"] = "#B3A48C"
    };

    public static readonly IReadOnlyDictionary<string, string> Makan = new Dictionary<string, string>
    {
        ["Bg"] = "#07111F", ["Panel"] = "#0B1728", ["Panel2"] = "#101D30", ["Surface"] = "#14243A", ["Border"] = "#213650",
        ["Text"] = "#F4F8FF", ["Muted"] = "#A9B8CC", ["Faint"] = "#667A94", ["TopBarText"] = "#07111F",
        ["Accent"] = "#39F5B0", ["AccentDark"] = "#38D9FF", ["OnAccent"] = "#07111F",
        ["Hover"] = "#162B46", ["Selected"] = "#1C3556",
        ["Success"] = "#39F5B0", ["Danger"] = "#FF5C7A", ["Warning"] = "#F6C453", ["SuccessBg"] = "#0D2A22",
        ["HeaderBg"] = "#0B1728", ["Track"] = "#101D30", ["Input"] = "#0B1728", ["InputBorder"] = "#213650", ["Popup"] = "#101D30",
        ["IconGrey"] = "#A9B8CC", ["IconPurple"] = "#A78BFA", ["IconTeal"] = "#38D9FF", ["IconAmber"] = "#F6C453",
        ["ScrollThumb"] = "#213650", ["ScrollThumbHover"] = "#38D9FF", ["Disabled"] = "#4A5668"
    };

    public static readonly IReadOnlyDictionary<string, string> Obsidian = new Dictionary<string, string>
    {
        ["Bg"] = "#000000", ["Panel"] = "#0A0A0C", ["Panel2"] = "#121214", ["Surface"] = "#17181B", ["Border"] = "#2A2B2F",
        ["Text"] = "#F2F2F4", ["Muted"] = "#9A9CA3", ["Faint"] = "#616369", ["TopBarText"] = "#0A1022",
        ["Accent"] = "#4D7CFF", ["AccentDark"] = "#2F5FE0", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#17181B", ["Selected"] = "#1D2333",
        ["Success"] = "#34D399", ["Danger"] = "#F5455C", ["Warning"] = "#F2B84B", ["SuccessBg"] = "#0E1F19",
        ["HeaderBg"] = "#0A0A0C", ["Track"] = "#121214", ["Input"] = "#0A0A0C", ["InputBorder"] = "#2A2B2F", ["Popup"] = "#121214",
        ["IconGrey"] = "#9A9CA3", ["IconPurple"] = "#8B7CF6", ["IconTeal"] = "#38B6D9", ["IconAmber"] = "#F2B84B",
        ["ScrollThumb"] = "#2A2B2F", ["ScrollThumbHover"] = "#4D7CFF", ["Disabled"] = "#45464B"
    };

    public static readonly IReadOnlyDictionary<string, string> Nebula = new Dictionary<string, string>
    {
        ["Bg"] = "#0A0818", ["Panel"] = "#120F26", ["Panel2"] = "#191532", ["Surface"] = "#201A3F", ["Border"] = "#362C5C",
        ["Text"] = "#F5F2FF", ["Muted"] = "#B3A8D9", ["Faint"] = "#7C6FA3", ["TopBarText"] = "#17102E",
        ["Accent"] = "#A78BFA", ["AccentDark"] = "#E879F9", ["OnAccent"] = "#17102E",
        ["Hover"] = "#221B45", ["Selected"] = "#2C2359",
        ["Success"] = "#39F5B0", ["Danger"] = "#FF5C7A", ["Warning"] = "#F6C453", ["SuccessBg"] = "#14261F",
        ["HeaderBg"] = "#120F26", ["Track"] = "#191532", ["Input"] = "#120F26", ["InputBorder"] = "#362C5C", ["Popup"] = "#191532",
        ["IconGrey"] = "#B3A8D9", ["IconPurple"] = "#E879F9", ["IconTeal"] = "#38D9FF", ["IconAmber"] = "#F6C453",
        ["ScrollThumb"] = "#362C5C", ["ScrollThumbHover"] = "#A78BFA", ["Disabled"] = "#574C7A"
    };

    public static readonly IReadOnlyDictionary<string, string> Lilac = new Dictionary<string, string>
    {
        ["Bg"] = "#11121E", ["Panel"] = "#191A2B", ["Panel2"] = "#23253C", ["Surface"] = "#2D2F50", ["Border"] = "#5C5F8F",
        ["Text"] = "#F5F6EB", ["Muted"] = "#D7DDBB", ["Faint"] = "#8C8EC6", ["TopBarText"] = "#121322",
        ["Accent"] = "#8C8EC6", ["AccentDark"] = "#760E6C", ["OnAccent"] = "#121322",
        ["Hover"] = "#343657", ["Selected"] = "#44466F",
        ["Success"] = "#D7DDBB", ["Danger"] = "#FF6B9E", ["Warning"] = "#F5C96A", ["SuccessBg"] = "#292E2A",
        ["HeaderBg"] = "#191A2B", ["Track"] = "#23253C", ["Input"] = "#151625", ["InputBorder"] = "#5C5F8F", ["Popup"] = "#23253C",
        ["IconGrey"] = "#D7DDBB", ["IconPurple"] = "#E453A0", ["IconTeal"] = "#8C8EC6", ["IconAmber"] = "#F5C96A",
        ["ScrollThumb"] = "#5C5F8F", ["ScrollThumbHover"] = "#E453A0", ["Disabled"] = "#6E708C"
    };

    // Dracula-inspired: charcoal-purple surfaces with its familiar pink/red accent.
    public static readonly IReadOnlyDictionary<string, string> Dracula = new Dictionary<string, string>
    {
        ["Bg"] = "#191A21", ["Panel"] = "#21222C", ["Panel2"] = "#282A36", ["Surface"] = "#303241", ["Border"] = "#44475A",
        ["Text"] = "#F8F8F2", ["Muted"] = "#C5C1D1", ["Faint"] = "#7F7B8D", ["TopBarText"] = "#1B1117",
        ["Accent"] = "#FF5555", ["AccentDark"] = "#FF79C6", ["OnAccent"] = "#1B1117",
        ["Hover"] = "#343746", ["Selected"] = "#493142",
        ["Success"] = "#50FA7B", ["Danger"] = "#FF5555", ["Warning"] = "#F1FA8C", ["SuccessBg"] = "#203525",
        ["HeaderBg"] = "#21222C", ["Track"] = "#282A36", ["Input"] = "#191A21", ["InputBorder"] = "#44475A", ["Popup"] = "#282A36",
        ["IconGrey"] = "#BFBCC9", ["IconPurple"] = "#BD93F9", ["IconTeal"] = "#8BE9FD", ["IconAmber"] = "#F1FA8C",
        ["ScrollThumb"] = "#44475A", ["ScrollThumbHover"] = "#FF79C6", ["Disabled"] = "#666978"
    };

    public static readonly IReadOnlyDictionary<string, string> Uhnohh = new Dictionary<string, string>
    {
        ["Bg"] = "#080808", ["Panel"] = "#101010", ["Panel2"] = "#181818", ["Surface"] = "#222222", ["Border"] = "#3B3B33",
        ["Text"] = "#FFFDE8", ["Muted"] = "#D4D2B8", ["Faint"] = "#8C8B76", ["TopBarText"] = "#111100",
        ["Accent"] = "#FFF700", ["AccentDark"] = "#FFB000", ["OnAccent"] = "#111100",
        ["Hover"] = "#292914", ["Selected"] = "#3A3900",
        ["Success"] = "#B6FF00", ["Danger"] = "#FF3D21", ["Warning"] = "#FFF700", ["SuccessBg"] = "#1D2600",
        ["HeaderBg"] = "#101010", ["Track"] = "#202020", ["Input"] = "#0C0C0C", ["InputBorder"] = "#4A4934", ["Popup"] = "#181818",
        ["IconGrey"] = "#D4D2B8", ["IconPurple"] = "#FF3D91", ["IconTeal"] = "#FF8A00", ["IconAmber"] = "#FFF700",
        ["ScrollThumb"] = "#4A4934", ["ScrollThumbHover"] = "#FFF700", ["Disabled"] = "#666654"
    };

    public static string Resolve(string? mode, bool windowsIsDark) => mode switch
    {
        "makan" or "dark" or "epsilon" => "makan",
        "obsidian" => "obsidian",
        "nebula" => "nebula",
        "lilac" => "lilac",
        "dracula" => "dracula",
        "uhnohh" => "uhnohh",
        "orange" => "orange",
        "auto" when windowsIsDark => "makan",
        _ => "light"
    };

    public static IReadOnlyDictionary<string, string> For(string resolved) => resolved switch
    {
        "makan" or "dark" or "epsilon" => Makan,
        "obsidian" => Obsidian,
        "nebula" => Nebula,
        "lilac" => Lilac,
        "dracula" => Dracula,
        "uhnohh" => Uhnohh,
        "orange" => Orange,
        _ => Light
    };

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
