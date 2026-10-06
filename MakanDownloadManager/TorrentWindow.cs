using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;

namespace MakanDownloadManager;

/// <summary>
/// The details window for one torrent (magnet link or .torrent file): overall status and speed, per-file priority,
/// the peers we are exchanging data with, and the trackers we announce to - the BitTorrent equivalent of the
/// ordinary download's progress window. Built in code, like the Intelligent Center, so it stays theme-aware without
/// duplicating a XAML file for something this window-shaped.
/// </summary>
public sealed class TorrentWindow : Window
{
    static readonly Dictionary<long, TorrentWindow> Open = new();

    readonly DownloadItem _item;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _state = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    readonly ProgressBar _bar = new() { Height = 8, Margin = new Thickness(0, 8, 0, 8) };
    readonly TextBlock _down = Stat(), _up = Stat(), _peers = Stat(), _ratio = Stat(), _size = Stat(), _eta = Stat();
    readonly ListView _files = new();
    readonly ListView _peerList = new();
    readonly ListView _trackerList = new();
    readonly TextBox _downLimitBox = new() { Width = 70 };
    readonly TextBox _upLimitBox = new() { Width = 70 };
    readonly Button _pauseButton = new() { Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
    bool _loading = true;
    bool _priorityDropDownOpen;

    public static void ShowFor(DownloadItem item, Window? owner = null)
    {
        try
        {
            if (Open.TryGetValue(item.Id, out var existing)) { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            var window = new TorrentWindow(item) { Owner = owner };
            Open[item.Id] = window;
            window.Show();
        }
        catch (Exception ex) { new DiagnosticsService().Error("The torrent details window could not be shown", ex); }
    }

    TorrentWindow(DownloadItem item)
    {
        _item = item;
        Title = Loc.T("Torrent details");
        Width = 860; Height = 620; MinWidth = 640; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        Content = Build();

        _downLimitBox.Text = item.SpeedLimitBytesPerSec > 0 ? Math.Max(1, item.SpeedLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture) : "";
        _upLimitBox.Text = item.UploadLimitBytesPerSec > 0 ? Math.Max(1, item.UploadLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture) : "";
        _downLimitBox.TextChanged += (_, _) => { if (!_loading) ApplyLimit(_downLimitBox, v => _item.SpeedLimitBytesPerSec = v); };
        _upLimitBox.TextChanged += (_, _) => { if (!_loading) ApplyLimit(_upLimitBox, v => _item.UploadLimitBytesPerSec = v); };
        _loading = false;

        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Open.Remove(item.Id); };
    }

    static void ApplyLimit(TextBox box, Action<long> apply) => apply(long.TryParse(box.Text, out var kbps) && kbps > 0 ? kbps * 1024 : 0);

    // ---------------------------------------------------------------- layout

    static TextBlock Stat() => new() { FontSize = 12, Margin = new Thickness(0, 0, 18, 0) };

    static TextBlock Label(string text, bool bold = false, double size = 12, string brush = "Text") =>
        new TextBlock { Text = Loc.T(text), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = size }.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, brush));

    UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        // ---- header: name, progress, live numbers, and the pause/open/magnet buttons
        var header = new StackPanel { Margin = new Thickness(16) };
        header.Children.Add(_title);
        header.Children.Add(_state.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));
        header.Children.Add(_bar);
        var stats = new WrapPanel();
        foreach (var t in new[] { _down, _up, _peers, _ratio, _size, _eta }) stats.Children.Add(t);
        header.Children.Add(stats);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _pauseButton.Click += PauseResume_Click;
        buttons.Children.Add(_pauseButton);
        buttons.Children.Add(Btn("Open folder", OpenFolder_Click));
        buttons.Children.Add(Btn("Copy magnet link", CopyMagnet_Click));
        buttons.Children.Add(Btn("Remove…", Remove_Click));
        header.Children.Add(buttons);
        var headerCard = new Border { Child = header };
        if (TryFindResource("CardBorder") is Style cardStyle) headerCard.Style = cardStyle;
        DockPanel.SetDock(headerCard, Dock.Top);
        root.Children.Add(headerCard);

        // ---- footer: per-torrent speed limits (0/blank = follow the global limiter)
        var footer = new Border { Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0) };
        var limits = new StackPanel { Orientation = Orientation.Horizontal };
        limits.Children.Add(Label("Download limit:") .Tap(t => t.VerticalAlignment = VerticalAlignment.Center));
        limits.Children.Add(_downLimitBox.Tap(b => b.Margin = new Thickness(6, 0, 4, 0)));
        limits.Children.Add(Label("KB/s").Tap(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 24, 0); }));
        limits.Children.Add(Label("Upload limit:").Tap(t => t.VerticalAlignment = VerticalAlignment.Center));
        limits.Children.Add(_upLimitBox.Tap(b => b.Margin = new Thickness(6, 0, 4, 0)));
        limits.Children.Add(Label("KB/s").Tap(t => t.VerticalAlignment = VerticalAlignment.Center));
        footer.Child = limits;
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ---- tabs: files (with priority), peers, trackers
        var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
        tabs.Items.Add(new TabItem { Header = Loc.T("Files"), Content = BuildFilesTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Peers"), Content = BuildPeersTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Trackers"), Content = BuildTrackersTab() });
        root.Children.Add(tabs);
        return root;
    }

    static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = Loc.T(text), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
    }

    UIElement BuildFilesTab()
    {
        _files.View = Grid(
            Col("Path", "Path", 320), Col("Size", "SizeText", 90), Col("Done", "DoneText", 90), Col("Progress", "ProgressText", 70));
        var priorityColumn = new GridViewColumn { Header = Loc.T("Priority"), Width = 130 };
        var template = new DataTemplate();
        var factory = new System.Windows.FrameworkElementFactory(typeof(ComboBox));
        factory.SetValue(ComboBox.ItemsSourceProperty, new[] { Loc.T("Skip"), Loc.T("Normal"), Loc.T("High") });
        factory.SetBinding(Selector.SelectedIndexProperty, new System.Windows.Data.Binding("PriorityIndex") { Mode = System.Windows.Data.BindingMode.OneWay });
        factory.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(FilePriority_Changed));
        // DropDownOpened/DropDownClosed are plain CLR events on ComboBox, not RoutedEvents, so they cannot be hooked via
        // FrameworkElementFactory.AddHandler directly; Loaded is a RoutedEvent, so hook that instead and attach the two
        // CLR events on the real instance once it exists.
        factory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not ComboBox combo) return;
            combo.DropDownOpened += (_, _) => _priorityDropDownOpen = true;
            combo.DropDownClosed += (_, _) => _priorityDropDownOpen = false;
        }));
        template.VisualTree = factory;
        priorityColumn.CellTemplate = template;
        ((GridView)_files.View).Columns.Add(priorityColumn);
        return _files;
    }

    void FilePriority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: FileRow row } combo || combo.SelectedIndex < 0) return;
        App.Manager.SessionOf(_item)?.SetFilePriority(row.Index, (FilePriority)combo.SelectedIndex);
    }

    UIElement BuildPeersTab() => _peerList.Tap(l => l.View = Grid(
        Col("Address", "Address", 160), Col("Client", "Client", 150), Col("Flags", "Flags", 60),
        Col("Progress", "ProgressText", 70), Col("Down", "DownText", 90), Col("Up", "UpText", 90)));

    UIElement BuildTrackersTab() => _trackerList.Tap(l => l.View = Grid(
        Col("Tracker", "Url", 380), Col("Status", "Status", 220), Col("Seeders", "SeedersText", 70), Col("Leechers", "LeechersText", 70)));

    static GridView Grid(params GridViewColumn[] columns)
    {
        var view = new GridView();
        foreach (var c in columns) view.Columns.Add(c);
        return view;
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = Loc.T(header), Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

    // ---------------------------------------------------------------- rows shown in the lists (plain snapshots, not live objects)

    sealed record FileRow(int Index, string Path, string SizeText, string DoneText, string ProgressText, int PriorityIndex);
    sealed record PeerRow(string Address, string Client, string Flags, string ProgressText, string DownText, string UpText);
    sealed record TrackerRow(string Url, string Status, string SeedersText, string LeechersText);

    // ---------------------------------------------------------------- refresh

    void Refresh()
    {
        var item = _item;
        _title.Text = item.FileName;
        var session = App.Manager.SessionOf(item);

        var running = item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued);
        _pauseButton.Content = Loc.T(running ? "Pause" : "Resume");
        _pauseButton.IsEnabled = item.Status != nameof(DownloadStatus.Cancelled);

        if (session == null)
        {
            _state.Text = Loc.T(item.Status == nameof(DownloadStatus.Paused) ? "Paused - not connected" : item.Status);
            return;
        }

        _bar.Value = Math.Clamp(session.Progress, 0, 100);
        _state.Text = StateText(session);
        _down.Text = Loc.T("Down") + ": " + Speed(session.DownloadRate);
        _up.Text = Loc.T("Up") + ": " + Speed(session.UploadRate);
        _peers.Text = Loc.T("Peers") + $": {session.PeerCount} ({session.SeedCount} " + Loc.T("seeds") + $", {session.KnownPeers} " + Loc.T("known") + ")";
        _ratio.Text = Loc.T("Ratio") + $": {session.Ratio:0.00}";
        _size.Text = Loc.T("Size") + ": " + DownloadItem.FormatBytes(session.WantedBytes) + (session.WantedBytes != session.TotalSize ? $" / {DownloadItem.FormatBytes(session.TotalSize)}" : "");
        _eta.Text = item.EtaText is { Length: > 0 } eta && eta != "—" ? Loc.T("ETA") + ": " + eta : "";

        // Left alone while the priority dropdown is open: replacing ItemsSource mid-click would tear the open ComboBox down
        // before the person can pick anything (the row containers, dropdown included, get rebuilt from scratch otherwise).
        if (!_priorityDropDownOpen)
            _files.ItemsSource = session.Files().Select(f => new FileRow(f.Index, f.Path, DownloadItem.FormatBytes(f.Length), DownloadItem.FormatBytes(f.Done), (f.Length > 0 ? 100.0 * f.Done / f.Length : 100).ToString("0.0") + "%", (int)f.Priority)).ToList();
        _peerList.ItemsSource = session.Peers().Select(p => new PeerRow(p.Address, p.Client, p.Flags, p.Progress.ToString("0.0") + "%", Speed(p.DownRate), Speed(p.UpRate))).ToList();
        _trackerList.ItemsSource = session.Trackers().Select(t => new TrackerRow(t.Url, Loc.T(t.Status), t.Seeders?.ToString() ?? "—", t.Leechers?.ToString() ?? "—")).ToList();
    }

    static string StateText(TorrentSession s) => s.State switch
    {
        TorrentState.Metadata => Loc.T("Getting torrent info…"),
        TorrentState.Checking => Loc.T("Checking files") + $" {s.CheckProgress:0}%",
        TorrentState.Downloading => Loc.T("Downloading"),
        TorrentState.Seeding => Loc.T("Seeding"),
        TorrentState.Finished => s.UploadedTotal > 0 ? Loc.T("Finished") : Loc.T("Finished - not sharing"),
        TorrentState.Paused => Loc.T("Paused"),
        TorrentState.Error => Loc.T("Error") + ": " + (s.Error ?? ""),
        _ => Loc.T("Stopped")
    };

    static string Speed(long bytesPerSecond) => DownloadItem.FormatBytes(bytesPerSecond) + "/s";

    // ---------------------------------------------------------------- buttons

    void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued)) App.Manager.Pause(_item);
        else App.Manager.Enqueue(_item);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = App.Manager.SessionOf(_item)?.ContentPath is { Length: > 0 } p ? p : _item.FilePath;
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is { Length: > 0 } && Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { new DiagnosticsService().Error("Could not open the torrent's folder", ex); }
    }

    void CopyMagnet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var magnet = App.Manager.SessionOf(_item)?.MagnetUri ?? (MagnetLink.IsMagnet(_item.Url) ? _item.Url : null);
            if (magnet != null) Clipboard.SetText(magnet);
        }
        catch (Exception) { /* clipboard busy */ }
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = Dlg.Show(this, "Also delete the downloaded files from disk?", "Remove torrent", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;
        _ = App.Manager.RemoveAsync(_item, answer == MessageBoxResult.Yes);
        Close();
    }
}

static class TapExtensions
{
    public static T Tap<T>(this T value, Action<T> configure) { configure(value); return value; }
}
