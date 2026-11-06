using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Colours and migration rules for every built-in application theme.</summary>
public static class ThemePalette
{
    public static readonly string[] Names =
    [
        "obsidian-gold", "platinum-blue", "royal-amethyst", "emerald-executive", "champagne-minimal",
        "graphite-copper", "sapphire-noir", "rose-titanium", "arctic-glass", "crystal-noir", "dracula", "makan-lab", "mint-glass"
    ];

    public static readonly IReadOnlyDictionary<string, string> ObsidianGold = Palette(
        "#0A0907", "#11100D", "#181611", "#201D16", "#4A3B22", "#FFF8E7", "#D8C7A0", "#8F7B54", "#120E05",
        "#E0B75A", "#A97822", "#120E05", "#28231A", "#3A2F1C", "#64D68A", "#FF6B6B", "#F4C66A", "#13251A",
        "#11100D", "#181611", "#0D0C09", "#4A3B22", "#181611", "#D8C7A0", "#C69CFF", "#63D6C5", "#E0B75A", "#4A3B22", "#E0B75A", "#6E634E");

    public static readonly IReadOnlyDictionary<string, string> MintGlass = Palette(
        "#EAF9F2", "#F3FCF8", "#D8F1E6", "#FFFFFF", "#C6E8DB", "#14392F", "#476F63", "#7A9C92", "#FFFFFF",
        "#0A8268", "#066350", "#FFFFFF", "#E1F5EE", "#CBEBDF", "#197347", "#B4233E", "#9A5700", "#DDF3E8",
        "#F3FCF8", "#D8F1E6", "#FFFFFF", "#B5DACE", "#FFFFFF", "#4D6B62", "#5B6BD6", "#1FB5A0", "#9A5700", "#B5DACE", "#0A8268", "#8AA39B");

    public static readonly IReadOnlyDictionary<string, string> PlatinumBlue = Palette(
        "#EFF7FF", "#F5FAFF", "#E3F0FC", "#FFFFFF", "#D5E5F5", "#173456", "#52708F", "#7E98B4", "#FFFFFF",
        "#0772EA", "#1554AD", "#FFFFFF", "#E7F3FF", "#DCEEFF", "#197347", "#B4233E", "#9A5700", "#E1F3EA",
        "#F5FAFF", "#E3F0FC", "#FFFFFF", "#BFD6EC", "#FFFFFF", "#536B84", "#4F6BED", "#26B3FF", "#9A5700", "#BFD6EC", "#0772EA", "#8B9DB0");

    public static readonly IReadOnlyDictionary<string, string> RoyalAmethyst = Palette(
        "#21162D", "#2A1C3A", "#35244A", "#3A284F", "#53396F", "#F4EDFB", "#C0B0D6", "#8E7AA8", "#1B0F2B",
        "#B98AF5", "#9566DD", "#1B0F2B", "#45305E", "#50367A", "#55D9A6", "#FF7392", "#FFBE55", "#123027",
        "#2A1C3A", "#35244A", "#1A1024", "#53396F", "#35244A", "#C0B0D6", "#D6A8FF", "#5ADBE6", "#FFBE55", "#53396F", "#B98AF5", "#7A6A92");

    public static readonly IReadOnlyDictionary<string, string> EmeraldExecutive = Palette(
        "#061412", "#08221D", "#0D3029", "#123B32", "#245C4E", "#EDFFF9", "#A7D7C8", "#5E9B89", "#041410",
        "#45D8AE", "#15896B", "#03140F", "#17483D", "#1D594A", "#55E6A8", "#FF6F78", "#F1C75B", "#0D3125",
        "#08221D", "#0D3029", "#071A17", "#245C4E", "#0D3029", "#A7D7C8", "#BD91FF", "#45D8AE", "#F1C75B", "#245C4E", "#45D8AE", "#52796E");

    public static readonly IReadOnlyDictionary<string, string> ChampagneMinimal = Palette(
        "#FFF7ED", "#FFFBF5", "#F9EAD6", "#FFFFFF", "#EBD7BC", "#3A2412", "#74553A", "#A58A70", "#FFFFFF",
        "#C2531A", "#9A3E10", "#FFFFFF", "#FCEBD8", "#F8DDBF", "#197347", "#B4233E", "#8A5200", "#E3F1E4",
        "#FFFBF5", "#F9EAD6", "#FFFFFF", "#DFC6A5", "#FFFFFF", "#7A5B3F", "#7E57C2", "#1E9C95", "#8A5200", "#DFC6A5", "#C2531A", "#A99B8A");

