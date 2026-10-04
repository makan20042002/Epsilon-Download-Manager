using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

public partial class MainWindow : Window
{
    // These are the very same DownloadItem objects the engine updates, so the list can never drift from reality.
    readonly ObservableCollection<DownloadItem> _items = new();
    readonly ICollectionView _view;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Forms.NotifyIcon _tray;
    readonly Dictionary<string, TextBlock> _counts = new();
    readonly Queue<Action> _prompts = new();
    bool _promptRunning;
    int _statusSignature;
    bool _exiting;
    TreeViewItem _queuesNode = null!;
    SchedulerWindow? _scheduler;
    DropTargetWindow? _drop;
    TreeViewItem _allNode = null!;
    string _lastClipboard = "";
    readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DispatcherTimer _marqueeScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    readonly HashSet<object> _marqueeBase = new();
    bool _marqueeArmed;
    bool _marqueeDragging;
    Point _marqueeStart;
    Point _marqueeCurrent;

    static bool AskFirst => App.Db.Get("ask_before_download") != "0";

    public MainWindow()
    {
        InitializeComponent();
        UpdateThemeGlyph();
        UpdateStatusPills();
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/makan.ico", UriKind.Absolute)); } catch (Exception) { /* the default icon is fine */ }
        BuildCategoryTree();
        _tray = CreateTray();

        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.Id), ListSortDirection.Descending));
        _view.Filter = Matches;
        Downloads.ItemsSource = _view;

        // Subscribe first, then snapshot, so an item added in between is never missed (Contains() guards duplicates).
        App.Manager.ItemAdded += Manager_ItemAdded;
        App.Manager.ItemRemoved += Manager_ItemRemoved;
        App.Manager.Notification += Manager_Notification;
        App.Queues.Changed += Queues_Changed;
        App.SettingsChanged += OnSettingsChanged;
        foreach (var item in App.Manager.Items) AddToList(item);

        Application.Current.SessionEnding += (_, _) => _exiting = true; // never block Windows sign-out / shutdown by hiding to the tray
        _timer.Tick += (_, _) => RefreshDashboard();
        _timer.Start();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        UpdateToolbar();
        UpdateCounts();
        if (App.Db.Get("drop_visible") == "1") SetDropTarget(true);
        _lastClipboard = ReadClipboardText();                 // whatever is on the clipboard now is not "new"
        _clipboardTimer.Tick += (_, _) => PollClipboard();
        _clipboardTimer.IsEnabled = App.Settings.ClipboardWatch;
        _marqueeScrollTimer.Tick += MarqueeScrollTimer_Tick;
    }

    // ---------------------------------------------------------------- category tree (like IDM's left pane)

    static string GlyphFor(string category) => category switch { "Video" => "🎞", "Music" => "🎵", "Documents" => "📄", "Programs" => "💿", "Compressed" => "🗜", "General" => "📁", _ => "📂" };

    void BuildCategoryTree()
    {
        _allNode = Node("all", "🗂", "All Downloads");
        Tree.Items.Add(_allNode);
        FillCategoryNodes();
        Tree.Items.Add(Node("unfinished", "⏳", "Unfinished"));
        Tree.Items.Add(Node("finished", "✅", "Finished"));
        Tree.Items.Add(Node("torrents", "🧲", "Torrents"));
        _queuesNode = Node("queues", "📚", "Queues");
        Tree.Items.Add(_queuesNode);
        RebuildQueueNodes();
        _allNode.IsSelected = true;
    }

    void FillCategoryNodes()
    {
        var selectedTag = (Tree.SelectedItem as TreeViewItem)?.Tag as string;
        _allNode.Items.Clear();
        foreach (var name in CategoryService.Names) _allNode.Items.Add(Node("cat:" + name, GlyphFor(name), name));
        foreach (var node in _allNode.Items.OfType<TreeViewItem>()) if ((string?)node.Tag == selectedTag) node.IsSelected = true;
    }

    /// <summary>Categories or the language may have changed in Options.</summary>
    void OnSettingsChanged() => Dispatcher.BeginInvoke(() =>
    {
        FillCategoryNodes(); UpdateCounts(); _view?.Refresh(); UpdateThemeGlyph(); UpdateStatusPills();
        _clipboardTimer.IsEnabled = App.Settings.ClipboardWatch;
        UpdateSpeedLimiterCaption(App.Manager.GlobalLimitBytesPerSec / 1024);   // e.g. a bandwidth profile applied from the Intelligent Center
    });

    TreeViewItem Node(string tag, string glyph, string text)
    {
        var count = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0) };
        _counts[tag] = count;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Emoji"), Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(new TextBlock { Text = Loc.T(text) });
        header.Children.Add(count);
        return new TreeViewItem { Header = header, Tag = tag, IsExpanded = true };
    }

    void Queues_Changed() => Dispatcher.BeginInvoke(() => { RebuildQueueNodes(); _view?.Refresh(); UpdateToolbar(); });

    void RebuildQueueNodes()
    {
        var selectedTag = (Tree.SelectedItem as TreeViewItem)?.Tag as string;
        _queuesNode.Items.Clear();
        foreach (var q in App.Queues.Queues)
        {
            var node = QueueNode(q);
            _queuesNode.Items.Add(node);
            if ((string?)node.Tag == selectedTag) node.IsSelected = true;
        }
        UpdateCounts();
    }

    /// <summary>Same as Node(), but with its own inline Start/Stop toggle - the direct alternative to picking a queue
    /// from the Start Queue / Stop Queue list in the More menu.</summary>
    TreeViewItem QueueNode(DownloadQueue q)
    {
        var running = App.Queues.IsRunning(q.Id);
        var count = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(6, 0, 0, 0) };
        _counts["queue:" + q.Id] = count;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = running ? "▶" : "📋", FontFamily = new FontFamily("Segoe UI Emoji"), Margin = new Thickness(0, 0, 6, 0) });
        header.Children.Add(new TextBlock { Text = q.Name });
        header.Children.Add(count);
        var toggle = new Button
        {
            Content = running ? "⏸" : "▶",
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(10, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            ToolTip = Loc.T(running ? "Stop this queue" : "Start this queue"),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        toggle.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Muted");
        toggle.Click += (_, ev) => { ev.Handled = true; if (App.Queues.IsRunning(q.Id)) App.Queues.Stop(q.Id); else App.Queues.Start(q.Id); };
        header.Children.Add(toggle);
        return new TreeViewItem { Header = header, Tag = "queue:" + q.Id, IsExpanded = true };
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => _view?.Refresh();

    void UpdateCounts()
    {
        var byCategory = _items.GroupBy(x => x.CategoryName).ToDictionary(g => g.Key, g => g.Count());
        void Set(string tag, int n) { if (_counts.TryGetValue(tag, out var box)) box.Text = n > 0 ? $"({n})" : ""; }
        Set("all", _items.Count);
        foreach (var name in CategoryService.Names) Set("cat:" + name, byCategory.GetValueOrDefault(name));
        Set("unfinished", _items.Count(x => x.Status != nameof(DownloadStatus.Complete)));
        Set("finished", _items.Count(x => x.Status == nameof(DownloadStatus.Complete)));
        Set("torrents", _items.Count(x => DownloadManager.IsTorrentUrl(x.Url)));
        Set("queues", _items.Count(x => x.QueueName != null));
        foreach (var q in App.Queues.Queues) Set("queue:" + q.Id, q.ItemIds.Count);
    }

    void ToggleCategories_Click(object sender, RoutedEventArgs e)
    {
        var show = ShowCategories.IsChecked;
        CategoryPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CategoryColumn.Width = show ? new GridLength(210) : new GridLength(0);
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        if (parts.Length != 2 || !Enum.TryParse<ListSortDirection>(parts[1], out var direction)) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(parts[0], direction));
            if (parts[0] != nameof(DownloadItem.Id))
                _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.Id), ListSortDirection.Descending));
        }
        Footer.Text = Loc.T("Downloads sorted") + ": " + Loc.T((string)((MenuItem)sender).Header);
    }

    // ---------------------------------------------------------------- tray / lifetime

    Forms.NotifyIcon CreateTray()
    {
        _staticTrayIcon = LoadTrayIcon();
        var tray = new Forms.NotifyIcon { Icon = _staticTrayIcon, Visible = true, Text = Branding.ProductName };
        tray.DoubleClick += (_, _) => ShowAndActivate();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Open"), null, (_, _) => ShowAndActivate());
        menu.Items.Add(Loc.T("Add clipboard URL"), null, (_, _) => Clipboard_Click(null, null));
        menu.Items.Add(Loc.T("Exit"), null, (_, _) => ExitApplication());
        tray.ContextMenuStrip = menu;
        return tray;
    }

    static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/makan.ico", UriKind.Absolute));
            if (info != null) using (info.Stream) return new System.Drawing.Icon(info.Stream);
        }
        catch (Exception) { /* fall back below */ }
        return System.Drawing.SystemIcons.Application;
    }

    // ---------------------------------------------------------------- tray: live download/upload speed on the icon itself
    // The same idea NetBalancer's tray icon uses: while something is actually moving, the icon itself is redrawn to show
    // the current speed (instead of a static logo), so activity is visible without opening the window at all. Idle goes
    // back to the plain Makan icon. A dynamically drawn icon needs its own care around Win32 icon handles - GDI handles
    // are a limited, leakable resource, and this redraws roughly twice a second for as long as the app runs.
    System.Drawing.Icon? _staticTrayIcon;
    System.Drawing.Icon? _dynamicTrayIcon;   // the previously-assigned dynamic one; disposed only once a new one has replaced it, never the instant it's set
    bool _trayShowingActivity;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    void UpdateTrayIcon(long downBytesPerSec, long upBytesPerSec)
    {
        const long idleThreshold = 1024;   // under 1 KB/s either way reads as "nothing meaningful happening", not a flickering icon over noise
        if (downBytesPerSec < idleThreshold && upBytesPerSec < idleThreshold)
        {
            if (_trayShowingActivity) { _tray.Icon = _staticTrayIcon; _tray.Text = Branding.ProductName; _trayShowingActivity = false; }
            return;
        }

        using var bitmap = RenderTraySpeedBitmap(downBytesPerSec, upBytesPerSec);
        var handle = bitmap.GetHicon();
        try
        {
            // Clone() makes a fully independent copy (its own handle), so the short-lived one from GetHicon can be
            // destroyed right away below instead of being kept alive for as long as the tray icon shows it.
            var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            var previous = _dynamicTrayIcon;
            _tray.Icon = icon;
            _dynamicTrayIcon = icon;
            previous?.Dispose();
            _trayShowingActivity = true;
        }
        finally { DestroyIcon(handle); }

        _tray.Text = $"{Branding.ProductName}\n\u2193 {Format(downBytesPerSec)}/s   \u2191 {Format(upBytesPerSec)}/s";
    }

    static string CompactSpeed(long bytesPerSec) =>
        bytesPerSec >= 1024 * 1024 ? (bytesPerSec / (1024.0 * 1024)).ToString("0.0") + "M" :
        bytesPerSec >= 1024 ? (bytesPerSec / 1024.0).ToString("0") + "K" : bytesPerSec.ToString();

    static System.Drawing.Bitmap RenderTraySpeedBitmap(long down, long up)
    {
        var bitmap = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        using var downBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 90, 190, 255));
        using var upBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 255, 165, 60));
        using var format = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
        g.DrawString(CompactSpeed(down), font, downBrush, new System.Drawing.RectangleF(0, -1, 32, 16), format);
        g.DrawString(CompactSpeed(up), font, upBrush, new System.Drawing.RectangleF(0, 15, 32, 16), format);
        return bitmap;
    }

    /// <summary>Brings the window to the front, un-minimising or un-hiding it as needed.</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // nudge above other windows without pinning it
    }

    /// <summary>Called on the UI thread for requests coming from the browser extension.</summary>
    public void HandleBridgeCommand(BridgeCommand command)
    {
        switch (command.Kind)
        {
            case "show":
                ShowAndActivate();
                break;
            case "media":
                ShowAndActivate();
                new MediaWindow(command.Url, command.Cookie, command.Referrer, command.UserAgent) { Owner = this }.Show();
                break;
        }
    }

    void Exit_Click(object sender, RoutedEventArgs e) => ExitApplication();

    /// <summary>A queue's "Exit Makan when done": no questions asked (the queue only runs the exit after all its downloads are finished).</summary>
    public void ExitFromScheduler()
    {
        _exiting = true;
        try { Close(); } catch (InvalidOperationException) { }
        Application.Current.Shutdown();
    }

    void ExitApplication()
    {
        var active = _items.Count(x => x.Status == nameof(DownloadStatus.Downloading));
        if (active > 0)
        {
            var text = $"{active} download(s) are still running. They will be stopped and stay in the list; press Resume next time.\n\nExit anyway?";
            var answer = IsVisible
                ? Dlg.Show(this, text, "Epsilon Download Manager", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : Dlg.Show(text, "Epsilon Download Manager", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _exiting = true;
        try { Close(); } catch (InvalidOperationException) { /* window never shown / already closing */ }
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The browser extension needs Makan running, so the window's X just tucks it into the tray (like IDM).
        if (!_exiting) { e.Cancel = true; Hide(); NotifyUser(Branding.ProductName, "Still running in the tray. Right-click the icon to exit."); return; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        App.Manager.ItemAdded -= Manager_ItemAdded;
        App.Manager.ItemRemoved -= Manager_ItemRemoved;
        App.Manager.Notification -= Manager_Notification;
        App.Queues.Changed -= Queues_Changed;
        App.SettingsChanged -= OnSettingsChanged;
        _clipboardTimer.Stop();
        _drop?.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _dynamicTrayIcon?.Dispose();
        _staticTrayIcon?.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    public void NotifyUser(string title, string message) => _tray.ShowBalloonTip(2500, Loc.T(title), Loc.T(message), Forms.ToolTipIcon.Info);

    // ---------------------------------------------------------------- manager events

    void AddToList(DownloadItem item) { if (!_items.Contains(item)) _items.Add(item); }
    void Manager_ItemAdded(DownloadItem item) => Dispatcher.BeginInvoke(() => { AddToList(item); UpdateCounts(); });
    void Manager_ItemRemoved(DownloadItem item) => Dispatcher.BeginInvoke(() => { _items.Remove(item); UpdateCounts(); });

    void Manager_Notification(DownloadItem item, string message) =>
        Dispatcher.BeginInvoke(() =>
        {
            Footer.Text = Loc.T(message) + ": " + item.FileName;
            if (!IsActive) NotifyUser(message, item.FileName);
            if (message == "Download complete") OnDownloadComplete(item);
        });

    void OnDownloadComplete(DownloadItem item)
    {
        // like IDM: no "complete" window for files a queue is working through
        if ((item.ShowCompleteDialog ?? App.Settings.ShowCompleteDialog) && item.QueueName == null)
        {
            try { new DownloadCompleteWindow(item).Show(); }
            catch (Exception ex) { ReportError("The download complete window", ex); }
        }
        if (App.Settings.AntivirusEnabled) RunAntivirus(item);

        // "Options on completion" of the progress window (IDM): set for this one download
        if (item.PowerWhenDone is { } power) { item.PowerWhenDone = null; _ = new WindowsPower().PowerOffAsync(power, item.ForcePowerAction); }
        else if (item.ExitWhenDone) { item.ExitWhenDone = false; ExitFromScheduler(); }
    }

    /// <summary>Optional: hand the finished file to an antivirus program (default: Microsoft Defender's command line scanner).</summary>
    void RunAntivirus(DownloadItem item)
    {
        var s = App.Settings;
        if (!File.Exists(s.AntivirusProgram) || !File.Exists(item.FilePath)) return;
        var (program, args) = AntivirusCommand.Build(s.AntivirusProgram, s.AntivirusArguments, item.FilePath);
        var name = item.FileName;
        _ = Task.Run(async () =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false, CreateNoWindow = true });
                if (process == null) return;
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
                if (process.ExitCode != 0 && process.ExitCode != 2) await Dispatcher.InvokeAsync(() => NotifyUser("Epsilon Download Manager", Loc.F("The antivirus program reported a problem with {0}", name)));
            }
            catch (Exception) { /* no antivirus / timed out: nothing to report */ }
        });
    }

    // ---- links copied to the clipboard (Options > General) --------------------------------------------------------------------------

    static string ReadClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch (Exception) { return ""; }   // the clipboard can be locked by another program
    }

    void PollClipboard()
    {
        if (!App.Settings.ClipboardWatch) return;
        var text = ReadClipboardText();
        if (text.Length == 0 || text == _lastClipboard) return;
        _lastClipboard = text;
        var rules = App.Settings.BuildCaptureRules(); rules.Browsers = "";           // no browser involved here
        // like IDM: only addresses of a type on the "File types" list (a plain web page link is not a download)
        var urls = ExtractUrls(text).Where(u => Path.GetExtension(new Uri(u).AbsolutePath).Length > 1 && rules.Evaluate(u, null, null).Capture).ToList();
        if (urls.Count > 0) AddUrls(urls);
    }

    // ---------------------------------------------------------------- asking before anything starts

    /// <summary>Dialogs from the browser can arrive in a burst: show them one after another.</summary>
    void Prompt(Action show)
    {
        _prompts.Enqueue(show);
        if (_promptRunning) return;
        _promptRunning = true;
        try
        {
            while (_prompts.Count > 0)
            {
                var next = _prompts.Dequeue();
                try { next(); } catch (Exception ex) { ReportError("Opening the window", ex); }
            }
        }
        finally { _promptRunning = false; }
    }

    internal static string FolderFor(string fileName) => App.FolderForFile(fileName);
    internal static string FolderForCategory(string category) => CategoryService.FolderFor(category, App.Settings.DefaultFolder);

    /// <summary>"Change the folder of a category to the one I chose last" (Options > Save to).</summary>
    static void RememberFolder(string fileName, string folder)
    {
        if (!App.Settings.ChangeFolderOnLastSelected) return;
        CategoryService.SetFolder(CategoryService.For(fileName), folder);
        App.Settings.CategoriesJson = CategoryService.ToJson();
    }

    /// <summary>The same address already in the list (in any state except cancelled).</summary>
    static DownloadItem? FindDuplicate(string url) =>
        App.Manager.Items.FirstOrDefault(i => string.Equals(i.Url, url, StringComparison.Ordinal) && i.Status != nameof(DownloadStatus.Cancelled));

    /// <summary>Start Download runs it now; Download Later parks it (stopped) in the chosen queue.</summary>
    static void Place(DownloadItem item, DownloadChoice choice, int queueId)
    {
        if (choice == DownloadChoice.Start) { App.Manager.Enqueue(item); DownloadProgressWindow.ShowIfWanted(item); }
        else App.Queues.AddLater(item, queueId);
    }

    static string Describe(long? size, string? contentType, bool answered)
    {
        if (!answered) return Loc.T("Checking the link…");
        var text = size is > 0 ? Loc.F("Size: {0}", DownloadItem.FormatBytes(size.Value)) : Loc.T("Size: unknown");
        return string.IsNullOrWhiteSpace(contentType) ? text : text + "   ·   " + Loc.F("Type: {0}", contentType);
    }

    /// <summary>A file from the browser (or the Add URL box): confirm name and folder, then Start or Later. Called on the UI thread.</summary>
    public void PromptDownload(DownloadPrompt p) => Prompt(() => ShowDownloadDialog(p));

    void ShowDownloadDialog(DownloadPrompt p)
    {
        if (YtDlpService.IsYouTubeUrl(p.Url)) { ShowYouTubeDialog(p.Url); return; }
        var overwrite = false;
        if (FindDuplicate(p.Url) is { } duplicate)
        {
            switch (App.Settings.DuplicateAction)
            {
                case "skip":
                    Footer.Text = Loc.T("That link is already in the list");
                    NotifyUser("Epsilon Download Manager", "Already in the list: " + duplicate.FileName);
                    return;
                case "suffix": break;
                case "overwrite": overwrite = true; break;
                default:
                    ShowAndActivate();
                    var answer = Dlg.Show(this, Loc.F("This link is already in the list ({0}).\n\nYes = download it again under a new name\nNo = download it again and replace the existing file\nCancel = do nothing", duplicate.StatusText),
                        "Duplicate link", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Cancel) return;
                    overwrite = answer == MessageBoxResult.No;
                    break;
            }
        }
        ShowAndActivate();
        var name = DownloadFileNamer.Sanitize(p.FileName ?? p.ProbedName ?? GuessFileName(p.Url), "download.bin");
        var folder = !string.IsNullOrWhiteSpace(p.Folder) ? p.Folder : FolderFor(name);
        var known = p.Size != null || p.ProbedName != null || p.ContentType != null;
        var dialog = new DownloadInfoDialog("Download File Info", p.Url, name, folder, Describe(p.Size, p.ContentType, known)) { Owner = this };

        using var cts = new CancellationTokenSource();
        if (!known) _ = ProbeIntoAsync(dialog, p, cts.Token);
        dialog.ShowDialog();
        cts.Cancel();
        if (dialog.Choice == DownloadChoice.Cancel) return;

        try { Directory.CreateDirectory(dialog.Folder); }
        catch (Exception ex) { Dlg.Show(this, $"Can't use the folder \"{dialog.Folder}\":\n{ex.Message}", "Epsilon Download Manager"); return; }
        var finalName = DownloadFileNamer.Sanitize(dialog.FileName, "download.bin");
        RememberFolder(finalName, dialog.Folder);

        var item = new DownloadItem
        {
            Url = p.Url, FilePath = Path.Combine(dialog.Folder, finalName), Priority = 5,
            Cookie = p.Cookie, Referrer = p.Referrer, UserAgent = p.UserAgent,
            Connections = p.Connections > 0 ? p.Connections : App.Manager.DefaultConnections,
            SpeedLimitBytesPerSec = Math.Max(0, p.SpeedLimitBytesPerSec), Overwrite = overwrite,
            // name still only guessed from the URL: let the server's real name replace it when the download starts
            AutoName = p.FileName == null && p.ProbedName == null && !dialog.NameEdited && !dialog.NameFromServer
        };
        Place(item, dialog.Choice, dialog.QueueId);
        Dispatcher.BeginInvoke(() => Downloads.SelectedItem = item, DispatcherPriority.Background);
        Footer.Text = Loc.T(dialog.Choice == DownloadChoice.Start ? "Download started" : "Added to the queue \"" + (item.QueueName ?? "Main download queue") + "\"");
    }

    async Task ProbeIntoAsync(DownloadInfoDialog dialog, DownloadPrompt p, CancellationToken ct)
    {
        try
        {
            var probeItem = new DownloadItem { Url = p.Url, Cookie = p.Cookie, Referrer = p.Referrer, UserAgent = p.UserAgent };
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(12));
            var probe = await App.Manager.ProbeAsync(probeItem, limit.Token);
            dialog.SetInfo(Describe(probe.Length, probe.ContentType, true));
            if (p.FileName == null) dialog.SetNameFromServer(probe.FileName ?? "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { dialog.SetInfo("Couldn't check the link (" + ex.Message + "). You can still download it."); }
    }

    /// <summary>"Download all links on this page": one dialog for the whole batch. Called on the UI thread.</summary>
    public void PromptBatch(BatchPrompt b) => Prompt(() => ShowBatchDialog(b));

    void ShowBatchDialog(BatchPrompt b)
    {
        ShowAndActivate();
        var dialog = new DownloadInfoDialog($"Add {b.Urls.Count} downloads", $"{b.Urls.Count} links", "", FolderFor("file.zip"),
            "File names are taken from the servers. Nothing starts until you choose.", batch: true) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Choice == DownloadChoice.Cancel) return;
        try { Directory.CreateDirectory(dialog.Folder); }
        catch (Exception ex) { Dlg.Show(this, $"Can't use the folder \"{dialog.Folder}\":\n{ex.Message}", "Epsilon Download Manager"); return; }
        var added = 0;
        foreach (var url in b.Urls)
        {
            if (App.Manager.FindActive(url) != null) continue;
            Place(new DownloadItem
            {
                Url = url, FilePath = Path.Combine(dialog.Folder, DownloadFileNamer.Sanitize(GuessFileName(url), "download.bin")), AutoName = true,
                Cookie = b.Cookie, Referrer = b.Referrer, UserAgent = b.UserAgent, Priority = 5, Connections = App.Manager.DefaultConnections
            }, dialog.Choice, dialog.QueueId);
            added++;
        }
        Footer.Text = Loc.T($"Added {added} download(s)" + (dialog.Choice == DownloadChoice.Start ? "" : " to the queue"));
    }

    /// <summary>A video (HLS / DASH) from the browser: confirm name and folder, then Start or Later. Called on the UI thread.</summary>
    public void PromptStream(StreamRequest request) => Prompt(() => ShowStreamDialog(request));

    void ShowStreamDialog(StreamRequest request)
    {
        ShowAndActivate();
        var name = NativeBridge.StreamFileName(request);
        var kind = YtDlpService.TryGetSelection(request.Url, out _, out _)
            ? request.Format switch { "mp4" => "MP4 video (with sound)", "srt" => "Subtitle file (SRT)", _ => "Audio only" }
            : request.Format == "mp4" ? "MP4 video" : "TS video (plays in VLC and most players; can be converted to MP4)";
        var extra = request.AudioUrl != null ? "   ·   has a separate audio track" : "";
        var dialog = new DownloadInfoDialog("Download File Info", request.Url.Split('#')[0], name, FolderFor(name), Loc.T(kind) + Loc.T(extra)) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Choice == DownloadChoice.Cancel) return;

        try { Directory.CreateDirectory(dialog.Folder); }
        catch (Exception ex) { Dlg.Show(this, $"Can't use the folder \"{dialog.Folder}\":\n{ex.Message}", "Epsilon Download Manager"); return; }
        var finalName = DownloadFileNamer.Sanitize(dialog.FileName, "video." + request.Format);
        if (!Path.HasExtension(finalName)) finalName += "." + request.Format;
        RememberFolder(finalName, dialog.Folder);

        var item = NativeBridge.BuildStreamItem(request, Path.Combine(dialog.Folder, finalName), App.Manager.DefaultConnections);
        Place(item, dialog.Choice, dialog.QueueId);
        Dispatcher.BeginInvoke(() => Downloads.SelectedItem = item, DispatcherPriority.Background);
        Footer.Text = Loc.T(dialog.Choice == DownloadChoice.Start ? "Video download started" : "Video added to the queue");
    }

    /// <summary>A YouTube (or other yt-dlp) address: if it turns out to be a playlist, offer to grab the whole thing at
    /// one quality; otherwise (or if the person just wants this one video), pick quality / audio / subtitles as before.</summary>
    async void ShowYouTubeDialog(string url)
    {
        ShowAndActivate();

        IReadOnlyList<YtPlaylistEntry>? entries = null; var playlistTitle = "";
        try
        {
            if (App.Manager.YtDlp is { } yt)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var info = await yt.ResolvePlaylistAsync(url, timeout.Token);
                if (info.Entries.Count > 1) { entries = info.Entries; playlistTitle = info.Title; }
            }
        }
        catch (Exception) { /* could not tell whether this is a playlist: treat it as the one video, same as always */ }

        if (entries != null)
        {
            var picker = new PlaylistDialog(this, playlistTitle, entries.Count);
            picker.ShowDialog();
            if (picker.Choice == PlaylistChoice.Cancel) return;
            if (picker.Choice == PlaylistChoice.DownloadAll)
            {
                var folder = FolderFor("video.mp4");
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { Dlg.Show(this, $"Can't use the folder \"{folder}\":\n{ex.Message}", "Epsilon Download Manager"); return; }
                int added = 0, skipped = 0;
                foreach (var entry in entries) { if (PlaceYouTubeSelection(entry.Url, entry.Title, picker.SelectedKey, folder, DownloadChoice.Start, quiet: true)) added++; else skipped++; }
                Footer.Text = Loc.F("Added {0} video(s) from the playlist", added) + (skipped > 0 ? " " + Loc.F("({0} already in the list)", skipped) : "");
                return;
            }
            // "Just this one video": falls through to the ordinary single-video dialog below, using the original address
        }

        var dialog = new YouTubeDialog(url, FolderFor("video.mp4")) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Choice == DownloadChoice.Cancel || dialog.Selected is not { } option) return;
        try { Directory.CreateDirectory(dialog.Folder); }
        catch (Exception ex) { Dlg.Show(this, $"Can't use the folder \"{dialog.Folder}\":\n{ex.Message}", "Epsilon Download Manager"); return; }
        PlaceYouTubeSelection(url, dialog.ChosenTitle, option.Key, dialog.Folder, dialog.Choice, quiet: false, option.ApproxBytes);
    }

    /// <summary>Builds one yt-dlp download from a chosen quality and places it in the queue. Returns false (and does
    /// nothing) if that address is already in the list - used both for a single video and for each video of a playlist.</summary>
    bool PlaceYouTubeSelection(string url, string? title, string key, string folder, DownloadChoice choice, bool quiet, long? estimatedSize = null)
    {
        var plan = YtDlpService.PlanFor(key);
        var request = new StreamRequest(YtDlpService.WithSelection(url, key), null, title, plan.OutputExtension, null, url, null, estimatedSize);
        if (FindDuplicate(request.Url) is { } duplicate)
        {
            if (!quiet) { Footer.Text = Loc.T("That link is already in the list"); Dispatcher.BeginInvoke(() => Downloads.SelectedItem = duplicate, DispatcherPriority.Background); }
            return false;
        }
        var item = NativeBridge.BuildStreamItem(request, Path.Combine(folder, NativeBridge.StreamFileName(request)), App.Manager.DefaultConnections);
        Place(item, choice, App.Queues.Main.Id);
        if (!quiet)
        {
            Dispatcher.BeginInvoke(() => Downloads.SelectedItem = item, DispatcherPriority.Background);
            Footer.Text = Loc.T(choice == DownloadChoice.Start ? "Video download started" : "Video added to the queue");
        }
        return true;
    }

    /// <summary>"Download all links" from the browser: every link of the page in one window (like IDM). Called on the UI thread.</summary>
    public void PromptLinks(LinksPrompt p) => Prompt(() => ShowLinksDialog(p));

    void ShowLinksDialog(LinksPrompt p)
    {
        ShowAndActivate();
        var dialog = new LinksDialog(p) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Choice == DownloadChoice.Cancel) return;
        var added = 0; var skipped = 0;
        foreach (var row in dialog.Selected)
        {
            var duplicate = FindDuplicate(row.Url);
            if (App.Manager.FindActive(row.Url) != null || (duplicate != null && App.Settings.DuplicateAction == "skip")) { skipped++; continue; }
            var path = row.SavePath;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch (Exception) { skipped++; continue; }
            var item = new DownloadItem
            {
                Url = row.Url, FilePath = path, Cookie = row.Cookie, Referrer = p.Referrer, UserAgent = p.UserAgent, Priority = 5,
                Connections = App.Manager.DefaultConnections, AutoName = !row.NameEdited && !row.NameFromServer,
                Overwrite = duplicate != null && App.Settings.DuplicateAction == "overwrite"
            };
            Place(item, dialog.Choice, dialog.QueueId);
            added++;
        }
        Footer.Text = Loc.T($"Added {added} download(s)" + (dialog.Choice == DownloadChoice.Start ? "" : " to the queue") + (skipped > 0 ? $" ({skipped} skipped)" : ""));
    }

    // ---------------------------------------------------------------- adding downloads

    void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.N && ctrl) { Add_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (typing) return;
        if (e.Key == Key.V && ctrl && Clipboard.ContainsText())
        {
            var urls = ExtractUrls(Clipboard.GetText());
            if (urls.Count > 0) { AddUrls(urls); e.Handled = true; }
        }
        else if (e.Key == Key.Delete) { Remove_Click(this, null); e.Handled = true; }
        else if (e.Key == Key.Enter) { Open_Click(this, null); e.Handled = true; }
    }

    void Add_Click(object? sender, RoutedEventArgs? e)
    {
        var dialog = new InputDialog { Owner = this };
        if (Clipboard.ContainsText() && ExtractUrls(Clipboard.GetText()).FirstOrDefault() is { } clip) dialog.Prefill(clip);
        if (dialog.ShowDialog() != true) return;
        AddOne(new DownloadPrompt(dialog.Url, null, NullIfEmpty(dialog.Cookie), NullIfEmpty(dialog.Referrer), null, null,
            SpeedLimitBytesPerSec: dialog.SpeedLimitBytesPerSec, Connections: dialog.ConnectionCount, Folder: dialog.SaveFolder));
    }

    void AddTorrent_Click(object? sender, RoutedEventArgs? e)
    {
        var dialog = new AddTorrentDialog(this);
        if (Clipboard.ContainsText() && MagnetLink.IsMagnet(Clipboard.GetText().Trim())) dialog.Prefill(Clipboard.GetText().Trim());
        if (dialog.ShowDialog() != true) return;
        var result = App.Manager.AddTorrent(dialog.Url, App.Settings.TorrentSaveFolder);
        if (result.Error != null) { Dlg.Show(this, result.Error, "Add torrent", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Footer.Text = Loc.T(result.Duplicate ? "Already in the list" : "Download started");
        if (!result.Duplicate) TorrentWindow.ShowFor(result.Item!, this);
    }

    void Clipboard_Click(object? sender, RoutedEventArgs? e)
    {
        var urls = Clipboard.ContainsText() ? ExtractUrls(Clipboard.GetText()) : new List<string>();
        if (urls.Count == 0) { Dlg.Show("The clipboard doesn't contain an HTTP/HTTPS link.", "Epsilon Download Manager"); return; }
        AddUrls(urls);
    }

    void AddUrls(IReadOnlyList<string> urls)
    {
        if (urls.Count == 1) { AddOne(new DownloadPrompt(urls[0], null, null, null, null, null)); return; }
        OpenLinkPicker(urls.Select(u => new LinkEntry(u, null, "link")), null, null);   // several addresses: choose in the list window
    }

    void AddOne(DownloadPrompt p)
    {
        if (DownloadManager.IsTorrentUrl(p.Url))
        {
            var result = App.Manager.AddTorrent(p.Url, App.Settings.TorrentSaveFolder);
            if (result.Error != null) { Dlg.Show(this, result.Error, "Add torrent", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            Footer.Text = Loc.T(result.Duplicate ? "Already in the list" : "Download started");
            if (!result.Duplicate) TorrentWindow.ShowFor(result.Item!, this);
            return;
        }
        if (!LooksLikeUrl(p.Url)) return;
        if (YtDlpService.IsYouTubeUrl(p.Url)) { Prompt(() => ShowYouTubeDialog(p.Url)); return; }   // YouTube: choose the quality first
        if (AskFirst) PromptDownload(p); else QuickAdd(p);
    }

    /// <summary>Only used when the user switched "always ask" off in Settings.</summary>
    void QuickAdd(DownloadPrompt p)
    {
        if (!LooksLikeUrl(p.Url) || App.Manager.FindActive(p.Url) != null) return;
        var name = DownloadFileNamer.Sanitize(p.FileName ?? GuessFileName(p.Url), "download.bin");
        var folder = !string.IsNullOrWhiteSpace(p.Folder) ? p.Folder : FolderFor(name);
        try { Directory.CreateDirectory(folder); } catch (Exception ex) { Footer.Text = ex.Message; return; }
        var quick = new DownloadItem
        {
            Url = p.Url, FilePath = Path.Combine(folder, name), AutoName = p.FileName == null, Priority = 5,
            Cookie = p.Cookie, Referrer = p.Referrer, UserAgent = p.UserAgent,
            Connections = p.Connections > 0 ? p.Connections : App.Manager.DefaultConnections, SpeedLimitBytesPerSec = Math.Max(0, p.SpeedLimitBytesPerSec)
        };
        App.Manager.Enqueue(quick);
        DownloadProgressWindow.ShowIfWanted(quick);
        Footer.Text = Loc.T("Download started");
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        var text = e.Data.GetDataPresent(DataFormats.UnicodeText) ? e.Data.GetData(DataFormats.UnicodeText)?.ToString()
                 : e.Data.GetDataPresent(DataFormats.Text) ? e.Data.GetData(DataFormats.Text)?.ToString() : null;
        if (text != null) HandleDroppedText(text);
    }

    /// <summary>Text dropped on the window or on the floating drop target: every http(s) address in it.</summary>
    public void HandleDroppedText(string text)
    {
        var urls = ExtractUrls(text);
        if (urls.Count > 0) AddUrls(urls);
        else NotifyUser("Epsilon Download Manager", "No download link found in what you dropped.");
    }

    // ---- IDM's Tasks menu: batch download, site grabber, drop target, text export / import -----------------------------------------

    void OpenLinkPicker(IEnumerable<LinkEntry> links, string? referrer, string? title)
    {
        var list = links.ToList();
        if (list.Count == 0) { Dlg.Show(this, "There are no addresses to show.", "Epsilon Download Manager"); return; }
        PromptLinks(new LinksPrompt(list, new Dictionary<string, string>(), referrer, null, title));
    }

    void AddBatch_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        OpenLinkPicker(dialog.Urls.Select(u => new LinkEntry(u, null, "link")), null, "Batch download");
    }

    void BatchClipboard_Click(object sender, RoutedEventArgs e) => Clipboard_Click(sender, e);

    static readonly HashSet<string> GrabImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif" };
    static readonly HashSet<string> GrabMediaExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mkv", ".mov", ".m4v", ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".m3u8", ".mpd" };

    static string KindOf(string url)
    {
        string ext;
        try { ext = Path.GetExtension(new Uri(url).AbsolutePath); } catch (UriFormatException) { ext = ""; }
        return GrabImageExts.Contains(ext) ? "image" : GrabMediaExts.Contains(ext) ? "media" : "link";
    }

    async void SiteGrabber_Click(object sender, RoutedEventArgs e)
    {
        var start = Clipboard.ContainsText() ? ExtractUrls(Clipboard.GetText()).FirstOrDefault() ?? "" : "";
        var ask = new TextPromptDialog("Run site grabber", "Address of the web page whose links and files you want to grab:", start) { Owner = this };
        if (ask.ShowDialog() != true) return;
        var page = ask.Value;
        if (!LooksLikeUrl(page)) { Dlg.Show(this, "The address must start with http:// or https://", "Site grabber", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        List<string> urls;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Footer.Text = Loc.T("Reading the page…");
            urls = await new SiteGrabber().ExtractAsync(page);
        }
        catch (Exception ex) { ReportError(Loc.F("Reading {0}", page), ex); return; }
        finally { Mouse.OverrideCursor = null; }
        Footer.Text = Loc.T($"Found {urls.Count} addresses");
        OpenLinkPicker(urls.Select(u => new LinkEntry(u, null, KindOf(u))), page, new Uri(page).Host);
    }

    void ShowDropTarget_Click(object sender, RoutedEventArgs e) => SetDropTarget(ShowDropTarget.IsChecked);

    /// <summary>Shows or hides the small floating window that accepts dropped links.</summary>
    public void SetDropTarget(bool show)
    {
        App.Db.Set("drop_visible", show ? "1" : "0");
        ShowDropTarget.IsChecked = show;
        if (show)
        {
            if (_drop == null) { _drop = new DropTargetWindow(); _drop.Closed += (_, _) => _drop = null; }
            _drop.Show();
        }
        else { _drop?.Close(); _drop = null; }
    }

    void ExportText_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems().ToList();
        if (items.Count == 0) items = _items.ToList();          // nothing selected: everything
        if (items.Count == 0) { Dlg.Show(this, "The list is empty.", "Export"); return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export addresses to a text file", Filter = "Text file (*.txt)|*.txt", FileName = "makan-downloads.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, UrlText.ToTextFile(items.Select(i => i.Url)), new UTF8Encoding(false));
            Footer.Text = Loc.T($"Exported {items.Count} address(es) to {dialog.FileName}");
        }
        catch (Exception ex) { ReportError("Export", ex); }
    }

    void ImportText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import addresses from a text file", Filter = "Text file (*.txt)|*.txt|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var urls = UrlText.ExtractUrls(File.ReadAllText(dialog.FileName));
            if (urls.Count == 0) { Dlg.Show(this, "No http:// or https:// addresses were found in that file.", "Import"); return; }
            OpenLinkPicker(urls.Select(u => new LinkEntry(u, null, "link")), null, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { ReportError("Import", ex); }
    }

    /// <summary>A problem the user should see (and be able to report) instead of failing silently.</summary>
    void ReportError(string what, Exception ex)
    {
        try { new DiagnosticsService().Error(what, ex); } catch (Exception) { /* logging must never make it worse */ }
        var text = $"{what} did not work:\n\n{ex.Message}\n\nDetails are in the log (Diagnostics report). Please send this message if it keeps happening.";
        if (IsVisible) Dlg.Show(this, text, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        else Dlg.Show(text, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---------------------------------------------------------------- toolbar / menu actions

    IEnumerable<DownloadItem> SelectedItems() => Downloads.SelectedItems.OfType<DownloadItem>().ToList();
    DownloadItem? Selected() => Downloads.SelectedItem as DownloadItem;

    static bool CanResume(DownloadItem x) => x.Status is nameof(DownloadStatus.Paused) or nameof(DownloadStatus.Failed) or nameof(DownloadStatus.Cancelled);
    static bool IsRunning(DownloadItem x) => x.Status is nameof(DownloadStatus.Downloading) or nameof(DownloadStatus.Queued);

    /// <summary>Resumes exactly the selected item(s) - never the rest of a queue they happen to belong to. A queue only
    /// fills multiple slots at once when the person explicitly starts the whole queue (Start Queue, or the sidebar's
    /// per-queue toggle); resuming one paused file by hand must resume only that file.</summary>
    void Start_Click(object? sender, RoutedEventArgs? e)
    {
        foreach (var item in SelectedItems().Where(CanResume)) App.Queues.ResumeItem(item);
    }
    void Pause_Click(object? sender, RoutedEventArgs? e) { foreach (var item in SelectedItems().Where(IsRunning)) App.Manager.Pause(item); }
    void StopAll_Click(object? sender, RoutedEventArgs? e)
    {
        App.Queues.StopAll();
        foreach (var item in _items.Where(IsRunning).ToList()) App.Manager.Pause(item);
    }
    void StartAll_Click(object? sender, RoutedEventArgs? e)
    {
        foreach (var q in App.Queues.Queues.Where(q => q.ItemIds.Count > 0 && !App.Queues.IsRunning(q.Id))) App.Queues.Start(q.Id);
        foreach (var item in _items.Where(x => CanResume(x) && App.Queues.QueueOf(x.Id) == null).ToList()) App.Manager.Enqueue(item);
    }
    // ---- queues ----------------------------------------------------------------------------------------------------------

    // ---- speed limiter (toolbar, IDM-style) ---------------------------------------------------------------------------

    bool _speedLimiterSyncing;

    void SpeedLimiter_Click(object? sender, RoutedEventArgs? e)
    {
        SyncSpeedLimiterUi();
        SpeedLimiterPopup.IsOpen = true;
    }

    void More_Click(object? sender, RoutedEventArgs? e) => MorePopup.IsOpen = true;

    /// <summary>Fills the popup from the current global limit and (re)builds the preset buttons.</summary>
    void SyncSpeedLimiterUi()
    {
        _speedLimiterSyncing = true;
        try
        {
            var kbps = App.Manager.GlobalLimitBytesPerSec / 1024;
            SpeedLimiterToggle.IsChecked = kbps > 0;
            SpeedLimiterSlider.IsEnabled = kbps > 0;
            SpeedLimiterBox.IsEnabled = kbps > 0;
            SpeedLimiterSlider.Value = Math.Min(SpeedLimiterSlider.Maximum, kbps);
            SpeedLimiterBox.Text = kbps > 0 ? kbps.ToString() : "";
            SpeedCombined.IsChecked = App.Manager.LimitScope == SpeedLimitScope.Combined;
            SpeedPerFile.IsChecked = App.Manager.LimitScope == SpeedLimitScope.PerDownload;

            SpeedLimiterPresets.Children.Clear();
            foreach (var profile in App.BandwidthProfiles.Load())
            {
                var presetKbps = profile.LimitBytesPerSec <= 0 ? 0 : Math.Max(1, profile.LimitBytesPerSec / 1024);
                var button = new Button { Style = (Style)FindResource("ToolBtn"), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 5, 10, 5), MinWidth = 0, Tag = presetKbps };
                button.Content = new TextBlock { Text = Loc.T(profile.Name), FontSize = 12 };
                button.ToolTip = Loc.T(profile.Description);
                button.Click += (_, _) => ApplySpeedLimitKbps(presetKbps);
                SpeedLimiterPresets.Children.Add(button);
            }
            UpdateSpeedLimiterCaption(kbps);
        }
        finally { _speedLimiterSyncing = false; }
    }

    void SpeedLimiterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_speedLimiterSyncing) return;
        var on = SpeedLimiterToggle.IsChecked == true;
        SpeedLimiterSlider.IsEnabled = on; SpeedLimiterBox.IsEnabled = on;
        ApplySpeedLimitKbps(on ? Math.Max(1, (long)SpeedLimiterSlider.Value) : 0);
    }

    void SpeedLimiterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_speedLimiterSyncing) return;
        var kbps = (long)Math.Round(e.NewValue);
        _speedLimiterSyncing = true;
        try { SpeedLimiterBox.Text = kbps.ToString(); }
        finally { _speedLimiterSyncing = false; }
        ApplySpeedLimitKbps(kbps);
    }

    void SpeedLimiterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_speedLimiterSyncing || !long.TryParse(SpeedLimiterBox.Text, out var kbps) || kbps < 0) return;
        _speedLimiterSyncing = true;
        try { SpeedLimiterSlider.Value = Math.Min(SpeedLimiterSlider.Maximum, kbps); }
        finally { _speedLimiterSyncing = false; }
        ApplySpeedLimitKbps(kbps);
    }

    void SpeedScope_Click(object sender, RoutedEventArgs e)
    {
        if (_speedLimiterSyncing) return;
        var scope = SpeedPerFile.IsChecked == true ? SpeedLimitScope.PerDownload : SpeedLimitScope.Combined;
        App.Settings.SpeedLimitScope = scope == SpeedLimitScope.PerDownload ? "per_file" : "combined";
        App.Manager.LimitScope = scope;
        UpdateSpeedLimiterCaption(App.Manager.GlobalLimitBytesPerSec / 1024);
    }

    /// <summary>Applies the limit at once to every current and future ordinary download. Torrents have their own separate
    /// bandwidth limits in Options > BitTorrent, the way a dedicated torrent client keeps them apart from anything else.</summary>
    void ApplySpeedLimitKbps(long kbps)
    {
        kbps = Math.Max(0, kbps);
        App.Settings.SpeedKbps = kbps;
        App.Manager.GlobalLimitBytesPerSec = kbps * 1024;
        UpdateSpeedLimiterCaption(kbps);
        if (App.Manager.Torrents != null)
        {
            SpeedLimiterTorrentNote.Visibility = Visibility.Visible;
            SpeedLimiterTorrentNote.Text = Loc.T("Torrents have their own limits in Options > BitTorrent.");
        }
    }

    void UpdateSpeedLimiterCaption(long kbps)
    {
        var amount = kbps >= 1024 ? $"{kbps / 1024.0:0.#} MB/s" : $"{kbps} KB/s";
        SpeedLimiterCaption.Text = kbps <= 0 ? Loc.T("Unlimited") : App.Manager.LimitScope == SpeedLimitScope.PerDownload ? amount + "/file" : amount;
        BtnSpeedLimiter.ToolTip = kbps <= 0
            ? Loc.T("Limit download speed")
            : App.Manager.LimitScope == SpeedLimitScope.PerDownload
                ? $"Each download is limited to {amount} — click to change"
                : $"All downloads combined are limited to {amount} — click to change";
    }

    void Center_Click(object? sender, RoutedEventArgs? e)
    {
        try { new IntelligentCenterWindow { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ReportError("The Intelligent Center", ex); }
        UpdateStatusPills();
    }

    /// <summary>The two pills in the header show real state: smart connections on/off and whether any browser is connected to Makan.</summary>
    void UpdateStatusPills()
    {
        try
        {
            var smart = App.Settings.AdaptiveConnections && App.Settings.SmartDownloads;
            SmartText.Text = Loc.T(smart ? "SMART ENGINE ON" : "SMART ENGINE OFF");
            SmartDot.SetResourceReference(Border.BackgroundProperty, smart ? "Success" : "Faint");
            var browser = WindowsIntegration.Browsers().Any(b => b.Registered);
            BrowserText.Text = Loc.T(browser ? "BROWSER CONNECTED" : "BROWSER NOT CONNECTED");
            BrowserDot.SetResourceReference(Border.BackgroundProperty, browser ? "Success" : "Warning");
        }
        catch (Exception) { /* cosmetic */ }
    }

    void Scheduler_Click(object? sender, RoutedEventArgs? e)
    {
        if (_scheduler is { IsLoaded: true }) { _scheduler.Activate(); return; }
        _scheduler = new SchedulerWindow { Owner = this };
        _scheduler.Show();
    }

    void StartMainQueue_Click(object? sender, RoutedEventArgs? e) => App.Queues.Start(App.Queues.Main.Id);
    void StopQueues_Click(object? sender, RoutedEventArgs? e) => App.Queues.StopAll();
    void StartQueue_Click(object sender, RoutedEventArgs e) => ShowQueueMenu((FrameworkElement)sender, start: true);
    void StopQueue_Click(object sender, RoutedEventArgs e) => ShowQueueMenu((FrameworkElement)sender, start: false);

    void ShowQueueMenu(FrameworkElement target, bool start)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var q in App.Queues.Queues)
        {
            if (App.Queues.IsRunning(q.Id) == start) continue;   // Start lists idle queues, Stop lists running ones
            var id = q.Id;
            var entry = new MenuItem { Header = Loc.F("{0}  ({1} files)", q.Name, q.ItemIds.Count) };
            entry.Click += (_, _) => { if (start) App.Queues.Start(id); else App.Queues.Stop(id); UpdateToolbar(); };
            menu.Items.Add(entry);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T(start ? "Every queue is already running" : "No queue is running"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var scheduler = new MenuItem { Header = Loc.T("Scheduler…") };
        scheduler.Click += (_, _) => Scheduler_Click(this, null);
        menu.Items.Add(scheduler);
        menu.IsOpen = true;
    }

    void Downloads_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var one = SelectedItems().Count() == 1 ? SelectedItems().Single() : null;
        PlayPartialMenuItem.Visibility = one != null && DownloadManager.LooksStreamable(one) && (one.Status == nameof(DownloadStatus.Downloading) || one.Status == nameof(DownloadStatus.Paused)) ? Visibility.Visible : Visibility.Collapsed;
        ForceDownloadMenuItem.Visibility = one != null && DownloadManager.IsTorrentUrl(one.Url) ? Visibility.Visible : Visibility.Collapsed;
        ForceDownloadMenuItem.IsChecked = one != null && App.Manager.IsForced(one);

        if (Downloads.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Tag as string == "queue") is not { } parent) return;
        parent.Items.Clear();
        foreach (var q in App.Queues.Queues)
        {
            var id = q.Id;
            var entry = new MenuItem { Header = q.Name };
            entry.Click += (_, _) =>
            {
                foreach (var item in SelectedItems().Where(x => x.Status != nameof(DownloadStatus.Complete)))
                {
                    if (item.Status == nameof(DownloadStatus.Queued)) App.Manager.Pause(item);   // it waits for its queue now
                    App.Queues.AddItem(id, item);
                }
            };
            parent.Items.Add(entry);
        }
        if (SelectedItems().Any(x => x.QueueName != null))
        {
            parent.Items.Add(new Separator());
            var remove = new MenuItem { Header = Loc.T("Remove from queue") };
            remove.Click += (_, _) => { foreach (var item in SelectedItems()) App.Queues.RemoveItem(item); };
            parent.Items.Add(remove);
        }
    }

    void Retry_Click(object? sender, RoutedEventArgs? e) { foreach (var item in SelectedItems()) App.Manager.Retry(item); }

    void PriorityUp_Click(object? sender, RoutedEventArgs? e) => ChangePriority(+1);
    void PriorityDown_Click(object? sender, RoutedEventArgs? e) => ChangePriority(-1);
    void ChangePriority(int delta)
    {
        foreach (var item in SelectedItems()) { item.Priority = Math.Clamp(item.Priority + delta, 0, 10); App.Db.Save(item); }
        Footer.Text = Loc.T("Priority changed");
    }

    async void Remove_Click(object? sender, RoutedEventArgs? e)
    {
        var selected = SelectedItems().ToList();
        if (selected.Count == 0) return;
        var anyDone = selected.Any(x => x.Status == nameof(DownloadStatus.Complete) && File.Exists(x.FilePath));
        var deleteFile = false;
        if (anyDone)
        {
            var answer = Dlg.Show(this, "Also delete the downloaded file(s) from disk?\n\nYes = delete file and remove from list\nNo = remove from list only", "Delete", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            deleteFile = answer == MessageBoxResult.Yes;
        }
        foreach (var item in selected) await App.Manager.RemoveAsync(item, deleteFile);
    }

    async void DeleteCompleted_Click(object? sender, RoutedEventArgs? e)
    {
        var done = _items.Where(x => x.Status == nameof(DownloadStatus.Complete)).ToList();
        if (done.Count == 0) return;
        if (Dlg.Show(this, $"Remove {done.Count} finished download(s) from the list?\nThe files on disk are kept.", "Delete Completed", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var item in done) await App.Manager.RemoveAsync(item, false);
    }

    void Open_Click(object? sender, RoutedEventArgs? e)
    {
        if (Selected() is not { } item) return;
        if (DownloadManager.IsTorrentUrl(item.Url) && item.Status != nameof(DownloadStatus.Complete))
        {
            TorrentWindow.ShowFor(item, this);
            return;
        }
        if (File.Exists(item.FilePath))
            try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); } catch (Exception ex) { Footer.Text = ex.Message; }
        else if (Directory.Exists(item.FilePath))
            try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); } catch (Exception ex) { Footer.Text = ex.Message; }
        else if (DownloadManager.IsTorrentUrl(item.Url))
            TorrentWindow.ShowFor(item, this);
        else if (item.Status != nameof(DownloadStatus.Complete) && item.Status != nameof(DownloadStatus.Cancelled))
            DownloadProgressWindow.ShowFor(item);
    }

    /// <summary>Opens whatever has downloaded so far in the default player. Only the portion known to be gap-free from
    /// the start of the file is used, copied to a small temporary file first - the real (pre-allocated, still partly
    /// empty) file on disk is not safe to open directly, since a player could seek past what has actually arrived.</summary>
    void PlayPartial_Click(object? sender, RoutedEventArgs? e)
    {
        if (Selected() is not { } item) return;
        var copy = App.Manager.PreparePlayableCopy(item);
        if (copy == null) { Footer.Text = Loc.T("Not enough of this file has downloaded yet to play it."); return; }
        try { Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true }); }
        catch (Exception ex) { Footer.Text = ex.Message; }
    }

    /// <summary>Force Download (uTorrent/qBittorrent's "Force Start" by another name): keeps this one torrent running
    /// regardless of the download/seed schedule, until toggled off again.</summary>
    void ForceDownload_Click(object? sender, RoutedEventArgs? e)
    {
        if (Selected() is not { } item || !DownloadManager.IsTorrentUrl(item.Url)) return;
        App.Manager.SetForced(item, !App.Manager.IsForced(item));
    }

    /// <summary>Finished file: open it. A torrent: its details window (peers, files, trackers), even once it says Complete since it may still be seeding.
    /// Anything else: the progress window (like IDM). Never starts anything by accident.</summary>
    void Downloads_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected() is not { } item) return;
        if (DownloadManager.IsTorrentUrl(item.Url)) { TorrentWindow.ShowFor(item, this); return; }
        if (item.Status != nameof(DownloadStatus.Complete) && item.Status != nameof(DownloadStatus.Cancelled)) DownloadProgressWindow.ShowFor(item);
        else Open_Click(this, null);
    }

    void OpenFolder_Click(object? sender, RoutedEventArgs? e)
    {
        if (Selected() is not { } item) return;
        if (File.Exists(item.FilePath)) { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true }); return; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(item.FilePath));
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    void Downloads_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var row = Ancestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not DownloadItem item) return;
        Downloads.SelectedItem = item;
        OpenFolder_Click(this, null);
        e.Handled = true;
    }

    void CopyUrl_Click(object? sender, RoutedEventArgs? e)
    {
        if (Selected() is not { } item) return;
        var text = item.Url.Split('#')[0];
        try { Clipboard.SetText(text); _lastClipboard = text; } catch (Exception) { /* clipboard busy */ }
    }
    void Media_Click(object sender, RoutedEventArgs e) => new MediaWindow { Owner = this }.ShowDialog();
    /// <summary>Applies a built-in theme selected from the More menu.</summary>
    void SetTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string theme }) return;
        App.Settings.Theme = theme;
        ThemeManager.Apply(App.Settings.Theme);
        UpdateThemeGlyph();
    }

    void UpdateThemeGlyph()
    {
        ThemeObsidianGoldCheck.Visibility = ThemeManager.Current == "obsidian-gold" ? Visibility.Visible : Visibility.Collapsed;
        ThemePlatinumBlueCheck.Visibility = ThemeManager.Current == "platinum-blue" ? Visibility.Visible : Visibility.Collapsed;
        ThemeRoyalAmethystCheck.Visibility = ThemeManager.Current == "royal-amethyst" ? Visibility.Visible : Visibility.Collapsed;
        ThemeEmeraldExecutiveCheck.Visibility = ThemeManager.Current == "emerald-executive" ? Visibility.Visible : Visibility.Collapsed;
        ThemeChampagneMinimalCheck.Visibility = ThemeManager.Current == "champagne-minimal" ? Visibility.Visible : Visibility.Collapsed;
        ThemeGraphiteCopperCheck.Visibility = ThemeManager.Current == "graphite-copper" ? Visibility.Visible : Visibility.Collapsed;
        ThemeSapphireNoirCheck.Visibility = ThemeManager.Current == "sapphire-noir" ? Visibility.Visible : Visibility.Collapsed;
        ThemeIvoryLuxeCheck.Visibility = ThemeManager.Current == "ivory-luxe" ? Visibility.Visible : Visibility.Collapsed;
        ThemeRoseTitaniumCheck.Visibility = ThemeManager.Current == "rose-titanium" ? Visibility.Visible : Visibility.Collapsed;
        ThemeArcticGlassCheck.Visibility = ThemeManager.Current == "arctic-glass" ? Visibility.Visible : Visibility.Collapsed;
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { new SettingsWindow { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ReportError("The Options window", ex); }
        UpdateStatusPills();
    }
    void History_Click(object sender, RoutedEventArgs e) => new HistoryWindow { Owner = this }.ShowDialog();

    void GettingStarted_Click(object sender, RoutedEventArgs e) => new FirstRunWindow(this).Show();

    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this).ShowDialog();

    void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = new DiagnosticsService().ExportReport(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakanDownloadManager", "downloads.db"));
            Footer.Text = Loc.T("Diagnostic report: " + path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Footer.Text = ex.Message; }
    }

    // Windows Explorer-style rubber-band selection. Start on empty list space and drag across rows; Ctrl/Shift keeps
    // the existing selection. When the pointer reaches an edge the list scrolls and keeps the rows already crossed.
    void Downloads_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        if (Ancestor<ListViewItem>(source) != null || Ancestor<GridViewColumnHeader>(source) != null || Ancestor<ScrollBar>(source) != null) return;

        _marqueeArmed = true;
        _marqueeDragging = false;
        _marqueeStart = _marqueeCurrent = e.GetPosition(Downloads);
        _marqueeBase.Clear();
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
            foreach (var item in Downloads.SelectedItems.Cast<object>()) _marqueeBase.Add(item);
        else
            Downloads.UnselectAll();
        Mouse.Capture(Downloads, CaptureMode.Element);
        e.Handled = true;
    }

    void Downloads_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueeArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
        _marqueeCurrent = e.GetPosition(Downloads);
        if (!_marqueeDragging)
        {
            if (Math.Abs(_marqueeCurrent.X - _marqueeStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(_marqueeCurrent.Y - _marqueeStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _marqueeDragging = true;
            SelectionRectangle.Visibility = Visibility.Visible;
            _marqueeScrollTimer.Start();
        }
        UpdateMarquee();
        e.Handled = true;
    }

    void Downloads_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_marqueeArmed || e.ChangedButton != MouseButton.Left) return;
        if (_marqueeDragging) UpdateMarquee();
        EndMarquee();
        e.Handled = true;
    }

    void Downloads_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_marqueeArmed) EndMarquee();
    }

    void UpdateMarquee()
    {
        var left = Math.Max(0, Math.Min(_marqueeStart.X, _marqueeCurrent.X));
        var top = Math.Max(0, Math.Min(_marqueeStart.Y, _marqueeCurrent.Y));
        var right = Math.Min(Downloads.ActualWidth, Math.Max(_marqueeStart.X, _marqueeCurrent.X));
        var bottom = Math.Min(Downloads.ActualHeight, Math.Max(_marqueeStart.Y, _marqueeCurrent.Y));
        var rectangle = new Rect(new Point(left, top), new Point(Math.Max(left, right), Math.Max(top, bottom)));
        Canvas.SetLeft(SelectionRectangle, rectangle.Left);
        Canvas.SetTop(SelectionRectangle, rectangle.Top);
        SelectionRectangle.Width = rectangle.Width;
        SelectionRectangle.Height = rectangle.Height;

        var wanted = new HashSet<object>(_marqueeBase);
        foreach (var item in Downloads.Items.Cast<object>())
        {
            if (Downloads.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem row || !row.IsVisible) continue;
            var origin = row.TranslatePoint(new Point(), Downloads);
            if (rectangle.IntersectsWith(new Rect(origin, new Size(row.ActualWidth, row.ActualHeight)))) wanted.Add(item);
        }
        foreach (var item in Downloads.Items.Cast<object>())
        {
            var selected = Downloads.SelectedItems.Contains(item);
            if (wanted.Contains(item) && !selected) Downloads.SelectedItems.Add(item);
            else if (!wanted.Contains(item) && selected) Downloads.SelectedItems.Remove(item);
        }
    }

    void MarqueeScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_marqueeDragging) return;
        _marqueeCurrent = Mouse.GetPosition(Downloads);
        var scroll = VisualChild<ScrollViewer>(Downloads);
        if (scroll == null) return;
        var direction = _marqueeCurrent.Y < 34 ? -1 : _marqueeCurrent.Y > Downloads.ActualHeight - 20 ? 1 : 0;
        if (direction == 0) return;

        // Rows already crossed remain selected as the viewport moves, matching Explorer's long-list selection.
        foreach (var item in Downloads.SelectedItems.Cast<object>()) _marqueeBase.Add(item);
        if (direction < 0) scroll.LineUp(); else scroll.LineDown();
        Downloads.UpdateLayout();
        UpdateMarquee();
    }

    void EndMarquee()
    {
        _marqueeArmed = false;
        _marqueeDragging = false;
        _marqueeScrollTimer.Stop();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        if (Mouse.Captured == Downloads) Mouse.Capture(null);
    }

    static T? Ancestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T found) return found;
            try { current = VisualTreeHelper.GetParent(current); }
            catch (InvalidOperationException) { current = LogicalTreeHelper.GetParent(current); }
        }
        return null;
    }

    static T? VisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (VisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    void Downloads_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateToolbar();

    void UpdateToolbar()
    {
        var selected = SelectedItems().ToList();
        BtnResume.IsEnabled = selected.Any(CanResume);
        BtnStop.IsEnabled = selected.Any(IsRunning);
        BtnDelete.IsEnabled = selected.Count > 0;
        BtnStopAll.IsEnabled = _items.Any(IsRunning);
        BtnStopAllTop.IsEnabled = _items.Any(IsRunning);
        BtnStartAllTop.IsEnabled = _items.Any(CanResume) || App.Queues.Queues.Any(q => !App.Queues.IsRunning(q.Id) && q.ItemIds.Count > 0);
        BtnDeleteCompleted.IsEnabled = _items.Any(x => x.Status == nameof(DownloadStatus.Complete));
    }

    // ---------------------------------------------------------------- filtering

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    bool Matches(object o)
    {
        if (o is not DownloadItem x) return false;
        var q = SearchBox?.Text?.Trim() ?? "";
        if (q.Length > 0 &&
            !x.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !x.Url.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !x.CategoryName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !x.StatusText.Contains(q, StringComparison.OrdinalIgnoreCase) &&
            !(x.QueueName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        var tag = (Tree?.SelectedItem as TreeViewItem)?.Tag as string ?? "all";
        return tag switch
        {
            "all" => true,
            "unfinished" => x.Status != nameof(DownloadStatus.Complete),
            "finished" => x.Status == nameof(DownloadStatus.Complete),
            "torrents" => DownloadManager.IsTorrentUrl(x.Url),
            "queues" => x.QueueName != null,
            _ when tag.StartsWith("queue:", StringComparison.Ordinal) => int.TryParse(tag[6..], out var queueId) && App.Queues.QueueOf(x.Id)?.Id == queueId,
            _ when tag.StartsWith("cat:", StringComparison.Ordinal) => x.CategoryName == tag[4..],
            _ => true
        };
    }

    // ---------------------------------------------------------------- status bar

    void RefreshDashboard()
    {
        long total = 0; var active = 0; var signature = 17;
        foreach (var item in _items)
        {
            signature = HashCode.Combine(signature, item.Id, item.Status, item.FilePath);
            if (item.Status == nameof(DownloadStatus.Downloading)) { active++; total += item.SpeedBytesPerSec; }
        }
        // Statuses change on background threads; re-evaluate the filter and buttons only when something actually changed.
        if (signature != _statusSignature) { _statusSignature = signature; _view.Refresh(); UpdateToolbar(); UpdateCounts(); }

        TotalSpeed.Text = $"{Format(total)}/s";
        ActiveText.Text = $"{active} active · {_items.Count} in list";

        long upload = 0;
        foreach (var item in _items) if (App.Manager.SessionOf(item) is { } session) upload += session.UploadRate;
        UpdateTrayIcon(total, upload);
    }

    // ---------------------------------------------------------------- helpers

    static bool LooksLikeUrl(string value) => (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https" or "magnet" or "file")) || DownloadManager.IsTorrentUrl(value);
    static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Every http(s) link in the text, one per line or whitespace-separated, without duplicates.</summary>
    static List<string> ExtractUrls(string text) =>
        text.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().Trim('<', '>', '"', '\'', ','))
            .Where(LooksLikeUrl).Distinct(StringComparer.Ordinal).Take(500).ToList();

    static string GuessFileName(string url)
    {
        try { var name = Path.GetFileName(new Uri(url).LocalPath); return string.IsNullOrWhiteSpace(name) ? "download.bin" : Uri.UnescapeDataString(name); }
        catch { return "download.bin"; }
    }

    static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes); var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.##} {units[i]}";
    }
}
