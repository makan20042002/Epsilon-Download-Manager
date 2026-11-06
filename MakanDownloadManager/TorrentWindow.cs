using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    readonly TextBlock _percent = new() { FontSize = 30, FontWeight = FontWeights.Bold };
    readonly TextBlock _progressCaption = new() { FontSize = 12 };
    readonly TextBlock _pieceSummary = Stat();
    readonly TextBlock _health = Stat();
    readonly ListView _files = new();
    readonly ListView _peerList = new();
    readonly ListView _trackerList = new();
    readonly ListView _logList = new();
    readonly WrapPanel _pieceMap = new() { Margin = new Thickness(4) };
    readonly Canvas _speedChart = new() { Height = 130, MinWidth = 260 };
    readonly TextBox _downLimitBox = new() { Width = 70 };
    readonly TextBox _upLimitBox = new() { Width = 70 };
    readonly CheckBox _sequential = new() { Content = Loc.T("Sequential download (to watch while it downloads)"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc.T("Downloads the start of the file first, then in order, so a video can be played early. A little slower overall.") };
    readonly Button _pauseButton = new() { Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
    bool _loading = true;
    bool _priorityDropDownOpen;
    readonly Queue<(long Down, long Up)> _speedHistory = new();
    readonly List<LogRow> _logs = new();
    string _lastState = "";
    int _lastPieceMapHash = -1;

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
        Width = 1060; Height = 720; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;
        foreach (var number in new[] { _percent, _progressCaption, _down, _up, _peers, _health, _ratio, _size, _eta, _pieceSummary })
            number.FlowDirection = FlowDirection.LeftToRight;

        Content = Build();

        _downLimitBox.Text = item.SpeedLimitBytesPerSec > 0 ? Math.Max(1, item.SpeedLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture) : "";
        _upLimitBox.Text = item.UploadLimitBytesPerSec > 0 ? Math.Max(1, item.UploadLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture) : "";
        _downLimitBox.TextChanged += (_, _) => { if (!_loading) ApplyLimit(_downLimitBox, v => _item.SpeedLimitBytesPerSec = v); };
        _upLimitBox.TextChanged += (_, _) => { if (!_loading) ApplyLimit(_upLimitBox, v => _item.UploadLimitBytesPerSec = v); };
        _loading = false;

        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => { SaveColumnWidths(); _timer.Stop(); Open.Remove(item.Id); };
    }

    static void ApplyLimit(TextBox box, Action<long> apply) => apply(long.TryParse(box.Text, out var kbps) && kbps > 0 ? kbps * 1024 : 0);

    // ---------------------------------------------------------------- layout

    static TextBlock Stat() => new() { FontSize = 12, Margin = new Thickness(0, 0, 18, 0) };

    static TextBlock Label(string text, bool bold = false, double size = 12, string brush = "Text") =>
        new TextBlock { Text = Loc.T(text), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = size }.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, brush));

    UIElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        // V2 header: real progress and live engine values, arranged to remain useful at the minimum window width.
        var header = new Grid { Margin = new Thickness(18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 250 });
        var summary = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        summary.Children.Add(_title);
        summary.Children.Add(_state.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));
        var progressLine = new Grid { Margin = new Thickness(0, 12, 0, 2) };
        progressLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressLine.ColumnDefinitions.Add(new ColumnDefinition());
        progressLine.Children.Add(_percent);
        _progressCaption.HorizontalAlignment = HorizontalAlignment.Right;
        _progressCaption.VerticalAlignment = VerticalAlignment.Bottom;
        _progressCaption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(_progressCaption, 1);
        progressLine.Children.Add(_progressCaption);
        summary.Children.Add(progressLine);
        _bar.Height = 10;
        summary.Children.Add(_bar);
        var stats = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var t in new[] { _size, _eta, _ratio }) stats.Children.Add(t);
        summary.Children.Add(stats);
        header.Children.Add(summary);

        var cards = new UniformGrid { Columns = 2, Rows = 2 };
        cards.Children.Add(StatCard("Download", _down, "\uE896", "AccentDark"));
        cards.Children.Add(StatCard("Upload", _up, "\uE898", "IconPurple"));
        cards.Children.Add(StatCard("Connections", _peers, "\uE701", "Success"));
        cards.Children.Add(StatCard("Health", _health, "\uE9D2", "IconAmber"));
        Grid.SetColumn(cards, 1);
        header.Children.Add(cards);

        var headerHost = new StackPanel();
        headerHost.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 14) };
        _pauseButton.Click += PauseResume_Click;
        if (TryFindResource("PrimaryButton") is Style primary) _pauseButton.Style = primary;
        buttons.Children.Add(_pauseButton);
        buttons.Children.Add(Btn("Open folder", OpenFolder_Click));
        buttons.Children.Add(Btn("Copy magnet link", CopyMagnet_Click));
        buttons.Children.Add(Btn("Remove…", Remove_Click));
        _sequential.Click += (_, _) => { if (App.Manager.SessionOf(_item) is { } s) s.Sequential = _sequential.IsChecked == true; };
        buttons.Children.Add(_sequential);
        headerHost.Children.Add(buttons);
        var headerCard = new Border { Child = headerHost };
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

        // ---- tabs: a complete torrent workspace rather than the old three basic lists.
        var tabs = new TabControl { Margin = new Thickness(0, 12, 0, 0) };
        tabs.Items.Add(new TabItem { Header = Loc.T("Overview"), Content = BuildOverviewTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Files"), Content = BuildFilesTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Peers"), Content = BuildPeersTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Trackers"), Content = BuildTrackersTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Pieces"), Content = BuildPiecesTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Logs"), Content = BuildLogsTab() });
        root.Children.Add(tabs);
        return root;
    }

    Border StatCard(string title, TextBlock value, string glyph, string colour)
    {
        value.FontSize = 14;
        value.FontWeight = FontWeights.SemiBold;
        value.Margin = new Thickness(0, 3, 0, 0);
        var icon = new TextBlock { Text = glyph, FontSize = 18, FontFamily = new FontFamily("Segoe MDL2 Assets"), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, colour);
        var copy = new TextBlock { Text = Loc.T(title), FontSize = 10 };
        copy.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(copy); text.Children.Add(value);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(11) };
        row.Children.Add(icon); row.Children.Add(text);
        var card = new Border { Child = row, Margin = new Thickness(4) };
        if (TryFindResource("CardBorder") is Style style) card.Style = style;
        return card;
    }

    UIElement BuildOverviewTab()
    {
        var grid = new Grid { Margin = new Thickness(4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });

        var left = new StackPanel { Margin = new Thickness(8) };
        left.Children.Add(Label("Torrent health", true, 15));
        _pieceSummary.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_pieceSummary);
        left.Children.Add(Label("Verified pieces", true, 12).Tap(t => t.Margin = new Thickness(0, 16, 0, 7)));
        var compactPieces = new WrapPanel { MaxHeight = 95 };
        compactPieces.SetBinding(FrameworkElement.TagProperty, new Binding());
        // The same live piece map is shown in the dedicated Pieces tab; Overview keeps the explanatory statistics.
        left.Children.Add(Label("Open the Pieces tab for the live verification map.").Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));

        var right = new StackPanel { Margin = new Thickness(8) };
        right.Children.Add(Label("Transfer speed", true, 15));
        var chartBorder = new Border { Child = _speedChart, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0) };
        if (TryFindResource("CardBorder") is Style style) chartBorder.Style = style;
        right.Children.Add(chartBorder);
        right.Children.Add(Label("Blue: download   Purple: upload").Tap(t => { t.Margin = new Thickness(0, 6, 0, 0); t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }));

        grid.Children.Add(left); Grid.SetColumn(right, 1); grid.Children.Add(right);
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    UIElement BuildPiecesTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(Label("Every tile represents a range of verified torrent pieces. Green is complete; blue is partially represented; dark tiles are still missing.", false, 12)
            .Tap(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 0, 0, 8); t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }));
        panel.Children.Add(_pieceMap);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    UIElement BuildLogsTab()
    {
        _logList.View = GridViewOf(Col("Time", "Time", 100), Col("Event", "Event", 650));
        RestoreWidths("logs", (GridView)_logList.View);
        return _logList;
    }

    static Button Btn(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = Loc.T(text), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
    }

    UIElement BuildFilesTab()
    {
        _files.View = GridViewOf(
            Col("#", "Number", 42));
        ((GridView)_files.View).Columns.Add(new GridViewColumn { Header = Loc.T("Type"), Width = 92, CellTemplate = FileTypeTemplate() });
        foreach (var column in new[] { Col("Name", "Path", 315), Col("Size", "SizeText", 100), Col("Downloaded", "DoneText", 105),
                     Col("Remaining", "RemainingText", 105), Col("Status", "Status", 105) })
            ((GridView)_files.View).Columns.Add(column);
        var progressColumn = new GridViewColumn { Header = Loc.T("Progress"), Width = 150, CellTemplate = ProgressTemplate() };
        ((GridView)_files.View).Columns.Add(progressColumn);
        var priorityColumn = new GridViewColumn { Header = Loc.T("Priority"), Width = 130 };
        var template = new DataTemplate();
        var factory = new System.Windows.FrameworkElementFactory(typeof(ComboBox));
        factory.SetValue(ComboBox.ItemsSourceProperty, new[] { Loc.T("Skip"), Loc.T("Normal"), Loc.T("High") });
        factory.SetValue(Control.PaddingProperty, new Thickness(12, 3, 30, 3));      // room for the drop-down arrow, so it never sits on the text
        factory.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        factory.SetValue(FrameworkElement.MinWidthProperty, 104.0);
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
        RestoreWidths("files", (GridView)_files.View);
        _files.MouseDoubleClick += Files_DoubleClick;
        _files.KeyDown += (_, e) => { if (e.Key == Key.Enter) { PreviewSelectedFile(); e.Handled = true; } };
        return _files;
    }

    static DataTemplate ProgressTemplate()
    {
        var template = new DataTemplate();
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        var bar = new FrameworkElementFactory(typeof(ProgressPill));
        bar.SetValue(FrameworkElement.HeightProperty, 16.0);
        bar.SetValue(FrameworkElement.WidthProperty, 130.0);
        bar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        bar.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bar.SetBinding(ProgressPill.ValueProperty, new Binding("Progress"));
        grid.AppendChild(bar);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.WidthProperty, 130.0);
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.FontSizeProperty, 10.0);
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetBinding(TextBlock.TextProperty, new Binding("ProgressText"));
        grid.AppendChild(text);
        template.VisualTree = grid;
        return template;
    }

    static DataTemplate FileTypeTemplate()
    {
        var template = new DataTemplate();
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 15.0);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 7, 0));
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentDark");
        icon.SetBinding(TextBlock.TextProperty, new Binding("Icon"));
        row.AppendChild(icon);
        var type = new FrameworkElementFactory(typeof(TextBlock));
        type.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        type.SetBinding(TextBlock.TextProperty, new Binding("Type"));
        row.AppendChild(type);
        template.VisualTree = row;
        return template;
    }

    void FilePriority_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: FileRow row } combo || combo.SelectedIndex < 0) return;
        App.Manager.SessionOf(_item)?.SetFilePriority(row.Index, (FilePriority)combo.SelectedIndex);
    }

    void Files_DoubleClick(object sender, MouseButtonEventArgs e) => PreviewSelectedFile();

    void PreviewSelectedFile()
    {
        if (_files.SelectedItem is not FileRow row || App.Manager.SessionOf(_item) is not { } session) return;
        var source = session.FilePath(row.Index);
        if (!DownloadManager.IsStreamablePath(source))
        {
            Dlg.Show(this, "Only audio and video files can be safely previewed while a torrent is incomplete.", "Preview file", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var complete = session.Files().FirstOrDefault(f => f.Index == row.Index) is { } info && info.Done >= info.Length;
        var playable = complete && source != null && File.Exists(source) ? source : App.Manager.PrepareTorrentPlayableCopy(session, row.Index);
        if (playable == null)
        {
            Dlg.Show(this, "The beginning of this media file is not fully verified yet. Epsilon will allow preview after at least 2 MB is available without gaps.", "Preview not ready", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(playable) { UseShellExecute = true }); AddLog("Opened media preview: " + row.Path); }
        catch (Exception ex) { Dlg.Show(this, ex.Message, "Could not open preview", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    UIElement BuildPeersTab() => _peerList.Tap(l =>
    {
        l.View = GridViewOf(Col("Address", "Address", 175), Col("Client", "Client", 170), Col("Flags", "Flags", 70),
            Col("Progress", "ProgressText", 80), Col("Down", "DownText", 100), Col("Up", "UpText", 100), Col("Security", "Security", 105));
        RestoreWidths("peers", (GridView)l.View);
    });

    UIElement BuildTrackersTab() => _trackerList.Tap(l =>
    {
        l.View = GridViewOf(Col("Tier", "TierText", 55), Col("Tracker", "Url", 390), Col("Status", "Status", 220),
            Col("Seeders", "SeedersText", 75), Col("Leechers", "LeechersText", 75), Col("Last announce", "LastText", 130));
        RestoreWidths("trackers", (GridView)l.View);
    });

    static GridView GridViewOf(params GridViewColumn[] columns)
    {
        var view = new GridView();
        foreach (var c in columns) view.Columns.Add(c);
        return view;
    }

    static GridViewColumn Col(string header, string path, double width) =>
        new() { Header = Loc.T(header), Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };

    static void RestoreWidths(string key, GridView view)
    {
        var saved = App.Db.Get("torrent_columns_" + key);
        if (string.IsNullOrWhiteSpace(saved)) return;
        var widths = saved.Split(',');
        for (var i = 0; i < Math.Min(widths.Length, view.Columns.Count); i++)
            if (double.TryParse(widths[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width is >= 35 and <= 1200)
                view.Columns[i].Width = width;
    }

    void SaveColumnWidths()
    {
        Save("files", _files); Save("peers", _peerList); Save("trackers", _trackerList); Save("logs", _logList);
        static void Save(string key, ListView list)
        {
            if (list.View is not GridView view) return;
            App.Db.Set("torrent_columns_" + key, string.Join(',', view.Columns.Select(c => c.ActualWidth.ToString("0.##", CultureInfo.InvariantCulture))));
        }
    }

    // ---------------------------------------------------------------- rows shown in the lists (plain snapshots, not live objects)

    sealed record FileRow(int Index, string Number, string Icon, string Type, string Path, string SizeText, string DoneText, string RemainingText, string Status, double Progress, string ProgressText, int PriorityIndex);
    sealed record PeerRow(string Address, string Client, string Flags, string ProgressText, string DownText, string UpText, string Security);
    sealed record TrackerRow(string TierText, string Url, string Status, string SeedersText, string LeechersText, string LastText);
    sealed record LogRow(string Time, string Event);

    // ---------------------------------------------------------------- refresh

    void Refresh()
    {
        var item = _item;
        _title.Text = item.FileName;
        var session = App.Manager.SessionOf(item);

        var running = item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued);
        _pauseButton.Content = Loc.T(running ? "Pause" : "Resume");
        _pauseButton.IsEnabled = item.Status != nameof(DownloadStatus.Cancelled);
        _sequential.IsEnabled = session != null;
        if (session != null) _sequential.IsChecked = session.Sequential;

        if (session == null)
        {
            _state.Text = Loc.T(item.Status == nameof(DownloadStatus.Paused) ? "Paused - not connected" : item.Status);
            _percent.Text = item.Progress.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            return;
        }

        _bar.Value = Math.Clamp(session.Progress, 0, 100);
        var state = StateText(session);
        _state.Text = state;
        _percent.Text = session.Progress.ToString("0.0", CultureInfo.CurrentCulture) + "%";
        _progressCaption.Text = DownloadItem.FormatBytes(session.BytesDone) + " / " + DownloadItem.FormatBytes(session.WantedBytes);
        _down.Text = Speed(session.DownloadRate);
        _up.Text = Speed(session.UploadRate);
        _peers.Text = $"{session.PeerCount} / {session.KnownPeers}";
        _ratio.Text = Loc.T("Ratio") + $": {session.Ratio:0.00}";
        _size.Text = Loc.T("Size") + ": " + DownloadItem.FormatBytes(session.WantedBytes) + (session.WantedBytes != session.TotalSize ? $" / {DownloadItem.FormatBytes(session.TotalSize)}" : "");
        _eta.Text = session.State == TorrentState.Downloading && item.EtaText is { Length: > 0 } eta && eta != "—" ? Loc.T("ETA") + ": " + eta : "";
        var metrics = session.GetMetricsSnapshot();
        var healthScore = metrics.ConnectedPeers == 0 ? 0 : Math.Clamp(100.0 * metrics.UsefulPeers / metrics.ConnectedPeers - metrics.StalledPeers * 5, 0, 100);
        _health.Text = healthScore.ToString("0") + "%";
        _pieceSummary.Text = $"{session.PiecesHave:N0} / {session.PieceCount:N0} pieces verified   •   " +
            $"{DownloadItem.FormatBytes(session.PieceLength)} per piece   •   {session.TrackerCount} trackers   •   {session.WebSeedCount} web seeds\n" +
            $"{metrics.UsefulPeers} useful peers   •   {metrics.StalledPeers} stalled   •   {metrics.RequestsInFlight} requests in flight   •   " +
            $"disk queue {metrics.DiskQueueDepth}\n" +
            $"Connections tried: {session.ConnectStats.Tried}, reached: {session.ConnectStats.Reached}" + (session.ConnectStats.LastError.Length > 0 ? $"   •   last problem: {session.ConnectStats.LastError}" : "") + "\n" +
            $"Seeds: {session.SeedCount} connected, {session.SwarmSeeders} reported by trackers   •   Peers: {session.PeerCount} connected, {session.SwarmLeechers} leechers reported   •   {session.KnownPeers} addresses known";

        if (!string.Equals(_lastState, state, StringComparison.Ordinal)) { AddLog(state); _lastState = state; }
        _speedHistory.Enqueue((session.DownloadRate, session.UploadRate));
        while (_speedHistory.Count > 90) _speedHistory.Dequeue();
        DrawSpeedChart();
        DrawPieceMap(session);

        // Left alone while the priority dropdown is open: replacing ItemsSource mid-click would tear the open ComboBox down
        // before the person can pick anything (the row containers, dropdown included, get rebuilt from scratch otherwise).
        if (!_priorityDropDownOpen)
            _files.ItemsSource = session.Files().Select(f =>
            {
                var progress = f.Length > 0 ? 100.0 * f.Done / f.Length : 100;
                var extension = System.IO.Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant();
                var type = extension.Length is > 0 and <= 6 ? extension : Loc.T("File");
                var icon = FileIcon(extension);
                var status = f.Priority == FilePriority.Skip ? Loc.T("Skipped") : f.Done >= f.Length ? Loc.T("Complete") : f.Done > 0 ? Loc.T("Downloading") : Loc.T("Waiting");
                return new FileRow(f.Index, (f.Index + 1).ToString(CultureInfo.CurrentCulture), icon, type,
                    f.Path, DownloadItem.FormatBytes(f.Length), DownloadItem.FormatBytes(f.Done), DownloadItem.FormatBytes(Math.Max(0, f.Length - f.Done)), status,
                    progress, progress.ToString("0.0", CultureInfo.CurrentCulture) + "%", (int)f.Priority);
            }).ToList();
        _peerList.ItemsSource = session.Peers().Select(p => new PeerRow(p.Address, p.Client, p.Flags, p.Progress.ToString("0.0") + "%",
            Speed(p.DownRate), Speed(p.UpRate), (p.Encrypted ? Loc.T("Encrypted") : Loc.T("Plain")) + " · " + (p.Incoming ? Loc.T("Incoming") : Loc.T("Outgoing")))).ToList();
        _trackerList.ItemsSource = session.Trackers().Select(t => new TrackerRow((t.Tier + 1).ToString(CultureInfo.CurrentCulture), t.Url, Loc.T(t.Status),
            t.Seeders?.ToString() ?? "—", t.Leechers?.ToString() ?? "—", t.LastAnnounce?.ToLocalTime().ToString("HH:mm:ss") ?? "—")).ToList();
    }

    static string FileIcon(string extension) => extension.ToUpperInvariant() switch
    {
        "MP4" or "MKV" or "WEBM" or "AVI" or "MOV" or "M4V" => "\uE714",
        "MP3" or "M4A" or "FLAC" or "WAV" or "OGG" or "AAC" => "\uE8D6",
        "ZIP" or "RAR" or "7Z" or "TAR" or "GZ" => "\uE7B8",
        "JPG" or "JPEG" or "PNG" or "GIF" or "WEBP" => "\uEB9F",
        _ => "\uE8A5"
    };

    void AddLog(string text)
    {
        if (_logs.Count > 250) _logs.RemoveAt(0);
        _logs.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss"), text));
        _logList.ItemsSource = null;
        _logList.ItemsSource = _logs.ToList();
    }

    void DrawSpeedChart()
    {
        _speedChart.Children.Clear();
        var width = Math.Max(260, _speedChart.ActualWidth > 0 ? _speedChart.ActualWidth : 360);
        var height = Math.Max(90, _speedChart.ActualHeight > 0 ? _speedChart.ActualHeight : 130);
        var data = _speedHistory.ToArray();
        if (data.Length < 2) return;
        var max = Math.Max(1L, data.Max(p => Math.Max(p.Down, p.Up)));
        Polyline Line(Func<(long Down, long Up), long> select, string brush)
        {
            var line = new Polyline { StrokeThickness = 2 };
            line.SetResourceReference(Shape.StrokeProperty, brush);
            for (var i = 0; i < data.Length; i++)
                line.Points.Add(new Point(i * width / Math.Max(1, data.Length - 1), height - 5 - (height - 10) * select(data[i]) / max));
            return line;
        }
        _speedChart.Children.Add(Line(p => p.Down, "AccentDark"));
        _speedChart.Children.Add(Line(p => p.Up, "IconPurple"));
    }

    void DrawPieceMap(TorrentSession session)
    {
        var map = session.PieceMap(192);
        var hash = new HashCode();
        foreach (var value in map) hash.Add((int)(value * 100));
        var code = hash.ToHashCode();
        if (code == _lastPieceMapHash) return;
        _lastPieceMapHash = code;
        _pieceMap.Children.Clear();
        foreach (var value in map)
        {
            var tile = new Border { Width = 22, Height = 13, Margin = new Thickness(2), CornerRadius = new CornerRadius(2), ToolTip = value >= .999 ? Loc.T("Verified") : value > 0 ? Loc.T("Partially represented") : Loc.T("Missing") };
            tile.SetResourceReference(Border.BackgroundProperty, value >= .999 ? "Success" : value > 0 ? "AccentDark" : "Track");
            _pieceMap.Children.Add(tile);
        }
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
            var folder = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
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
