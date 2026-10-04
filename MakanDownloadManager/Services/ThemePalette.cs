using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Colours and migration rules for every built-in application theme.</summary>
public static class ThemePalette
{
    public static readonly string[] Names =
    [
        "obsidian-gold", "platinum-blue", "royal-amethyst", "emerald-executive", "champagne-minimal",
        "graphite-copper", "sapphire-noir", "ivory-luxe", "rose-titanium", "arctic-glass"
    ];

    public static readonly IReadOnlyDictionary<string, string> ObsidianGold = Palette(
        "#0A0907", "#11100D", "#181611", "#201D16", "#4A3B22", "#FFF8E7", "#D8C7A0", "#8F7B54", "#120E05",
        "#E0B75A", "#A97822", "#120E05", "#28231A", "#3A2F1C", "#64D68A", "#FF6B6B", "#F4C66A", "#13251A",
        "#11100D", "#181611", "#0D0C09", "#4A3B22", "#181611", "#D8C7A0", "#C69CFF", "#63D6C5", "#E0B75A", "#4A3B22", "#E0B75A", "#6E634E");

    public static readonly IReadOnlyDictionary<string, string> PlatinumBlue = Palette(
        "#EAF4FF", "#F4F9FF", "#DCEBFA", "#FFFFFF", "#B7CEE6", "#12263F", "#4D6680", "#7890A8", "#FFFFFF",
        "#1467D8", "#0C4FAE", "#FFFFFF", "#E2EFFC", "#C8DFFF", "#197347", "#B4233E", "#9A5700", "#E1F3EA",
        "#F4F9FF", "#DCEBFA", "#FFFFFF", "#AFC8E2", "#FFFFFF", "#536B84", "#7047B8", "#08798A", "#9A5700", "#AFC8E2", "#1467D8", "#8B9DB0");

    public static readonly IReadOnlyDictionary<string, string> RoyalAmethyst = Palette(
        "#0C081A", "#150D2C", "#211343", "#2A1854", "#54338B", "#F8F2FF", "#C7B5E5", "#856AAA", "#180A2A",
        "#A873F0", "#6C2CC1", "#FFFFFF", "#321E61", "#432777", "#55DDA6", "#FF668C", "#F4C96B", "#142A24",
        "#150D2C", "#211343", "#100921", "#54338B", "#211343", "#C7B5E5", "#C794FF", "#63D8EA", "#F4C96B", "#54338B", "#A873F0", "#6E5C86");

    public static readonly IReadOnlyDictionary<string, string> EmeraldExecutive = Palette(
        "#061412", "#08221D", "#0D3029", "#123B32", "#245C4E", "#EDFFF9", "#A7D7C8", "#5E9B89", "#041410",
        "#45D8AE", "#15896B", "#03140F", "#17483D", "#1D594A", "#55E6A8", "#FF6F78", "#F1C75B", "#0D3125",
        "#08221D", "#0D3029", "#071A17", "#245C4E", "#0D3029", "#A7D7C8", "#BD91FF", "#45D8AE", "#F1C75B", "#245C4E", "#45D8AE", "#52796E");

    public static readonly IReadOnlyDictionary<string, string> ChampagneMinimal = Palette(
        "#F6F0E7", "#FBF7F1", "#EDE2D2", "#FFFFFF", "#D7C2A4", "#2D241A", "#6F5E49", "#9C8870", "#FFFFFF",
        "#9B682D", "#754817", "#FFFFFF", "#F1E7D8", "#E6D3B7", "#287546", "#B52D3E", "#965400", "#E5F1E8",
        "#FBF7F1", "#EDE2D2", "#FFFFFF", "#CFB999", "#FFFFFF", "#6F5E49", "#724AA2", "#1F7480", "#9B682D", "#CFB999", "#9B682D", "#A2927D");

