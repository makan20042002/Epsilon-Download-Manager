using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>
/// Puts every window into the current language: the English texts written in the XAML are looked up in the Persian list and replaced,
/// and Persian windows are mirrored (right-to-left). Runs once per window when it is loaded.
/// </summary>
public static class LocUi
{
    static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window)) { ThemeManager.ApplyToWindow(window); Apply(window); }
    }

    public static void Apply(Window window)
    {
        if (!Loc.IsPersian) return;
        window.FlowDirection = FlowDirection.RightToLeft;
        Translate(window);
    }

    static string? Changed(string? original)
    {
        if (string.IsNullOrEmpty(original)) return null;
        var translated = Loc.T(original);
        return translated == original ? null : translated;
    }

    static void Translate(DependencyObject d)
    {
        if (d is Window w && Changed(w.Title) is { } title) w.Title = title;
        if (d is TextBlock tb && tb.Inlines.Count <= 1 && !System.Windows.Data.BindingOperations.IsDataBound(tb, TextBlock.TextProperty) && Changed(tb.Text) is { } text) tb.Text = text;
        if (d is MenuItem mi && mi.Header is string mh && Changed(mh) is { } mht) mi.Header = mht;
        if (d is HeaderedContentControl hc && hc.Header is string hh && Changed(hh) is { } hht) hc.Header = hht;
        if (d is ContentControl cc && cc.Content is string content && Changed(content) is { } ct) cc.Content = ct;
        if (d is TextBox box && box.Name != "SearchBox" && box.Name != "NameBox") box.FlowDirection = FlowDirection.LeftToRight;   // addresses, paths and numbers stay left-to-right
        if (d is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && Changed(tip) is { } tipText) fe.ToolTip = tipText;
            if (fe.ContextMenu != null) Translate(fe.ContextMenu);
        }
        if (d is ListView lv && lv.View is GridView gv)
            foreach (var column in gv.Columns) if (column.Header is string ch && Changed(ch) is { } cht) column.Header = cht;
        if (d is DataGrid dg)
            foreach (var column in dg.Columns) if (column.Header is string dh && Changed(dh) is { } dht) column.Header = dht;
        foreach (var child in LogicalTreeHelper.GetChildren(d)) if (child is DependencyObject next) Translate(next);
    }
}
