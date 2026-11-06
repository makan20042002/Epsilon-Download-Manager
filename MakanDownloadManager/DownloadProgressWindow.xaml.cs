using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>
/// The download progress window, in the Epsilon look (the same design as the live preview on makanlab.tech/epsilon):
/// status / speed / downloaded / time left, a gradient progress bar, the start-position map, one live bar + speed per
/// connection, and chips that open the Speed Limiter and "Options on completion" settings. One window per download;
/// it closes when the download ends.
/// </summary>
public partial class DownloadProgressWindow : Window
{
    /// <summary>One connection row: a bar (how much of its fair share of the file it has fetched) and its live speed.</summary>
    public sealed class ConnectionView : INotifyPropertyChanged
    {
        string _label = "", _speedText = "", _info = "", _network = "";
        double _percent;
        bool _isDone;

        public int Index { get; init; }
        public string Label { get => _label; set => Set(ref _label, value); }
        public double Percent { get => _percent; set => Set(ref _percent, value); }
        public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }
        public string Info { get => _info; set => Set(ref _info, value); }
        public string Network { get => _network; set => Set(ref _network, value); }
        public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }

        // live speed bookkeeping (not shown directly)
        internal long LastBytes = -1;
        internal double SmoothedSpeed;

        public event PropertyChangedEventHandler? PropertyChanged;
        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>One row of the network breakdown (Overview and Multi-Network tabs).</summary>
    public sealed class NetworkView : INotifyPropertyChanged
    {
        string _name = "", _connectionsText = "", _bytesText = "", _percentText = "";
        double _percent;
        public string Name { get => _name; set => Set(ref _name, value); }
        public string ConnectionsText { get => _connectionsText; set => Set(ref _connectionsText, value); }
        public string BytesText { get => _bytesText; set => Set(ref _bytesText, value); }
        public string PercentText { get => _percentText; set => Set(ref _percentText, value); }
        public double Percent { get => _percent; set => Set(ref _percent, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    const int Buckets = 100;
    static readonly Dictionary<long, DownloadProgressWindow> Open = new();

    readonly DownloadItem _item;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Border[] _cells = new Border[Buckets];
    readonly ObservableCollection<ConnectionView> _connections = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly SpeedHistory _history = new();
    readonly ProgressLog _log = new();
    readonly ObservableCollection<string> _logLines = new();
    readonly ObservableCollection<NetworkView> _networks = new();
    readonly Border[] _segmentCells = new Border[Buckets];
    IReadOnlyList<ConnectionInfo> _lastRows = Array.Empty<ConnectionInfo>();
    string _tab = "Overview";
    int _rangeSeconds = 60;
    double _lastTick;
    bool _loading = true;
    bool? _resume;

    /// <summary>Shows the window for a download (or brings the existing one to the front).</summary>
    public static void ShowFor(DownloadItem item, Window? owner = null)
    {
        try
        {
            if (Open.TryGetValue(item.Id, out var existing)) { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            var window = new DownloadProgressWindow(item);
            if (owner != null) window.Owner = owner;
            Open[item.Id] = window;
            window.Show();
        }
        catch (Exception ex) { new DiagnosticsService().Error("The download progress window could not be shown", ex); }
    }

    /// <summary>Opens the window for a download that has just been started (only when Options > Downloads says so, and not for queue downloads).
    /// A torrent gets its own details window instead - a peer/file list, not a progress bar, is the useful view there.</summary>
    public static void ShowIfWanted(DownloadItem item)
    {
        if (!App.Settings.ShowProgressWindow || item.QueueName != null || item.Status == nameof(DownloadStatus.Complete)) return;
        if (DownloadManager.IsTorrentUrl(item.Url)) TorrentWindow.ShowFor(item);
        else ShowFor(item);
    }

    DownloadProgressWindow(DownloadItem item)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Max(360, workArea.Height - 24);
        MaxWidth = Math.Max(520, workArea.Width - 24);
        MinWidth = Math.Min(700, MaxWidth);
        Width = Math.Min(860, MaxWidth);
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);
        StateChanged += (_, _) => RefreshMaximizeButton();
        _item = item;
        ConnList.ItemsSource = _connections;
        LogList.ItemsSource = _logLines;
        NetList.ItemsSource = _networks;
        NetListFull.ItemsSource = _networks;
        var emptyCell = (Brush)FindResource("Track");
        for (var i = 0; i < Buckets; i++)
        {
            _segmentCells[i] = new Border { Background = emptyCell, Margin = new Thickness(1), MinHeight = 13, CornerRadius = new CornerRadius(2) };
            SegmentGrid.Children.Add(_segmentCells[i]);
        }
        _log.Add(DateTime.Now, "Progress window opened");
        AppendLog(new[] { _log.Lines[^1] });

        var fill = (Brush)FindResource("Accent");
        for (var i = 0; i < Buckets; i++)
        {
            _cells[i] = new Border { Background = fill, Opacity = 0 };
            PositionGrid.Children.Add(_cells[i]);
        }

        // start values from the download itself
        SaveToText.Text = System.IO.Path.GetDirectoryName(item.FilePath) is { Length: > 0 } dir ? dir : item.FilePath;
        SaveToText.ToolTip = SaveToText.Text;
        SaveFooterText.Text = SaveToText.Text; SaveFooterText.ToolTip = SaveToText.Text;
        UseLimiter.IsChecked = item.SpeedLimitBytesPerSec > 0;
        if (item.SpeedLimitBytesPerSec > 0) LimitBox.Text = Math.Max(1, item.SpeedLimitBytesPerSec / 1024).ToString(CultureInfo.InvariantCulture);
        RememberLimit.IsChecked = item.SpeedLimitBytesPerSec > 0 && !item.LimitIsTemporary;
        ShowCompleteBox.IsChecked = item.ShowCompleteDialog ?? App.Settings.ShowCompleteDialog;
        ExitBox.IsChecked = item.ExitWhenDone;
        PowerBox.IsChecked = item.PowerWhenDone != null;
        PowerCombo.SelectedIndex = (int)(item.PowerWhenDone ?? PowerAction.ShutDown);
        ForceBox.IsChecked = item.ForcePowerAction;
        ApplyCompletionEnabled();
        LimitBox.IsEnabled = UseLimiter.IsChecked == true;

        var showDetails = App.Db.Get("progress_details") != "0";
        SetDetails(showDetails);

        _loading = false;
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        // Re-measure after the first frame. Later Details/Options toggles use the same path so the window
        // opens and closes by itself instead of making the user drag its bottom edge.
        ContentRendered += (_, _) => FitToContent();
        Closed += (_, _) => { _timer.Stop(); Open.Remove(item.Id); };
    }

    // ---------------------------------------------------------------- what is shown

    static string Speed(long bytesPerSecond) => DownloadItem.FormatBytes(bytesPerSecond) + "/s";

    void Refresh()
    {
        var item = _item;
        var running = item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued);
        var hasTotal = item.TotalBytes is > 0;

        Title = (hasTotal ? $"{item.Progress:0}% " : "") + item.FileName;
        TitleText.Text = Title;
        FileNameText.Text = item.FileName;
        FileNameText.ToolTip = item.FilePath;
        var address = item.Url.Split('#')[0];
        UrlText.Text = address; UrlText.ToolTip = address;

        switch (item.Status)
        {
            case nameof(DownloadStatus.Downloading): StatusText.Text = Loc.T(item.ActiveConnections > 0 || item.DoneBytes > 0 ? "Receiving data..." : "Connecting..."); break;
            case nameof(DownloadStatus.Queued): StatusText.Text = Loc.T("Waiting for a free download slot"); break;
            case nameof(DownloadStatus.Paused): StatusText.Text = Loc.T("Stopped"); break;
            case nameof(DownloadStatus.Failed): StatusText.Text = Loc.T("Error") + ": " + item.Description; break;
            default: StatusText.Text = Loc.T(item.Status); break;
        }
        StatusText.ToolTip = StatusText.Text;
        StatusText.Foreground = item.Status switch
        {
            nameof(DownloadStatus.Failed) => ResourceBrush("Danger"),
            nameof(DownloadStatus.Paused) => ResourceBrush("Warning"),
            nameof(DownloadStatus.Downloading) => ResourceBrush("Success"),
            _ => ResourceBrush("Text")
        };

        var totalText = hasTotal ? DownloadItem.FormatBytes(item.TotalBytes!.Value) : Loc.T("unknown");
        SizeText.Text = Loc.T("File size") + ": " + totalText;
        DownloadedText.Text = DownloadItem.FormatBytes(item.DoneBytes) + (hasTotal ? " / " + totalText : "");
        RateText.Text = running ? Speed(item.SpeedBytesPerSec) : "—";
        LimiterRate.Text = running ? Speed(item.SpeedBytesPerSec) : "—";
        EtaText.Text = running && item.EtaText != "—" && !string.IsNullOrEmpty(item.EtaText) ? item.EtaText : "—";
        PercentText.Text = hasTotal ? item.Progress.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";
        MainBar.Value = item.Progress;
        DiskLoadPanel.Visibility = item.DiskLoadedBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiskLoadBar.Value = item.DiskLoadPercent;
        DiskLoadText.Text = item.DiskLoadText;
        DiskLoadPercentText.Text = item.DiskLoadedBytes > 0 && hasTotal ? item.DiskLoadPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";

        if (App.Manager.SupportsResume(item) is { } known) _resume = known;
        ResumeText.Text = Loc.T("Resume capability") + ": " + (_resume == null ? "—" : Loc.T(_resume == true ? "Yes" : "No"));
        ResumeText.Foreground = _resume == true ? ResourceBrush("Success") : _resume == false ? ResourceBrush("Warning") : ResourceBrush("Muted");

        LimiterChipText.Text = Loc.T("Speed Limiter") + ": " + (item.SpeedLimitBytesPerSec > 0 ? Speed(item.SpeedLimitBytesPerSec) : Loc.T("Off"));
        CompletionChipText.Text = Loc.T("On completion") + ": " + CompletionSummary();

        StartPauseButton.Content = Loc.T(running ? "Pause" : "Start");
        StartPauseButton.IsEnabled = item.Status != nameof(DownloadStatus.Complete);

        RefreshConnections(running);
        _history.Add(DateTime.UtcNow, running ? item.SpeedBytesPerSec : 0, 0);
        AppendLog(_log.Observe(DateTime.Now, item.Status, item.LastError, running ? _lastRows.Count : 0, item.RetryCount));
        Sha256Text.Text = VerificationText.For(item);
        var canChange = !running && item.DoneBytes == 0 && item.Status != nameof(DownloadStatus.Complete);
        ChangeSaveButton.IsEnabled = canChange;
        ChangeSaveButton.ToolTip = Loc.T(canChange ? "Choose another folder for this download" : "The folder can only be changed before any data is saved (stop the download first).");
        RefreshTabs(running);

        if (item.Status is nameof(DownloadStatus.Complete) or nameof(DownloadStatus.Cancelled)) Close();
    }

    Brush ResourceBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;

    string CompletionSummary()
    {
        if (ShowCompleteBox.IsChecked == true) return Loc.T("Show dialog");
        if (PowerBox.IsChecked == true) return (PowerCombo.SelectedItem as ComboBoxItem)?.Content as string ?? Loc.T("Shut down");
        if (ExitBox.IsChecked == true) return Loc.T("Exit");
        return Loc.T("Nothing");
    }

    void RefreshPositions()
    {
        var map = App.Manager.GetPositionMap(_item, Buckets);
        for (var i = 0; i < Buckets; i++) _cells[i].Opacity = map[i];
    }

    /// <summary>
    /// Updates the connection rows in place (so the bars move instead of flickering). A connection's bar is how much it has
    /// fetched out of an equal share of the file; its speed is measured from what it fetched since the last update.
    /// </summary>
    void RefreshConnections(bool running)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Max(0.05, now - _lastTick);
        _lastTick = now;

        var rows = App.Manager.GetConnections(_item);
        if (rows.Count == 0) rows = new List<ConnectionInfo> { new(1, _item.DoneBytes, _item.Status == nameof(DownloadStatus.Downloading) ? "Receiving data..." : "Disconnect.") };

        _lastRows = rows;
        UpdateNetworks();
        var count = rows.Count;
        ConnCountText.Text = count == 1 ? Loc.T("1 connection") : Loc.F("{0} connections", count);
        ConnPill.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        double share = _item.TotalBytes is > 0 ? _item.TotalBytes.Value / (double)count : 0;
        var maxBytes = Math.Max(1, rows.Max(r => r.Downloaded));

        while (_connections.Count > count) _connections.RemoveAt(_connections.Count - 1);
        for (var i = 0; i < count; i++)
        {
            var row = rows[i];
            if (i >= _connections.Count) _connections.Add(new ConnectionView { Index = row.Index });
            var view = _connections[i];

            view.Label = "#" + row.Index.ToString("00", CultureInfo.InvariantCulture);
            view.Info = Loc.T(row.Info);
            view.Network = row.Network;

            var done = row.Info == "Download complete." || _item.Status == nameof(DownloadStatus.Complete);
            var fraction = share > 0 ? row.Downloaded / share : row.Downloaded / (double)maxBytes;
            view.Percent = done ? 100 : Math.Clamp(fraction * 100, 0, 99.5);
            view.IsDone = done;

            // live speed of this connection, smoothed so the numbers don't jump around
            var delta = view.LastBytes < 0 ? 0 : Math.Max(0, row.Downloaded - view.LastBytes);
            view.LastBytes = row.Downloaded;
            var instant = delta / dt;
            view.SmoothedSpeed = view.SmoothedSpeed <= 0 ? instant : view.SmoothedSpeed * 0.55 + instant * 0.45;
            var receiving = running && row.Info == "Receiving data...";
            view.SpeedText = done ? Loc.T("done")
                : receiving && view.SmoothedSpeed >= 1 ? Speed((long)view.SmoothedSpeed)
                : row.Info == "Send GET..." ? Loc.T("connecting")
                : Loc.T("idle");
        }
    }

    // ---------------------------------------------------------------- tabs: Overview / Segments / Connections / Sources / Multi-Network / Logs

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelLogs == null || sender is not RadioButton { Tag: string tag }) return;   // fires once while the window is being built
        _tab = tag;
        PanelOverview.Visibility = tag is "Overview" or "Segments" ? Visibility.Visible : Visibility.Collapsed;   // the segment grid now lives on the Overview, like the design
        PanelConnections.Visibility = tag == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        PanelSources.Visibility = tag == "Sources" ? Visibility.Visible : Visibility.Collapsed;
        PanelNetworks.Visibility = tag == "Networks" ? Visibility.Visible : Visibility.Collapsed;
        PanelLogs.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        if (!IsLoaded) return;
        Refresh();
        if (tag == "Logs") LogScroll.ScrollToEnd();
        FitToContent();
    }

    void RefreshTabs(bool running)
    {
        if (DetailsPanel.Visibility != Visibility.Visible) return;
        switch (_tab)
        {
            case "Overview": RefreshOverview(running); break;
            case "Segments": RefreshSegments(); break;
            case "Connections": RefreshPositions(); break;
            case "Sources": RefreshSources(running); break;
            case "Networks": RefreshNetworks(); break;
        }
    }

    void RefreshOverview(bool running)
    {
        var shares = NetworkBreakdown.FromConnections(_lastRows);
        StatConnections.Text = (running ? _lastRows.Count : 0) + " / " + _item.Connections;
        var (total, complete) = App.Manager.GetSegmentCounts(_item);
        StatSegments.Text = total > 0 ? complete + " / " + total : "—";
        StatSources.Text = running ? "1 / 1" : "0 / 1";
        StatMulti.Text = NetworkBreakdown.IsMultiNetwork(shares) ? Loc.F("Active ({0} networks)", shares.Count)
            : App.Manager.MultiNetworkEnabled ? Loc.T("On (one network)") : Loc.T("Off");
        StatUpload.Text = "—";            // an ordinary HTTP download does not upload anything
        DrawGraph();
    }

    void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (SpeedCanvas == null || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return;
        _rangeSeconds = seconds;
        DrawGraph();
    }

    void SpeedCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    /// <summary>Draws the download (and upload) speed of the last 1, 5 or 30 minutes. The graph starts when this window opens.</summary>
    void DrawGraph()
    {
        var width = SpeedCanvas.ActualWidth; var height = SpeedCanvas.ActualHeight;
        if (width < 20 || height < 20) return;
        var now = DateTime.UtcNow;
        var points = (int)Math.Clamp(width / 4, 20, 150);
        var down = _history.Series(_rangeSeconds, points, now);
        var up = _history.Series(_rangeSeconds, points, now, upload: true);
        var peak = Math.Max(down.Max(), up.Max());
        var top = SpeedHistory.AxisMaximum(peak);
        AxisTop.Text = Speed((long)top); AxisMid.Text = Speed((long)(top / 2));

        PointCollection Line(double[] values)
        {
            var line = new PointCollection(values.Length);
            for (var i = 0; i < values.Length; i++)
                line.Add(new Point(values.Length == 1 ? 0 : i * width / (values.Length - 1), height - 1 - Math.Clamp(values[i] / top, 0, 1) * (height - 4)));
            return line;
        }
        var downLine = Line(down);
        SpeedLine.Points = downLine;
        var fill = new PointCollection(downLine) { new Point(width, height), new Point(0, height) };
        SpeedFill.Points = fill;
        UploadLine.Points = up.Max() > 0 ? Line(up) : new PointCollection();

        var range = _rangeSeconds switch { 60 => "1 min", 300 => "5 min", _ => "30 min" };
        GraphSummaryText.Text = _history.SampleCount < 3
            ? Loc.T("The graph starts when this window opens.")
            : Loc.T("Peak") + " " + Speed((long)_history.Peak(_rangeSeconds, now)) + "  ·  " + Loc.T("last") + " " + range;
    }

    void RefreshSegments()
    {
        var map = App.Manager.GetPositionMap(_item, Buckets);
        var states = SegmentMap.Classify(map, _item.Status, _item.ActiveConnections);
        var brushes = new Dictionary<SegmentState, Brush>
        {
            [SegmentState.Downloading] = ResourceBrush("Accent"),
            [SegmentState.Complete] = ResourceBrush("Success"),
            [SegmentState.Waiting] = ResourceBrush("Faint"),
            [SegmentState.Error] = ResourceBrush("Danger"),
            [SegmentState.Unassigned] = ResourceBrush("Track"),
        };
        for (var i = 0; i < Buckets && i < states.Length; i++) _segmentCells[i].Background = brushes[states[i]];
        var counts = SegmentMap.Count(states);
        LegDownloading.Text = Loc.T("Downloading") + " (" + counts.Downloading + ")";
        LegComplete.Text = Loc.T("Complete") + " (" + counts.Complete + ")";
        LegWaiting.Text = Loc.T("Waiting") + " (" + counts.Waiting + ")";
        LegError.Text = Loc.T("Error") + " (" + counts.Error + ")";
        LegUnassigned.Text = Loc.T("Unassigned") + " (" + counts.Unassigned + ")";
        var (total, complete) = App.Manager.GetSegmentCounts(_item);
        SegmentSummaryText.Text = total > 0 ? Loc.F("{0} of {1} parts finished", complete, total) : "";
    }

    void RefreshSources(bool running)
    {
        var address = _item.Url.Split('#')[0];
        SourceUrlText.Text = address;
        var valid = Uri.TryCreate(address, UriKind.Absolute, out var uri);
        SourceHostText.Text = valid ? uri!.Host : "—";
        SourceSecurityText.Text = !valid ? "—" : uri!.Scheme == Uri.UriSchemeHttps ? Loc.T("HTTPS (encrypted)") : Loc.T("HTTP (not encrypted)");
        SourceResumeText.Text = _resume == null ? "—" : Loc.T(_resume == true ? "Yes" : "No");
        SourceCountText.Text = (running ? "1" : "0") + " / 1";
    }

    void RefreshNetworks()
    {
        var shares = NetworkBreakdown.FromConnections(_lastRows);
        MultiStatusText.Text = NetworkBreakdown.IsMultiNetwork(shares)
            ? Loc.F("Multi-Network is active: this download uses {0} networks at the same time.", shares.Count)
            : App.Manager.MultiNetworkEnabled
                ? Loc.T("Multi-Network is on, but this download uses only one network right now (it needs two connected networks and a large file).")
                : Loc.T("Multi-Network is off. Turn it on in Options to add up the speed of several connected networks.");
    }

    /// <summary>Keeps the network rows in place (so the bars move instead of flickering).</summary>
    void UpdateNetworks()
    {
        var shares = NetworkBreakdown.FromConnections(_lastRows);
        while (_networks.Count > shares.Count) _networks.RemoveAt(_networks.Count - 1);
        for (var i = 0; i < shares.Count; i++)
        {
            if (i >= _networks.Count) _networks.Add(new NetworkView());
            var view = _networks[i]; var share = shares[i];
            view.Name = share.Network == NetworkBreakdown.DefaultRoute ? Loc.T(share.Network) : share.Network;
            view.ConnectionsText = share.Connections == 1 ? Loc.T("1 connection") : Loc.F("{0} connections", share.Connections);
            view.BytesText = DownloadItem.FormatBytes(share.Bytes);
            view.Percent = share.Percent;
            view.PercentText = share.Percent.ToString("0", CultureInfo.InvariantCulture) + "%";
        }
        NetEmptyText.Visibility = shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AppendLog(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        foreach (var line in lines) _logLines.Add(line);
        while (_logLines.Count > 300) _logLines.RemoveAt(0);
        if (_tab == "Logs" && IsLoaded) LogScroll.ScrollToEnd();
    }

    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, _log.Lines)); } catch (Exception) { /* clipboard busy */ }
    }

    /// <summary>"Change": picks another folder. Only possible before any data is saved (the engine refuses otherwise and says why).</summary>
    void ChangeSave_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Choose the folder for this download"),
            InitialDirectory = System.IO.Path.GetDirectoryName(_item.FilePath) ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        var error = App.Manager.ChangeSaveFolder(_item, dialog.FolderName);
        if (error != null) { Dlg.Show(this, error, icon: MessageBoxImage.Warning); return; }
        var folder = System.IO.Path.GetDirectoryName(_item.FilePath) ?? _item.FilePath;
        SaveToText.Text = folder; SaveToText.ToolTip = folder;
        SaveFooterText.Text = folder; SaveFooterText.ToolTip = folder;
        _log.Add(DateTime.Now, "Save folder changed: " + folder);
        AppendLog(new[] { _log.Lines[^1] });
    }

    // ---------------------------------------------------------------- title bar

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void RefreshMaximizeButton()
    {
        if (!IsInitialized) return;
        var restored = WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = Loc.T(restored ? "Restore" : "Maximize");
        MaximizeGlyph.Data = Geometry.Parse(restored
            ? "M 4.5 2.5 H 11.5 V 9.5 H 9.5 M 2.5 4.5 H 9.5 V 11.5 H 2.5 Z"
            : "M 2.5 2.5 H 11.5 V 11.5 H 2.5 Z");
    }

    /// <summary>The X button: closes the window only - the download keeps running in the main window (as in IDM).</summary>
    void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- buttons

    void Details_Click(object sender, RoutedEventArgs e)
    {
        var show = DetailsPanel.Visibility != Visibility.Visible;
        SetDetails(show);
        App.Db.Set("progress_details", show ? "1" : "0");
    }

    void SetDetails(bool show)
    {
        DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = Loc.T(show ? "<< Hide details" : "Show details >>");
        if (show && IsLoaded) RefreshPositions();
        FitToContent();
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        var show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Content = Loc.T(show ? "Hide options" : "Options");
        FitToContent();
    }

    /// <summary>Resize vertically around the visible panels, capped to the monitor work area; overflow scrolls inside.</summary>
    void FitToContent()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (WindowState != WindowState.Normal) return;
            var keepWidth = Math.Clamp(ActualWidth > 0 ? ActualWidth : Width, MinWidth, MaxWidth);
            SizeToContent = SizeToContent.Manual;
            Width = keepWidth;
            SizeToContent = SizeToContent.Height;
            InvalidateMeasure();
        }, DispatcherPriority.Loaded);
    }

    void StartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued)) App.Manager.Pause(_item);
        else App.Manager.Enqueue(_item);
        Refresh();
    }

    /// <summary>Like IDM: Cancel stops the download and closes this window (the file stays in the list, and can be started again).</summary>
    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_item.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued)) App.Manager.Pause(_item);
        Close();
    }

    // ---------------------------------------------------------------- Speed Limiter

    void Limiter_Changed(object sender, RoutedEventArgs e) => ApplyLimiter();
    void Limiter_Text_Changed(object sender, TextChangedEventArgs e) => ApplyLimiter();

    void ApplyLimiter()
    {
        if (_loading) return;
        var on = UseLimiter.IsChecked == true;
        LimitBox.IsEnabled = on;
        var kb = long.TryParse(LimitBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : 0;
        App.Manager.SetItemSpeedLimit(_item, on && kb > 0 ? kb * 1024 : 0);
        _item.LimitIsTemporary = _item.SpeedLimitBytesPerSec > 0 && RememberLimit.IsChecked != true;
        Refresh();
    }

    // ---------------------------------------------------------------- Options on completion

    void Completion_Changed(object sender, RoutedEventArgs e) => ApplyCompletion();

    /// <summary>As in IDM the other choices are unavailable while "Show download complete dialog" is on.</summary>
    void ApplyCompletionEnabled()
    {
        var others = ShowCompleteBox.IsChecked != true;
        ExitBox.IsEnabled = others; PowerBox.IsEnabled = others;
        PowerCombo.IsEnabled = others && PowerBox.IsChecked == true;
        ForceBox.IsEnabled = others && PowerBox.IsChecked == true;
    }

    void ApplyCompletion()
    {
        if (_loading) return;
        _loading = true;
        if (ShowCompleteBox.IsChecked == true) { ExitBox.IsChecked = false; PowerBox.IsChecked = false; ForceBox.IsChecked = false; }
        _loading = false;
        ApplyCompletionEnabled();

        _item.ShowCompleteDialog = ShowCompleteBox.IsChecked == true;
        _item.ExitWhenDone = ExitBox.IsChecked == true;
        _item.PowerWhenDone = PowerBox.IsChecked == true ? (PowerAction)Math.Clamp(PowerCombo.SelectedIndex, 0, 3) : null;
        _item.ForcePowerAction = PowerBox.IsChecked == true && ForceBox.IsChecked == true;
        Refresh();
    }
}
