using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Colours of the app's themes: light, dark "blue grey", and orange/bone-white. The window code turns each entry into a brush of the same name.</summary>
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
        // VS Code's actual "Dark+" colours (editor.background, sidebar.background, list.activeSelectionBackground, etc.) -
        // a scheme tested daily by millions of people, used in place of the earlier navy attempt.
        ["Bg"] = "#1E1E1E", ["Panel"] = "#1E1E1E", ["Panel2"] = "#252526", ["Surface"] = "#252526", ["Border"] = "#3C3C3C",
        ["Text"] = "#D4D4D4", ["Muted"] = "#B0B0B0", ["Faint"] = "#8A8A8A",
        ["Accent"] = "#3794FF", ["AccentDark"] = "#0E639C", ["OnAccent"] = "#FFFFFF",
        ["Hover"] = "#2A2D2E", ["Selected"] = "#04395E",
        ["Success"] = "#20E878", ["Danger"] = "#F14C4C", ["Warning"] = "#E2C08D", ["SuccessBg"] = "#1B3B2B",
        ["HeaderBg"] = "#252526", ["Track"] = "#3C3C3C", ["Input"] = "#3C3C3C", ["InputBorder"] = "#5A5A5A", ["Popup"] = "#252526",
        ["IconGrey"] = "#C5C5C5", ["IconPurple"] = "#C586C0", ["IconTeal"] = "#4EC9B0", ["IconAmber"] = "#D7BA7D",
        ["ScrollThumb"] = "#4F4F4F", ["ScrollThumbHover"] = "#5F5F5F", ["Disabled"] = "#767676"
    };

    /// <summary>"light" | "dark" for the setting "light" | "dark" | "auto".</summary>
    public static readonly IReadOnlyDictionary<string, string> Orange = new Dictionary<string, string>
    {
        // Bone-white background with a warm orange accent - a separate, additive theme; Light and Dark above are untouched.
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
        // From the Epsilon UI design kit (THEME.md / theme/EpsilonTheme.xaml) - exact hex values from that spec, not
        // re-derived, so this stays a faithful match to the kit rather than a "close enough" reinterpretation of it.
        // Four keys the kit doesn't name directly (the four Icon* roles) are mapped from its own semantic colours:
        // grey->Muted, purple->its Violet extra, teal->its Cyan accent, amber->its Amber warning colour.
        ["Bg"] = "#070C16", ["Panel"] = "#060B14", ["Panel2"] = "#0E1A2C", ["Surface"] = "#0B1422", ["Border"] = "#1C2839",
        ["Text"] = "#EAF1F7", ["Muted"] = "#93A1B5", ["Faint"] = "#5E6B7E",
        ["Accent"] = "#13C8F5", ["AccentDark"] = "#1F6FEB", ["OnAccent"] = "#03111A",
        ["Hover"] = "#0F1A2A", ["Selected"] = "#0C2233",
        ["Success"] = "#22E58A", ["Danger"] = "#FF6B6B", ["Warning"] = "#FFB44A", ["SuccessBg"] = "#0D2A22",
        ["HeaderBg"] = "#0E1A2C", ["Track"] = "#141D2B", ["Input"] = "#08101C", ["InputBorder"] = "#26344A", ["Popup"] = "#0E1A2C",
        ["IconGrey"] = "#93A1B5", ["IconPurple"] = "#A78BFA", ["IconTeal"] = "#13C8F5", ["IconAmber"] = "#FFB44A",
        ["ScrollThumb"] = "#26344A", ["ScrollThumbHover"] = "#34465F", ["Disabled"] = "#4A5668"
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
