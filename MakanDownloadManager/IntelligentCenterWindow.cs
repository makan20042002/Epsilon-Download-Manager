using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>
/// The Intelligent Center: what Makan has learned about servers, one-click bandwidth profiles, health checks, statistics, crash recovery,
/// the smart planner, the download basket and the database tools. Presentation only: the engine stays the source of truth.
/// All colours are theme resources, so the window follows the light / dark theme.
/// </summary>
public sealed class IntelligentCenterWindow : Window
{
    readonly TabControl _tabs = new();
    readonly StackPanel _servers = Column();
    readonly StackPanel _bandwidth = Column();
    readonly StackPanel _health = Column();
    readonly StackPanel _recovery = Column();
    readonly StackPanel _breakdown = Column();
    readonly Dictionary<string, TextBlock> _live = new();
    readonly ListBox _basket = new();
    readonly TextBox _basketInput = new();
    readonly TextBox _plannerUrl = new();
    readonly ComboBox _plannerMode = new();
    readonly TextBlock _plannerResult = new();
    readonly TextBlock _securityText = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public IntelligentCenterWindow()
    {
        Title = Loc.T("Epsilon Download Manager — Intelligent Center");
        Width = 1080; Height = 740; MinWidth = 860; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        Content = BuildRoot();
        RefreshAll();
        _timer.Tick += (_, _) => RefreshLive();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    // ---------------------------------------------------------------- small building blocks

    static StackPanel Column() => new() { Margin = new Thickness(4) };

    static TextBlock Label(string text, double size = 12, bool bold = false, string brush = "Text", double top = 0, double bottom = 0, bool wrap = false)
    {
        var block = new TextBlock { Text = Loc.T(text), FontSize = size, Margin = new Thickness(0, top, 0, bottom) };
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    Border Card(UIElement child, double width = double.NaN, Thickness? margin = null)
    {
        var card = new Border { Padding = new Thickness(14), Margin = margin ?? new Thickness(0, 0, 0, 8), Child = child };
        if (!double.IsNaN(width)) card.Width = width;
        if (TryFindResource("CardBorder") is Style cardStyle) card.Style = cardStyle;
        return card;
    }

    static Button MakeButton(string text, RoutedEventHandler onClick, bool primary = false)
    {
        var button = new Button { Content = Loc.T(text), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += onClick;
        if (primary && Application.Current.TryFindResource("PrimaryButton") is Style primaryStyle) button.Style = primaryStyle;
        return button;
    }

    static TabItem MakeTab(string title, UIElement content) => new()
    {
        Header = Loc.T(title),
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
            Padding = new Thickness(8)
        }
    };

    static void Section(Panel panel, string title, string subtitle)
    {
        panel.Children.Add(Label(title, 18, true));
        panel.Children.Add(Label(subtitle, 11, false, "Muted", 4, 12, true));
    }

    void Metric(Panel panel, string key, string label, string detail)
    {
        var value = new TextBlock { Text = "—", FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 3, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _live[key] = value;
        var stack = new StackPanel();
        stack.Children.Add(Label(label, 9, true, "Muted"));
        stack.Children.Add(value);
        stack.Children.Add(Label(detail, 10, false, "Muted", 0, 0, true));
        panel.Children.Add(Card(stack, 158, new Thickness(0, 0, 9, 9)));
    }

    void InfoCard(Panel panel, string title, string body)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(title, 13, true));
        stack.Children.Add(Label(body, 11, false, "Muted", 5, 0, true));
        panel.Children.Add(Card(stack, 250, new Thickness(0, 0, 10, 10)));
    }

    void EmptyState(Panel panel, string title, string body)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(title, 14, true));
        stack.Children.Add(Label(body, 12, false, "Muted", 4, 0, true));
        panel.Children.Add(Card(stack));
    }

    void SetLive(string key, string value)
    {
        if (_live.TryGetValue(key, out var block)) block.Text = value;
    }

    // ---------------------------------------------------------------- layout

    UIElement BuildRoot()
    {
        var root = new DockPanel { Margin = new Thickness(20) };
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _tabs.Items.Add(MakeTab("Overview", BuildOverview()));
        _tabs.Items.Add(MakeTab("Server Intelligence", _servers));
        _tabs.Items.Add(MakeTab("Bandwidth", _bandwidth));
        _tabs.Items.Add(MakeTab("Health Center", _health));
        _tabs.Items.Add(MakeTab("Statistics", BuildStatistics()));
        _tabs.Items.Add(MakeTab("Recovery", _recovery));
        _tabs.Items.Add(MakeTab("Planner", BuildPlanner()));
        _tabs.Items.Add(MakeTab("Basket", BuildBasket()));
        _tabs.Items.Add(MakeTab("Security & Backup", BuildSecurity()));
        root.Children.Add(_tabs);
        return root;
    }

    UIElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(Label("INTELLIGENT CENTER", 24, true));
        title.Children.Add(Label("Server learning · bandwidth profiles · health · recovery · backup", 12, false, "Muted", 3, 0));
        grid.Children.Add(title);

        var refresh = MakeButton("Refresh", (_, _) => RefreshAll());
        Grid.SetColumn(refresh, 1);
        refresh.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(refresh);
        return grid;
    }

    UIElement BuildOverview()
    {
        var panel = Column();
        Section(panel, "Overview", "Live numbers from your download list and the engine.");
        var metrics = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        Metric(metrics, "active", "ACTIVE", "Transferring now");
        Metric(metrics, "queued", "QUEUED", "Waiting for a free slot");
        Metric(metrics, "completed", "COMPLETED", "Finished downloads");
        Metric(metrics, "downloaded", "DOWNLOADED", "All data received so far");
        Metric(metrics, "fastest", "FASTEST", "Best transfer right now");
        panel.Children.Add(metrics);

        var cards = new WrapPanel();
        InfoCard(cards, "Server learning", "Makan remembers which servers throttle you and starts them with fewer connections next time. Nothing leaves your computer.");
        InfoCard(cards, "Bandwidth profiles", "Switch the global speed limit in one click, for example while gaming or during a call.");
        InfoCard(cards, "Health and recovery", "Checks the database, disk space, browser link and tools, and lists downloads that were interrupted.");
        InfoCard(cards, "Private by design", "Browser cookies are stored encrypted for your Windows account; diagnostic reports hide cookies and tokens.");
        panel.Children.Add(cards);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(MakeButton("Export diagnostic report", (_, _) => ExportDiagnostics(), true));
        actions.Children.Add(MakeButton("Open Options", (_, _) => OpenOptions()));
        panel.Children.Add(actions);
        return panel;
    }

    UIElement BuildStatistics()
    {
        var panel = Column();
        Section(panel, "Statistics", "Worked out from the current download list; no account and no upload.");
        var metrics = new WrapPanel();
        Metric(metrics, "s_total", "TOTAL", "Downloads in the list");
        Metric(metrics, "s_active", "ACTIVE", "Transferring now");
        Metric(metrics, "s_failed", "FAILED", "Need attention");
        Metric(metrics, "s_paused", "STOPPED", "Waiting to be resumed");
        Metric(metrics, "s_retries", "RETRIES", "Automatic retries so far");
        Metric(metrics, "s_rate", "AVERAGE SPEED", "Of the running downloads");
        Metric(metrics, "s_data", "FINISHED DATA", "Size of completed downloads");
        panel.Children.Add(metrics);
        panel.Children.Add(Label("By category", 14, true, "Text", 8, 6));
        panel.Children.Add(_breakdown);
        return panel;
    }

    UIElement BuildPlanner()
    {
        var panel = Column();
        Section(panel, "Smart download planner", "Paste an address to preview where Makan would save it and how many connections it would use. Nothing is downloaded.");
        _plannerUrl.Padding = new Thickness(8);
        _plannerUrl.MinWidth = 600;
        _plannerUrl.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_plannerUrl);

        _plannerMode.ItemsSource = Enum.GetValues<DownloadPerformanceMode>();
        _plannerMode.SelectedItem = DownloadPerformanceMode.Balanced;
        _plannerMode.Width = 220;
        _plannerMode.HorizontalAlignment = HorizontalAlignment.Left;
        _plannerMode.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(_plannerMode);

        var analyze = MakeButton("Analyze address", (_, _) => Analyze(), true);
        analyze.HorizontalAlignment = HorizontalAlignment.Left;
        analyze.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(analyze);

        _plannerResult.TextWrapping = TextWrapping.Wrap;
        _plannerResult.Margin = new Thickness(0, 14, 0, 0);
        _plannerResult.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        panel.Children.Add(_plannerResult);
        return panel;
    }

    UIElement BuildBasket()
    {
        var panel = Column();
        Section(panel, "Download basket", "Collect addresses now and download them together later. The basket is kept between runs.");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _basketInput.MinWidth = 480;
        _basketInput.Padding = new Thickness(8);
        _basketInput.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(_basketInput);
        row.Children.Add(MakeButton("Add", (_, _) => AddToBasket()));
        row.Children.Add(MakeButton("Download all", (_, _) => DownloadBasket(), true));
        row.Children.Add(MakeButton("Export…", (_, _) => ExportBasket()));
        row.Children.Add(MakeButton("Clear", (_, _) => { App.Basket.Clear(); SaveBasket(); RefreshBasket(); }));
        panel.Children.Add(row);
        _basket.Height = 360;
        panel.Children.Add(_basket);
        return panel;
    }

    UIElement BuildSecurity()
    {
        var panel = Column();
        Section(panel, "Security and backup", "Your download list lives in a local SQLite database. Makan keeps a backup copy that is refreshed every time it starts.");
        _securityText.TextWrapping = TextWrapping.Wrap;
        _securityText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        panel.Children.Add(_securityText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(MakeButton("Check database", (_, _) => CheckDatabase()));
        actions.Children.Add(MakeButton("Create backup…", (_, _) => BackupDatabase(), true));
        actions.Children.Add(MakeButton("Open data folder", (_, _) => OpenDataFolder()));
        panel.Children.Add(actions);
        panel.Children.Add(Label("If the database is ever damaged, Makan restores its automatic backup copy by itself the next time it starts.", 11, false, "Muted", 12, 0, true));
        return panel;
    }

    // ---------------------------------------------------------------- refreshing

    void RefreshAll()
    {
        RefreshServers();
        RefreshBandwidth();
        RefreshHealth();
        RefreshRecovery();
        RefreshBasket();
        RefreshSecurity();
        RefreshLive();
    }

    void RefreshLive()
    {
        var s = App.V15Statistics.GetSnapshot();
        SetLive("active", s.Active.ToString());
        SetLive("queued", s.Queued.ToString());
        SetLive("completed", s.Completed.ToString());
        SetLive("downloaded", DownloadItem.FormatBytes(s.DownloadedBytes));
        SetLive("fastest", DownloadItem.FormatBytes(s.FastestBytesPerSec) + "/s");
        SetLive("s_total", s.Total.ToString());
        SetLive("s_active", s.Active.ToString());
        SetLive("s_failed", s.Failed.ToString());
        SetLive("s_paused", s.Paused.ToString());
        SetLive("s_retries", s.Retries.ToString());
        SetLive("s_rate", $"{s.AverageMbps:0.0} Mbps");
        SetLive("s_data", DownloadItem.FormatBytes(s.CompletedBytes));

        _breakdown.Children.Clear();
        var byCategory = StatisticsSnapshotBuilder.Build(App.Manager.Items).ByCategory.OrderByDescending(x => x.Value).ToList();
        if (byCategory.Count == 0) _breakdown.Children.Add(Label("No downloads yet.", 12, false, "Muted"));
        foreach (var pair in byCategory) _breakdown.Children.Add(Label($"{Loc.T(pair.Key)}   ·   {pair.Value}", 12, false, "Text", 0, 3));
    }

    void RefreshServers()
    {
        _servers.Children.Clear();
        Section(_servers, "Server intelligence", "What Makan has learned from your finished downloads. A server that keeps refusing connections gets a lower limit.");
        var servers = App.Intelligence.Servers();
        if (servers.Count == 0)
        {
            EmptyState(_servers, "Nothing learned yet", "Finish a few downloads and Makan builds a history for each server.");
            return;
        }
        foreach (var server in servers)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(Label(server.Host, 14, true));
            var limit = server.Throttles == 0 && server.Connections >= SmartDownloadController.NoLimit
                ? Loc.T("no connection limit learned")
                : Loc.F("up to {0} connections", server.Connections);
            left.Children.Add(Label($"{limit}   ·   {server.Samples} " + Loc.T("downloads measured") + $"   ·   {server.Throttles} " + Loc.T("throttling events"), 11, false, "Muted", 4, 0, true));
            grid.Children.Add(left);
            var speed = new TextBlock { Text = $"{server.AverageMbps:0.0} Mbps", FontSize = 16, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            speed.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            Grid.SetColumn(speed, 1);
            grid.Children.Add(speed);
            _servers.Children.Add(Card(grid));
        }
    }

    void RefreshBandwidth()
    {
        _bandwidth.Children.Clear();
        Section(_bandwidth, "Bandwidth profiles", "Set the global speed limit in one click. It applies to all downloads at once; a limit on one download still wins.");
        var current = App.Settings.SpeedKbps;
        _bandwidth.Children.Add(Label(current <= 0 ? Loc.T("Current global limit: unlimited") : Loc.F("Current global limit: {0} KB/s", current), 12, true, "Accent", 0, 10));
        var active = App.BandwidthProfiles.ActiveName(current);
        var wrap = new WrapPanel();
        foreach (var profile in App.BandwidthProfiles.Load())
        {
            var stack = new StackPanel();
            stack.Children.Add(Label(profile.Name.ToUpperInvariant(), 13, true));
            stack.Children.Add(Label(profile.LimitBytesPerSec <= 0 ? Loc.T("Unlimited") : DownloadItem.FormatBytes(profile.LimitBytesPerSec) + "/s", 21, true, "Accent", 5, 2));
            stack.Children.Add(Label(profile.Description, 11, false, "Muted", 0, 0, true));
            var isActive = string.Equals(profile.Name, active, StringComparison.OrdinalIgnoreCase);
            var name = profile.Name;
            var apply = MakeButton(isActive ? "Active" : "Apply profile", (_, _) => { App.BandwidthProfiles.Apply(name); RefreshBandwidth(); });
            apply.IsEnabled = !isActive;
            apply.Margin = new Thickness(0, 12, 0, 0);
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(apply);
            wrap.Children.Add(Card(stack, 235, new Thickness(0, 0, 10, 10)));
        }
        _bandwidth.Children.Add(wrap);
    }

    void RefreshHealth()
    {
        _health.Children.Clear();
        Section(_health, "Health Center", "Quick checks of the database, disk, network, browser link and optional tools.");
        foreach (var check in App.Health.Run())
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock { Text = check.Healthy ? "✓" : check.Optional ? "i" : "!", FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, check.Healthy ? "Success" : check.Optional ? "Muted" : "Warning");
            grid.Children.Add(glyph);

            var text = new StackPanel();
            text.Children.Add(Label(check.Name, 13, true));
            var detail = new TextBlock { Text = check.Detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(detail);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var badgeText = check.Healthy ? "READY" : check.Optional ? "OPTIONAL" : "CHECK";
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Label(badgeText, 9, true) };
            badge.SetResourceReference(Border.BackgroundProperty, check.Healthy ? "SuccessBg" : "Hover");
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
            _health.Children.Add(Card(grid));
        }
    }

    void RefreshRecovery()
    {
        _recovery.Children.Clear();
        Section(_recovery, "Crash recovery", "Unfinished downloads that Makan found state files for. They continue from the saved position when you resume them.");
        var interrupted = V15RecoveryService.Scan(App.Manager.Items);
        if (interrupted.Count == 0)
        {
            EmptyState(_recovery, "Nothing waiting for recovery", "No interrupted download was found.");
            return;
        }
        foreach (var item in interrupted)
        {
            var progress = item.TotalBytes is > 0 ? $"{100d * item.DoneBytes / item.TotalBytes.Value:0.0}%" : DownloadItem.FormatBytes(item.DoneBytes);
            var text = new TextBlock { Text = $"{Path.GetFileName(item.Path)}   ·   {progress}   ·   {item.UpdatedUtc.ToLocalTime():g}", TextTrimming = TextTrimming.CharacterEllipsis };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            _recovery.Children.Add(Card(text));
        }
    }

    void RefreshBasket()
    {
        _basket.ItemsSource = null;
        _basket.ItemsSource = App.Basket.Items;
    }

    void RefreshSecurity()
    {
        var net = App.NetworkProfile.GetSnapshot();
        _securityText.Text = string.Join("\n", new[]
        {
            Loc.T("Database") + ": " + App.Db.DatabasePath,
            Loc.T("Automatic backup copy") + ": " + App.Db.BackupPath,
            Loc.T("Portable mode") + ": " + Loc.T(PortableModeService.IsPortable ? "on" : "off"),
            Loc.T("Network") + ": " + Loc.T(net.Connected ? "connected" : "offline") + (net.Interfaces.Length > 0 ? "  ·  " + net.Interfaces : ""),
            Loc.T("Fastest link") + ": " + DownloadItem.FormatBytes(net.FastestLinkBitsPerSecond / 8) + "/s",
            "",
            Loc.T("Browser cookies are stored encrypted for your Windows account. Redirects never share cookies between sites. Diagnostic reports hide cookies and tokens.")
        });
    }

    // ---------------------------------------------------------------- actions

    void Analyze()
    {
        var url = _plannerUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _plannerResult.Text = Loc.T("Enter a full http:// or https:// address.");
            return;
        }
        var mode = _plannerMode.SelectedItem is DownloadPerformanceMode chosen ? chosen : DownloadPerformanceMode.Balanced;
        var plan = new SmartDownloadAnalyzer().Plan(url, null, App.Settings.DefaultFolder, App.Manager.DefaultConnections, mode);
        var connections = App.SmartDownloads.Suggest(uri, plan.Connections);
        _plannerResult.Text = string.Join("\n", new[]
        {
            Loc.T("File name") + ": " + plan.SuggestedFileName,
            Loc.T("Category") + ": " + Loc.T(plan.Category),
            Loc.T("Folder") + ": " + plan.Folder,
            Loc.T("Connections") + ": " + connections + (connections < plan.Connections ? "  (" + Loc.T("lowered: this server has throttled before") + ")" : ""),
            "",
            App.Intelligence.Recommend(new DownloadItem { Url = url })
        });
    }

    void SaveBasket()
    {
        try { App.Db.Set("basket_json", App.Basket.ExportJson()); }
        catch (Exception ex) { new DiagnosticsService().Error("Saving the download basket failed", ex); }
    }

    void AddToBasket()
    {
        var text = _basketInput.Text.Trim();
        if (text.Length == 0) return;
        var before = App.Basket.Items.Count;
        App.Basket.Add(text);
        if (App.Basket.Items.Count == before) Dlg.Show(this, "Enter a full http:// or https:// address that is not in the basket yet.", "Basket", MessageBoxButton.OK, MessageBoxImage.Information);
        _basketInput.Clear();
        SaveBasket();
        RefreshBasket();
    }

    void DownloadBasket()
    {
        var urls = App.Basket.Items.ToList();
        if (urls.Count == 0) return;
        var skipped = 0;
        foreach (var url in urls)
        {
            if (YtDlpService.IsYouTubeUrl(url)) { skipped++; continue; }      // these need a quality choice: add them from the main window
            try
            {
                var plan = new SmartDownloadAnalyzer().Plan(url, null, App.Settings.DefaultFolder, App.Manager.DefaultConnections);
                App.Manager.Enqueue(new DownloadItem { Url = url, FilePath = Path.Combine(plan.Folder, plan.SuggestedFileName ?? "download.bin"), Connections = plan.Connections, Category = plan.Category });
                App.Basket.Remove(url);
            }
            catch (Exception ex)
            {
                new DiagnosticsService().Error("Adding a basket address failed", ex);
                Dlg.Show(this, $"Could not add {url}:\n{ex.Message}", "Basket", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        SaveBasket();
        RefreshBasket();
        if (skipped > 0) Dlg.Show(this, Loc.F("{0} YouTube link(s) stay in the basket: add them from the main window to choose a quality.", skipped), "Basket", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void ExportBasket()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "makan-download-basket.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllLines(dialog.FileName, App.Basket.Items); }
        catch (Exception ex) { Dlg.Show(this, ex.Message, "Basket", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void CheckDatabase()
    {
        var ok = App.Db.IntegrityCheck();
        Dlg.Show(this, ok ? "The database passed SQLite's integrity check." : "SQLite reports a problem in the database. Create a backup now and restart Makan.", "Database", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    void BackupDatabase()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Makan database backup (*.db)|*.db", FileName = "makan-download-manager-backup.db" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            BackupService.ExportDatabase(App.Db, dialog.FileName);
            Dlg.Show(this, "The backup was created.", "Database", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Dlg.Show(this, Loc.F("The backup failed:\n{0}", ex.Message), "Database", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    static void OpenDataFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + PortableModeService.DataDirectory + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { new DiagnosticsService().Error("Opening the data folder failed", ex); }
    }

    void OpenOptions()
    {
        try { new SettingsWindow { Owner = this }.ShowDialog(); }
        catch (Exception ex) { new DiagnosticsService().Error("Opening the Options window failed", ex); }
        RefreshAll();
    }

    void ExportDiagnostics()
    {
        try
        {
            var path = V15DiagnosticReport.Export();
            Dlg.Show(this, Loc.F("The diagnostic report was saved to:\n\n{0}", path), "Diagnostic report", MessageBoxButton.OK, MessageBoxImage.Information);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception) { /* the message already names the file */ }
        }
        catch (Exception ex)
        {
            new DiagnosticsService().Error("Exporting the diagnostic report failed", ex);
            Dlg.Show(this, Loc.F("The diagnostic report could not be created:\n{0}", ex.Message), "Diagnostic report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