    public static readonly IReadOnlyDictionary<string, string> GraphiteCopper = Palette(
        "#191B21", "#20232A", "#292D36", "#2D313B", "#434955", "#F5F1E8", "#B4B0A6", "#7F8088", "#1A1406",
        "#E9AD4B", "#C48A2C", "#1A1406", "#353A45", "#43391F", "#5BD88F", "#FF7080", "#F4C66A", "#17291F",
        "#20232A", "#292D36", "#15171C", "#434955", "#2D313B", "#B4B0A6", "#B79CFF", "#5ADBE6", "#E9AD4B", "#434955", "#E9AD4B", "#7F8088");

    public static readonly IReadOnlyDictionary<string, string> SapphireNoir = Palette(
        "#050E1F", "#0A1A33", "#10264A", "#122E55", "#23497D", "#EAF3FF", "#A5C0E3", "#6A8CB4", "#06142B",
        "#4BA0FF", "#2E86FF", "#06142B", "#17376A", "#1D4682", "#4ED6A8", "#FF6B7D", "#FFB44A", "#0E2D28",
        "#0A1A33", "#10264A", "#081428", "#23497D", "#10264A", "#A5C0E3", "#A98CFF", "#45CFEA", "#FFB44A", "#23497D", "#4BA0FF", "#5A7596");

    public static readonly IReadOnlyDictionary<string, string> IvoryLuxe = Palette(
        "#F8F4EC", "#FFFDF8", "#ECE5D8", "#FFFFFF", "#D8CBB8", "#2A241B", "#6B604F", "#9B8C75", "#FFFFFF",
        "#93642D", "#704518", "#FFFFFF", "#F2ECE2", "#E5D8C4", "#267348", "#B42C42", "#925200", "#E5F1E9",
        "#FFFDF8", "#ECE5D8", "#FFFFFF", "#D0C1AC", "#FFFFFF", "#6B604F", "#6E4C9D", "#1F7280", "#93642D", "#D0C1AC", "#93642D", "#A19584");

    public static readonly IReadOnlyDictionary<string, string> RoseTitanium = Palette(
        "#120B10", "#20111B", "#301825", "#3C1E30", "#6A3B56", "#FFF3F9", "#D9B4C8", "#93677E", "#260C18",
        "#E187B1", "#9D3F6E", "#FFFFFF", "#48243A", "#5B2C48", "#63D59A", "#FF6680", "#F2C565", "#17291F",
        "#20111B", "#301825", "#180D14", "#6A3B56", "#301825", "#D9B4C8", "#C69AFF", "#66D2D6", "#F2C565", "#6A3B56", "#E187B1", "#79596A");

    public static readonly IReadOnlyDictionary<string, string> ArcticGlass = Palette(
        "#07131F", "#0B2133", "#103049", "#153C58", "#2B6687", "#EDF9FF", "#A9D4E8", "#6E9DB5", "#07131F",
        "#45C7FF", "#0D86C6", "#03131D", "#174C6D", "#1E5D80", "#62E0B1", "#FF6F91", "#FFD166", "#10382E",
        "#0B2133", "#103049", "#081A28", "#2B6687", "#103049", "#A9D4E8", "#BFA5FF", "#55DFE5", "#FFD166", "#2B6687", "#45C7FF", "#557F95");

    /// <summary>A dark, translucent jewel palette intended for the Windows acrylic backdrop.</summary>
    public static readonly IReadOnlyDictionary<string, string> CrystalNoir = Palette(
        "#B20A0D18", "#C4141928", "#B91C2235", "#CC242B40", "#805E6A86", "#FFF8FAFF", "#FFD2D8E8", "#FF909AB1", "#FF100C1D",
        "#FFB89CFF", "#FF8063D8", "#FF100C1D", "#753A435E", "#8A46516F", "#FF6DE0B3", "#FFFF6F91", "#FFFFD37A", "#99213B34",
        "#C4141928", "#A91B2233", "#C8101421", "#8A5E6A86", "#D81B2031", "#FFD2D8E8", "#FFD89BFF", "#FF65D9E8", "#FFFFC56E", "#995E6A86", "#FFB89CFF", "#FF6F7890");

