using System.Globalization;
using MakanDownloadManager.Services;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MakanDownloadManager;

/// <summary>
/// Stops a list column from being dragged wider than its own content: once the longest text is fully visible (plus a little
/// room), pulling the edge further does nothing. Without this a column can be stretched on and on.
/// </summary>
public static class GridColumnLimit
{
    /// <summary>Extra rules for columns drawn by a template: which property holds the text, how much room the cell needs besides the text and its font size.</summary>
    public sealed record Rule(string Property, double Extra = 14, double FontSize = 0);

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GridViewColumn, Rule> Rules = new();

    /// <summary>Tells the limiter which property a templated column shows, so it can measure the longest value.</summary>
    public static void SetRule(GridViewColumn column, string property, double extra = 14, double fontSize = 0)
    {
        Rules.Remove(column);
        Rules.Add(column, new Rule(property, extra, fontSize));
    }

    /// <summary>Limits the column that is being dragged, so a list with thousands of rows stays fast.</summary>
    public static void Attach(ListView list)
    {
        void OnDrag(object sender, RoutedEventArgs e)
        {
            DependencyObject? d = e.OriginalSource as DependencyObject;
            while (d != null && d is not GridViewColumnHeader) d = VisualTreeHelper.GetParent(d);
            if (d is GridViewColumnHeader { Column: { } column } && list.View is GridView) Limit(list, column);
        }
        list.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnDrag), true);
        list.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDrag), true);
    }

    static void Limit(ListView list, GridViewColumn column)
    {
        var width = double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
        var limited = ColumnLimit.Clamp(width, ContentWidth(list, column));
        if (Math.Abs(limited - width) > 0.5) column.Width = limited;
    }

    static double ContentWidth(ListView list, GridViewColumn column)
    {
        var dpi = VisualTreeHelper.GetDpi(list).PixelsPerDip;
        var typeface = new Typeface(list.FontFamily, list.FontStyle, FontWeights.SemiBold, list.FontStretch);
        double Measure(string? text, double size) => string.IsNullOrEmpty(text) ? 0 : new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;

        var widest = Measure(column.Header as string, list.FontSize);
        string? name = null; double extra = 0, size = list.FontSize;
        if (column.DisplayMemberBinding is Binding { Path: { } path } && !string.IsNullOrEmpty(path.Path)) name = path.Path;
        else if (Rules.TryGetValue(column, out var rule)) { name = rule.Property; extra = rule.Extra; if (rule.FontSize > 0) size = rule.FontSize; }
        if (name == null) return Math.Max(widest, 240);   // a templated column with no text to measure (bars, buttons): a sensible fixed ceiling

        PropertyInfo? property = null;
        var seen = new HashSet<string>();
        foreach (var item in list.Items)
        {
            if (item == null) continue;
            property ??= item.GetType().GetProperty(name);
            var text = property?.GetValue(item)?.ToString();
            if (text != null && seen.Add(text)) widest = Math.Max(widest, Measure(text, size) + extra);
        }
        return widest;
    }
}
