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

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < Buckets * 2 || h <= 0) return;
        var item = Item;
        double[] map;
        try { map = item != null && App.Manager != null ? App.Manager.GetPositionMap(item, Buckets) : new double[Buckets]; }
        catch (Exception) { map = new double[Buckets]; }
        var complete = Status == nameof(DownloadStatus.Complete);
        var torrent = item != null && DownloadManager.IsTorrentUrl(item.Url);
        var fill = complete ? DoneFill ?? Fill : torrent ? TorrentFill ?? Fill : Fill;
        const double gap = 2;
        var cell = (w - gap * (Buckets - 1)) / Buckets;
        for (var i = 0; i < Buckets; i++)
        {
            var x = Math.Round(i * (cell + gap));
            var width = Math.Max(1, Math.Round((i + 1) * (cell + gap) - gap) - x);
            if (Track != null) dc.DrawRoundedRectangle(Track, null, new Rect(x, 0, width, h), 2, 2);
            var part = complete ? 1 : i < map.Length ? Math.Clamp(map[i], 0, 1) : 0;
            if (part > 0.02 && fill != null) dc.DrawRoundedRectangle(fill, null, new Rect(x, 0, Math.Max(2, width * part), h), 2, 2);
        }
        if (item != null)
        {
            IReadOnlyList<(NetworkLink Link, long Bytes, bool Active)> raw;
            try { raw = App.Manager?.GetLinkUsage(item) ?? Array.Empty<(NetworkLink, long, bool)>(); } catch (Exception) { raw = Array.Empty<(NetworkLink, long, bool)>(); }
            var usage = raw.Where(x => x.Bytes > 0).ToList();
            var total = usage.Sum(x => (double)x.Bytes);
            if (total > 0)
            {
                double x = 0;
                foreach (var entry in usage)
                {
                    var width = w * entry.Bytes / total;
                    try { dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString(entry.Link.Color)!, null, new Rect(x, Math.Max(0, h - 2), width, 2)); } catch (FormatException) { }
                    x += width;
                }
            }
        }
    }
}
