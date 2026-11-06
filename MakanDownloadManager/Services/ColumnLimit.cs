namespace MakanDownloadManager.Services;

/// <summary>The width rules for list columns (kept free of WPF so they can be tested).</summary>
public static class ColumnLimit
{
    public const double MinColumnWidth = 48;
    public const double Spare = 36;

    /// <summary>The widest a column may be: its longest content plus a little room, never below the minimum.</summary>
    public static double MaxWidth(double contentWidth) => Math.Max(MinColumnWidth, contentWidth + Spare);

    /// <summary>The width a column ends up with after the person dragged it to <paramref name="wanted"/>.</summary>
    public static double Clamp(double wanted, double contentWidth)
    {
        if (double.IsNaN(wanted) || double.IsInfinity(wanted)) return MinColumnWidth;
        return Math.Clamp(wanted, MinColumnWidth, MaxWidth(contentWidth));
    }
}