    public static readonly IReadOnlyDictionary<string, string> GraphiteCopper = Palette(
        "#0C0C0C", "#151311", "#201A16", "#29211B", "#5A3C28", "#FFF4EC", "#D6B9A5", "#8D6D58", "#180B04",
        "#E28A4D", "#9B4D20", "#FFFFFF", "#32251D", "#463020", "#66D796", "#FF6B64", "#F2B95C", "#14271C",
        "#151311", "#201A16", "#100E0D", "#5A3C28", "#201A16", "#D6B9A5", "#C59BFF", "#60D1C0", "#E28A4D", "#5A3C28", "#E28A4D", "#745E50");

    public static readonly IReadOnlyDictionary<string, string> SapphireNoir = Palette(
        "#030A17", "#071429", "#0B2140", "#102B50", "#1B477A", "#EFF7FF", "#A7C4E5", "#5C83AD", "#030A17",
        "#2E86FF", "#1558C0", "#FFFFFF", "#123561", "#174474", "#51DBA3", "#FF657A", "#F4C65B", "#0C2A22",
        "#071429", "#0B2140", "#050F20", "#1B477A", "#0B2140", "#A7C4E5", "#A98CFF", "#45CFEA", "#F4C65B", "#1B477A", "#2E86FF", "#4E6B8B");

    public static readonly IReadOnlyDictionary<string, string> IvoryLuxe = Palette(
        "#F8F4EC", "#FFFDF8", "#ECE5D8", "#FFFFFF", "#D8CBB8", "#2A241B", "#6B604F", "#9B8C75", "#FFFFFF",
        "#93642D", "#704518", "#FFFFFF", "#F2ECE2", "#E5D8C4", "#267348", "#B42C42", "#925200", "#E5F1E9",
        "#FFFDF8", "#ECE5D8", "#FFFFFF", "#D0C1AC", "#FFFFFF", "#6B604F", "#6E4C9D", "#1F7280", "#93642D", "#D0C1AC", "#93642D", "#A19584");

    public static readonly IReadOnlyDictionary<string, string> RoseTitanium = Palette(
        "#120B10", "#20111B", "#301825", "#3C1E30", "#6A3B56", "#FFF3F9", "#D9B4C8", "#93677E", "#260C18",
        "#E187B1", "#9D3F6E", "#FFFFFF", "#48243A", "#5B2C48", "#63D59A", "#FF6680", "#F2C565", "#17291F",
        "#20111B", "#301825", "#180D14", "#6A3B56", "#301825", "#D9B4C8", "#C69AFF", "#66D2D6", "#F2C565", "#6A3B56", "#E187B1", "#79596A");

    public static readonly IReadOnlyDictionary<string, string> ArcticGlass = Palette(
        "#E9F5FF", "#F4FAFF", "#D9ECFA", "#FFFFFF", "#B7D7EC", "#102A43", "#486B86", "#789BB4", "#FFFFFF",
        "#087AB8", "#045A8A", "#FFFFFF", "#E0F1FC", "#C5E5F7", "#167451", "#B42346", "#985600", "#DFF3EB",
        "#F4FAFF", "#D9ECFA", "#FFFFFF", "#ACCEE4", "#FFFFFF", "#486B86", "#6C4BA8", "#087A88", "#985600", "#ACCEE4", "#087AB8", "#879EAF");

    public static string Normalize(string? theme)
    {
        var value = (theme ?? "").Trim().ToLowerInvariant();
        if (value == "auto" || Names.Contains(value)) return value;
        return value switch
        {
            "light" => "platinum-blue",
            "orange" => "champagne-minimal",
            "obsidian" or "uhnohh" => "obsidian-gold",
            "nebula" => "royal-amethyst",
            "lilac" or "dracula" => "rose-titanium",
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
        "royal-amethyst" => RoyalAmethyst,
        "emerald-executive" => EmeraldExecutive,
        "champagne-minimal" => ChampagneMinimal,
        "graphite-copper" => GraphiteCopper,
        "ivory-luxe" => IvoryLuxe,
        "rose-titanium" => RoseTitanium,
        "arctic-glass" => ArcticGlass,
        _ => SapphireNoir
    };

    public static (byte R, byte G, byte B) Parse(string hex)
    {
        var value = hex.TrimStart('#');
        return (
            byte.Parse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
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