    public static readonly IReadOnlyDictionary<string, string> Dracula = Palette(
        "#1E1F29", "#282A36", "#343746", "#3C3F51", "#5A5F78", "#F8F8F2", "#C9C9C2", "#8B8D9A", "#1E1F29",
        "#BD93F9", "#8B5CC7", "#171820", "#44475A", "#50536A", "#50FA7B", "#FF6E78", "#F1FA8C", "#243C2B",
        "#282A36", "#343746", "#21222C", "#5A5F78", "#343746", "#C9C9C2", "#FF79C6", "#8BE9FD", "#FFB86C", "#5A5F78", "#BD93F9", "#6B6E82");

    /// <summary>The MAKAN LAB website palette: ink-black surfaces, electric blue and warm amber.</summary>
    public static readonly IReadOnlyDictionary<string, string> MakanLab = Palette(
        "#05070D", "#0A0E17", "#101724", "#151E2E", "#24324A", "#F2F6FC", "#A9B6C8", "#718096", "#F2F6FC",
        "#58A6FF", "#2F81F7", "#07111F", "#152238", "#1B3150", "#4ED6A8", "#FF6B7D", "#FFB44A", "#102A24",
        "#0A0E17", "#111A28", "#080C14", "#293A55", "#101724", "#A9B6C8", "#B79CFF", "#55D6E8", "#FFB44A", "#293A55", "#58A6FF", "#59677A");

    /// <summary>True for the themes with a dark window.</summary>
    public static bool IsDarkTheme(string resolved) => resolved is not ("platinum-blue" or "mint-glass" or "champagne-minimal");

    public static string Normalize(string? theme)
    {
        var value = (theme ?? "").Trim().ToLowerInvariant();
        if (value == "ivory-luxe") return "champagne-minimal";
        if (value == "auto" || Names.Contains(value)) return value;
        return value switch
        {
            "light" => "platinum-blue",
            "orange" => "champagne-minimal",
            "obsidian" or "uhnohh" => "obsidian-gold",
            "nebula" => "royal-amethyst",
            "lilac" => "rose-titanium",
            "dracula" => "dracula",
            "makan" or "dark" or "epsilon" => "sapphire-noir",
            _ => "sapphire-noir"
        };
    }

    public static string Resolve(string? theme, bool windowsDark) =>
        Normalize(theme) == "auto" ? (windowsDark ? "sapphire-noir" : "platinum-blue") : Normalize(theme);

    public static IReadOnlyDictionary<string, string> For(string? theme, bool windowsDark = false) => Resolve(theme, windowsDark) switch
    {
        "obsidian-gold" => ObsidianGold,
        "platinum-blue" => PlatinumBlue,
        "mint-glass" => MintGlass,
        "royal-amethyst" => RoyalAmethyst,
        "emerald-executive" => EmeraldExecutive,
        "champagne-minimal" => ChampagneMinimal,
        "graphite-copper" => GraphiteCopper,
        "rose-titanium" => RoseTitanium,
        "arctic-glass" => ArcticGlass,
        "crystal-noir" => CrystalNoir,
        "dracula" => Dracula,
        "makan-lab" => MakanLab,
        _ => SapphireNoir
    };

    public static (byte R, byte G, byte B) Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 8) value = value[2..];
        return (
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static (byte A, byte R, byte G, byte B) ParseArgb(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6)
        {
            var (r, g, b) = Parse(value);
            return (255, r, g, b);
        }
        if (value.Length != 8) throw new FormatException($"Invalid colour '{hex}'.");
        return (
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static double Contrast(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var (r, g, b) = Parse(hex);
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
        }

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static IReadOnlyDictionary<string, string> Palette(params string[] values)
    {
        string[] keys =
        [
            "Bg", "Panel", "Panel2", "Surface", "Border", "Text", "Muted", "Faint", "TopBarText", "Accent", "AccentDark",
            "OnAccent", "Hover", "Selected", "Success", "Danger", "Warning", "SuccessBg", "HeaderBg", "Track", "Input",
            "InputBorder", "Popup", "IconGrey", "IconPurple", "IconTeal", "IconAmber", "ScrollThumb", "ScrollThumbHover", "Disabled"
        ];
        if (values.Length != keys.Length) throw new ArgumentException("A theme palette must define every colour.", nameof(values));
        return keys.Select((key, index) => (key, values[index])).ToDictionary(pair => pair.key, pair => pair.Item2);
    }
}
