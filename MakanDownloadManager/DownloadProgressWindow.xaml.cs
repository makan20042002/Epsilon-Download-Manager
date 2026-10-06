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

    const int Buckets = 100;
    static readonly Dictionary<long, DownloadProgressWindow> Open = new();

    readonly DownloadItem _item;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Border[] _cells = new Border[Buckets];
    readonly ObservableCollection<ConnectionView> _connections = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTick;
    bool _loading = true;
    bool? _resume;

    static readonly Brush Green = Frozen(0x22, 0xE5, 0x8A);
    static readonly Brush Red = Frozen(0xFF, 0x6B, 0x6B);
    static readonly Brush Amber = Frozen(0xFF, 0xB4, 0x4A);
    static readonly Brush Muted = Frozen(0x93, 0xA1, 0xB5);
    static readonly Brush Text = Frozen(0xEA, 0xF1, 0xF7);

    static SolidColorBrush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }

    /// <summary>Shows the window for a download (or brings the existing one to the front).</summary>
    public static void ShowFor(DownloadItem item, Window? owner = null)
    {
        try
        {
            if (Open.TryGetValue(item.Id, out var existing)) { if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal; existing.Activate(); return; }
            var window = new DownloadProgressWindow(item);
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
        _item = item;
        ConnList.ItemsSource = _connections;

        var fill = (Brush)FindResource("BrandGrad");
        for (var i = 0; i < Buckets; i++)
        {
            _cells[i] = new Border { Background = fill, Opacity = 0 };
            PositionGrid.Children.Add(_cells[i]);
        }

        // start values from the download itself
        SaveToText.Text = System.IO.Path.GetDirectoryName(item.FilePath) is { Length: > 0 } dir ? dir : item.FilePath;
        SaveToText.ToolTip = SaveToText.Text;
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
        // SizeToContent + a custom title bar: measure once more after the first frame so no empty strip is left at the bottom
        ContentRendered += (_, _) => { SizeToContent = SizeToContent.Manual; SizeToContent = SizeToContent.Height; };
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
            nameof(DownloadStatus.Failed) => Red,
            nameof(DownloadStatus.Paused) => Amber,
            nameof(DownloadStatus.Downloading) => Green,
            _ => Text
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
        ResumeText.Foreground = _resume == true ? Green : _resume == false ? Amber : Muted;

        LimiterChipText.Text = Loc.T("Speed Limiter") + ": " + (item.SpeedLimitBytesPerSec > 0 ? Speed(item.SpeedLimitBytesPerSec) : Loc.T("Off"));
        CompletionChipText.Text = Loc.T("On completion") + ": " + CompletionSummary();

        StartPauseButton.Content = Loc.T(running ? "Pause" : "Start");
        StartPauseButton.IsEnabled = item.Status != nameof(DownloadStatus.Complete);

        RefreshConnections(running);
        if (DetailsPanel.Visibility == Visibility.Visible) RefreshPositions();

        if (item.Status is nameof(DownloadStatus.Complete) or nameof(DownloadStatus.Cancelled)) Close();
    }

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

    // ---------------------------------------------------------------- title bar

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

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
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        var show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OptionsButton.Content = Loc.T(show ? "Hide options" : "Options");
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
