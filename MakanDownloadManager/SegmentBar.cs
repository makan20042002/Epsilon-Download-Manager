using System.Windows;
using System.Windows.Media;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>
/// The row's segment map: which parts of the file are on disk, drawn as a strip of small blocks (the design's signature
/// visual). It asks the engine for the real position map, so several connections show up as several growing runs.
/// </summary>
public sealed class SegmentBar : FrameworkElement
{
    const int Buckets = 48;

    static DependencyProperty Reg<T>(string name) => DependencyProperty.Register(name, typeof(T), typeof(SegmentBar), new FrameworkPropertyMetadata(default(T), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ItemProperty = Reg<DownloadItem>(nameof(Item));
    public static readonly DependencyProperty ProgressProperty = Reg<double>(nameof(Progress));
    public static readonly DependencyProperty StatusProperty = Reg<string>(nameof(Status));
    public static readonly DependencyProperty FillProperty = Reg<Brush>(nameof(Fill));
    public static readonly DependencyProperty TrackProperty = Reg<Brush>(nameof(Track));
    public static readonly DependencyProperty DoneFillProperty = Reg<Brush>(nameof(DoneFill));
    public static readonly DependencyProperty TorrentFillProperty = Reg<Brush>(nameof(TorrentFill));

    public DownloadItem? Item { get => (DownloadItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    /// <summary>Bound to the item's progress only so the strip redraws whenever it changes.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public string? Status { get => (string?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush? DoneFill { get => (Brush?)GetValue(DoneFillProperty); set => SetValue(DoneFillProperty, value); }
    public Brush? TorrentFill { get => (Brush?)GetValue(TorrentFillProperty); set => SetValue(TorrentFillProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 7 : Height);

    static readonly Brush Gloss = MakeGloss();
    static Brush MakeGloss()
    {
        var g = new LinearGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0x00, 255, 255, 255), 90);
        g.Freeze();
        return g;
    }

    /// <summary>A smooth rounded glass bar: soft track, a filled capsule with a light gloss on top (the percentage is drawn over it by the row).</summary>
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h <= 0) return;
        var complete = Status == nameof(DownloadStatus.Complete);
        var item = Item;
        var torrent = item != null && DownloadManager.IsTorrentUrl(item.Url);
        var fill = complete ? DoneFill ?? Fill : torrent ? TorrentFill ?? Fill : Fill;
        var radius = h / 2;
        if (Track != null) dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        var part = complete ? 1 : Math.Clamp(Progress / 100.0, 0, 1);
        if (part > 0 && fill != null)
        {
            var fw = Math.Max(h, w * part);
            var rect = new Rect(0, 0, Math.Min(w, fw), h);
            dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            dc.DrawRoundedRectangle(Gloss, null, new Rect(rect.X, rect.Y, rect.Width, h / 2), radius, radius);
        }
    }
}

/// <summary>A small rounded progress bar with a soft gloss, for tables built in code (the torrent file list).</summary>
public sealed class ProgressPill : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressPill), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    static readonly Brush Gloss = MakeGloss();
    static Brush MakeGloss() { var g = new LinearGradientBrush(Color.FromArgb(0x55, 255, 255, 255), Color.FromArgb(0x00, 255, 255, 255), 90); g.Freeze(); return g; }
    protected override Size MeasureOverride(Size availableSize) => new(double.IsInfinity(availableSize.Width) ? 60 : availableSize.Width, double.IsNaN(Height) ? 14 : Height);
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h <= 0) return;
        var radius = h / 2;
        var track = TryFindResource("BarTrack") as Brush; var fill = TryFindResource("Accent") as Brush;
        if (track != null) dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), radius, radius);
        var part = Math.Clamp(Value / 100.0, 0, 1);
        if (part > 0 && fill != null)
        {
            var rect = new Rect(0, 0, Math.Min(w, Math.Max(h, w * part)), h);
            dc.DrawRoundedRectangle(fill, null, rect, radius, radius);
            dc.DrawRoundedRectangle(Gloss, null, new Rect(rect.X, rect.Y, rect.Width, h / 2), radius, radius);
        }
    }
}


/// <summary>White text once a bar is more than half full, normal text colour before that.</summary>
public sealed class ProgressTextBrush : System.Windows.Data.IValueConverter
{
    public static readonly ProgressTextBrush Instance = new();
    public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var p = value is double d ? d : 0;
        return p >= 45 ? System.Windows.Media.Brushes.White : (System.Windows.Application.Current.TryFindResource("Text") ?? System.Windows.Media.Brushes.Black);
    }
    public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new System.NotSupportedException();
}
