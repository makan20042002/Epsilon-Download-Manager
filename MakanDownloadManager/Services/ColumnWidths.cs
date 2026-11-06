using System.Globalization;

namespace MakanDownloadManager.Services;

/// <summary>Saved widths of the download table's columns ("250,72,88,..."): reading, writing and safe defaults.</summary>
public static class ColumnWidths
{
    public const double Min = 56, Max = 520;

    /// <summary>Name, Type, Size, Progress, Speed, Connections, Network, Status, ETA, Queue, Downloaded, Remaining, Date added, Finished, Average.</summary>
    public static readonly double[] Defaults = { 250, 72, 88, 126, 98, 104, 84, 112, 84, 92, 116, 110, 126, 126, 112 };

    public static string DefaultText => Serialize(Defaults);

    public static string Serialize(IEnumerable<double> widths)
        => string.Join(",", widths.Select(w => Math.Round(Math.Clamp(double.IsNaN(w) ? Min : w, Min, Max)).ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Reads saved text. Text from an older version (a different number of columns) or damaged text gives the defaults;
    /// one unreadable value falls back to that column's default; values are kept between <see cref="Min"/> and <see cref="Max"/>.
    /// </summary>
    public static double[] Parse(string? text, double[]? defaults = null)
    {
        defaults ??= Defaults;
        var parts = (text ?? "").Split(',');
        if (parts.Length != defaults.Length) return (double[])defaults.Clone();
        return defaults.Select((fallback, i) =>
            double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v)
                ? Math.Clamp(v, Min, Max) : fallback).ToArray();
    }
}
