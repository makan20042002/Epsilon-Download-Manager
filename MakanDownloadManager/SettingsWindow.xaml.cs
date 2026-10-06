using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;
using MakanDownloadManager.Services.Torrent;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

/// <summary>Options (like IDM's configuration window): General, File types, Save to, Downloads, Connection.</summary>
public partial class SettingsWindow : Window
{
    readonly List<CategoryDef> _cats;
    int _shownCategory = -1;
    bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        var s = App.Settings;
        _loading = true;

        // General
        ShowBrowserStatus();
        LaunchStartup.IsEnabled = !WindowsIntegration.IsPackaged;
        LaunchStartup.IsChecked = WindowsIntegration.LaunchOnStartup;
        ClipboardWatch.IsChecked = s.ClipboardWatch;
        var allowed = s.CaptureBrowsers.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        BrChrome.IsChecked = allowed.Contains("chrome"); BrEdge.IsChecked = allowed.Contains("edge"); BrFirefox.IsChecked = allowed.Contains("firefox");
        BrOpera.IsChecked = allowed.Contains("opera"); BrVivaldi.IsChecked = allowed.Contains("vivaldi"); BrOther.IsChecked = allowed.Contains("other");
        LanguageBox.SelectedIndex = s.Language == "fa" ? 1 : 0;
        ThemeBox.SelectedIndex = s.Theme switch { "obsidian-gold" => 0, "platinum-blue" => 1, "royal-amethyst" => 2, "emerald-executive" => 3, "champagne-minimal" => 4, "graphite-copper" => 5, "sapphire-noir" => 6, "ivory-luxe" => 7, "rose-titanium" => 8, "arctic-glass" => 9, "dracula" => 10, "makan-lab" => 11, "auto" => 12, _ => 1 };

        // File types
        FileTypesBox.Text = s.FileTypes; SitesBox.Text = s.ExcludedSites; AddressesBox.Text = s.ExcludedAddresses;

        // Save to
        _cats = CategoryService.Categories.ToList();
        CategoryCombo.ItemsSource = _cats.Select(c => c.Name).ToList();
        ChangeFolderLast.IsChecked = s.ChangeFolderOnLastSelected;
        BaseFolderBox.Text = s.DefaultFolder; TempFolderBox.Text = s.TempDirectory; SetFileDate.IsChecked = s.SetFileDateFromServer;
        TorrentFolderBox.Text = s.TorrentFolder;

        // Downloads
        AskBefore.IsChecked = s.AskBeforeDownload; OnlyQueue.IsChecked = s.OnlyAddToQueue; OnlyQueue.IsEnabled = s.AskBeforeDownload;
        ShowProgress.IsChecked = s.ShowProgressWindow; ShowWindowOnCapture.IsChecked = s.ShowWindowOnCapture; ShowComplete.IsChecked = s.ShowCompleteDialog; AskQueueLater.IsChecked = s.AskQueueOnLater; AskQueueBatch.IsChecked = s.AskQueueOnBatch;
        IgnoreModified.IsChecked = s.IgnoreModifiedOnResume;
        DuplicateBox.SelectedIndex = s.DuplicateAction switch { "suffix" => 1, "overwrite" => 2, "skip" => 3, _ => 0 };
        UserAgentBox.Text = s.UserAgent;
        AvOn.IsChecked = s.AntivirusEnabled; AvProgramBox.Text = s.AntivirusProgram; AvArgsBox.Text = s.AntivirusArguments;

        // Connection
        Slots.Value = s.MaxActive; SpeedLimitOn.IsChecked = s.SpeedKbps > 0; Speed.Text = (s.SpeedKbps > 0 ? s.SpeedKbps : 1024).ToString(CultureInfo.InvariantCulture);
        MultiNetOn.IsChecked = s.MultiNetwork; MultiNetStatus.Text = DescribeNetworks();
        SpeedScope.SelectedIndex = s.SpeedLimitScope == "per_file" ? 1 : 0; Connections.Value = s.Connections;
        AutoResume.IsChecked = s.AutoResume; KeepAwake.IsChecked = s.KeepAwakeWhileDownloading; Ffmpeg.Text = s.FfmpegPath;
        SmartConnections.IsChecked = s.AdaptiveConnections && s.SmartDownloads;
        BoostSpeed.IsChecked = s.BoostDownloadSpeed;

        // BitTorrent
        TorrentPortBox.Text = s.TorrentPort.ToString(CultureInfo.InvariantCulture);
        TorrentDht.IsChecked = s.TorrentDht; TorrentPex.IsChecked = s.TorrentPex;
        TorrentDownloadBox.Text = s.TorrentDownloadKbps.ToString(CultureInfo.InvariantCulture);
        TorrentUploadBox.Text = s.TorrentUploadKbps.ToString(CultureInfo.InvariantCulture);
        TorrentSlots.Value = s.TorrentUploadSlots; TorrentMaxPeersBox.Text = s.TorrentMaxPeers.ToString(CultureInfo.InvariantCulture);
        TorrentSeedAfter.IsChecked = s.TorrentSeedAfterCompletion; TorrentSeedOnStart.IsChecked = s.TorrentSeedOnStart;
        TorrentRatioBox.Text = (s.TorrentSeedRatioLimit * 100).ToString("0.#", CultureInfo.InvariantCulture);
        TorrentDlScheduleOn.IsChecked = s.TorrentDownloadScheduleEnabled; TorrentDlStart.Text = s.TorrentDownloadStartTime; TorrentDlStop.Text = s.TorrentDownloadStopTime;
        TorrentSeedScheduleOn.IsChecked = s.TorrentSeedScheduleEnabled; TorrentSeedStart.Text = s.TorrentSeedStartTime; TorrentSeedStop.Text = s.TorrentSeedStopTime;

        // Remote
        // Proxy / Socks
        ProxyNone.IsChecked = s.ProxyMode == "none"; ProxySystem.IsChecked = s.ProxyMode == "system"; ProxyManual.IsChecked = s.ProxyMode == "manual";
        ProxyHostBox.Text = s.ProxyHost; ProxyPortBox.Text = s.ProxyPort.ToString(CultureInfo.InvariantCulture);
        ProxyUsernameBox.Text = s.ProxyUsername; ProxyPasswordBox.Password = s.ProxyPassword;
        ProxyForHttp.IsChecked = s.ProxyUseForHttp; ProxyForHttps.IsChecked = s.ProxyUseForHttps;
        ProxyManualPanel.Visibility = s.ProxyMode == "manual" ? Visibility.Visible : Visibility.Collapsed;
        ProxyNone.Checked += (_, _) => ProxyManualPanel.Visibility = Visibility.Collapsed;
        ProxySystem.Checked += (_, _) => ProxyManualPanel.Visibility = Visibility.Collapsed;
        ProxyManual.Checked += (_, _) => ProxyManualPanel.Visibility = Visibility.Visible;

        RemoteOn.IsChecked = s.RemoteEnabled;
        RemotePortBox.Text = s.RemotePort.ToString(CultureInfo.InvariantCulture);
        RefreshRemoteUrl();
        RemoteDetails.Visibility = s.RemoteEnabled ? Visibility.Visible : Visibility.Collapsed;
        RemoteOn.Checked += (_, _) => RemoteDetails.Visibility = Visibility.Visible;
        RemoteOn.Unchecked += (_, _) => RemoteDetails.Visibility = Visibility.Collapsed;

        // YouTube & other sites
        YtPathBox.Text = s.YtDlpPath;
        CookiesBox.SelectedIndex = s.YtCookiesBrowser switch { "chrome" => 1, "edge" => 2, "firefox" => 3, "brave" => 4, _ => 0 };

        _loading = false;
        CategoryCombo.SelectedIndex = 0;
        RefreshToolStatus(initial: true);
        if (WindowsIntegration.IsPackaged) _ = LoadPackagedStartupAsync();
    }

    async Task LoadPackagedStartupAsync()
    {
        LaunchStartup.IsChecked = await WindowsIntegration.GetLaunchOnStartupAsync();
        LaunchStartup.IsEnabled = true;
    }

    // ---------------------------------------------------------------- General

    void ShowBrowserStatus()
    {
        BrowserStatusPanel.Children.Clear();
        foreach (var browser in WindowsIntegration.Browsers())
        {
            var row = new TextBlock { Margin = new Thickness(0, 2, 0, 2) };
            row.Inlines.Add(new System.Windows.Documents.Run(browser.Registered ? "✓  " : "✗  ") { Foreground = (System.Windows.Media.Brush)FindResource(browser.Registered ? "Success" : "Danger"), FontWeight = FontWeights.Bold });
            row.Inlines.Add(new System.Windows.Documents.Run(browser.Name + " — " + Loc.T(browser.Registered ? "connected" : "not set up")));
            BrowserStatusPanel.Children.Add(row);
        }
    }

    void Recheck_Click(object sender, RoutedEventArgs e) => ShowBrowserStatus();

    void Repair_Click(object sender, RoutedEventArgs e)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "install-browser-integration.ps1");
        if (!File.Exists(script)) { Dlg.Show(this, "The installer script (install-browser-integration.ps1) was not found next to Makan. Run it from the publish folder.", "Options"); return; }
        try { Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Dlg.Show(this, ex.Message, "Options"); }
    }

    // ---------------------------------------------------------------- File types

    void DefaultFileTypes_Click(object sender, RoutedEventArgs e) => FileTypesBox.Text = CaptureRules.DefaultFileTypes;
    void DefaultSites_Click(object sender, RoutedEventArgs e) => SitesBox.Text = CaptureRules.DefaultExcludedSites;

    // ---------------------------------------------------------------- Save to (categories)

    void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StoreCategoryFields();
        _shownCategory = CategoryCombo.SelectedIndex;
        if (_shownCategory < 0 || _shownCategory >= _cats.Count) return;
        var c = _cats[_shownCategory];
        var general = c.Name == CategoryService.General;
        CategoryTypesBox.IsEnabled = !general;
        CategoryTypesBox.Text = general ? Loc.T("Everything that is not listed in another category") : string.Join(" ", c.Extensions.Select(x => x.TrimStart('.').ToUpperInvariant()));
        CategoryFolderBox.Text = string.IsNullOrWhiteSpace(c.Folder) ? Derived(c.Name, BaseFolder()) : c.Folder;
        DeleteCategoryButton.IsEnabled = !general;
    }

    string BaseFolder() => BaseFolderBox.Text.Trim().Length > 0 ? BaseFolderBox.Text.Trim() : App.Settings.DefaultFolder;
    static string Derived(string category, string baseFolder) => category == CategoryService.General ? baseFolder : Path.Combine(baseFolder, category);

    /// <summary>Copies the text boxes back into the category that was showing.</summary>
    void StoreCategoryFields()
    {
        if (_shownCategory < 0 || _shownCategory >= _cats.Count) return;
        var c = _cats[_shownCategory];
        if (c.Name != CategoryService.General)
            c.Extensions = CategoryTypesBox.Text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(CategoryService.NormalizeExtension).Where(x => x.Length > 1).Distinct().ToList();
        var typed = CategoryFolderBox.Text.Trim();
        // the automatic folder (<main folder>\<category>) is not stored, so it keeps following the main folder
        c.Folder = typed.Length == 0 || string.Equals(typed, Derived(c.Name, BaseFolder()), StringComparison.OrdinalIgnoreCase) ? null : typed;
    }

    void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var ask = new TextPromptDialog(Loc.T("New category"), Loc.T("Name of the new category:")) { Owner = this };
        if (ask.ShowDialog() != true) return;
        if (_cats.Any(c => string.Equals(c.Name, ask.Value, StringComparison.OrdinalIgnoreCase))) { Dlg.Show(this, "A category with that name already exists.", "Options"); return; }
        StoreCategoryFields();
        _cats.Insert(Math.Max(0, _cats.Count - 1), new CategoryDef { Name = ask.Value });   // before "General"
        _loading = true; CategoryCombo.ItemsSource = _cats.Select(c => c.Name).ToList(); _loading = false;
        _shownCategory = -1;
        CategoryCombo.SelectedIndex = _cats.Count - 2;
    }

    void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_shownCategory < 0 || _shownCategory >= _cats.Count || _cats[_shownCategory].Name == CategoryService.General) return;
        if (Dlg.Show(this, Loc.F("Delete the category \"{0}\"? Its files are not touched; new downloads of its types go to General.", _cats[_shownCategory].Name), "Options", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _cats.RemoveAt(_shownCategory);
        _shownCategory = -1;
        _loading = true; CategoryCombo.ItemsSource = _cats.Select(c => c.Name).ToList(); _loading = false;
        CategoryCombo.SelectedIndex = 0;
    }

    static void PickFolder(System.Windows.Controls.TextBox box, string description)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = Loc.T(description), UseDescriptionForTitle = true, SelectedPath = box.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) box.Text = dialog.SelectedPath;
    }

    void BrowseCategory_Click(object sender, RoutedEventArgs e) => PickFolder(CategoryFolderBox, "Folder for this category");
    void BrowseBase_Click(object sender, RoutedEventArgs e) => PickFolder(BaseFolderBox, "Main download folder");
    void BrowseTemp_Click(object sender, RoutedEventArgs e) => PickFolder(TempFolderBox, "Temporary folder");
    void BrowseTorrent_Click(object sender, RoutedEventArgs e) => PickFolder(TorrentFolderBox, "Torrents");

    // ---------------------------------------------------------------- Downloads

    void AskBefore_Changed(object sender, RoutedEventArgs e) { if (OnlyQueue != null) OnlyQueue.IsEnabled = AskBefore.IsChecked == true; }

    void BrowseAv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Antivirus program"), Filter = "Programs|*.exe|All files|*.*" };
        if (dialog.ShowDialog(this) == true) AvProgramBox.Text = dialog.FileName;
    }
    void DefaultAv_Click(object sender, RoutedEventArgs e) { AvArgsBox.Text = AppSettings.DefaultAntivirusArguments; }

    void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Find ffmpeg.exe"), Filter = "ffmpeg|ffmpeg.exe|Programs|*.exe|All files|*.*" };
        if (dialog.ShowDialog(this) == true) Ffmpeg.Text = dialog.FileName;
    }

    // ---------------------------------------------------------------- OK / Cancel

    bool TryFolder(string folder, string what)
    {
        if (folder.Length == 0) return true;
        try { Directory.CreateDirectory(folder); return true; }
        catch (Exception ex) { Dlg.Show(this, Loc.F("Can't use the {0} folder:\n{1}", Loc.T(what), ex.Message), "Options", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        StoreCategoryFields();
        var s = App.Settings;
        // The limiter is a switch plus a value: off means no limit at all, on always means a real limit (at least 1 KB/s).
        var kb = SpeedLimitOn.IsChecked == true
            ? (long.TryParse(Speed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : Math.Max(1, s.SpeedKbps))
            : 0;
        var baseFolder = BaseFolderBox.Text.Trim(); var tempFolder = TempFolderBox.Text.Trim(); var torrentFolder = TorrentFolderBox.Text.Trim();
        if (baseFolder.Length == 0) baseFolder = s.DefaultFolder;
        if (!TryFolder(baseFolder, "main download") || !TryFolder(tempFolder, "temporary")) return;

        // General
        var browsers = new List<string>();
        if (BrChrome.IsChecked == true) browsers.Add("chrome"); if (BrEdge.IsChecked == true) browsers.Add("edge"); if (BrFirefox.IsChecked == true) browsers.Add("firefox");
        if (BrOpera.IsChecked == true) browsers.Add("opera"); if (BrVivaldi.IsChecked == true) browsers.Add("vivaldi"); if (BrOther.IsChecked == true) browsers.Add("other");
        s.CaptureBrowsers = browsers.Count == 0 ? "none" : string.Join(",", browsers);
        s.ClipboardWatch = ClipboardWatch.IsChecked == true;
        if ((LaunchStartup.IsChecked == true) != await WindowsIntegration.GetLaunchOnStartupAsync())
        {
            var changed = await WindowsIntegration.SetLaunchOnStartupAsync(LaunchStartup.IsChecked == true, Environment.ProcessPath ?? "");
            if (!changed && LaunchStartup.IsChecked == true)
            {
                Dlg.Show(this, Loc.T("Windows did not allow Epsilon Download Manager to start automatically. You can enable it in Windows Settings > Apps > Startup."), "Options", MessageBoxButton.OK, MessageBoxImage.Information);
                LaunchStartup.IsChecked = await WindowsIntegration.GetLaunchOnStartupAsync();
            }
        }
        var newLanguage = LanguageBox.SelectedIndex == 1 ? "fa" : "en";
        var languageChanged = newLanguage != s.Language;
        s.Language = newLanguage;
        s.Theme = ThemeBox.SelectedIndex switch { 0 => "obsidian-gold", 1 => "platinum-blue", 2 => "royal-amethyst", 3 => "emerald-executive", 4 => "champagne-minimal", 5 => "graphite-copper", 6 => "sapphire-noir", 7 => "ivory-luxe", 8 => "rose-titanium", 9 => "arctic-glass", 10 => "dracula", 11 => "makan-lab", 12 => "auto", _ => "platinum-blue" };

        // File types
        s.FileTypes = FileTypesBox.Text.Trim(); s.ExcludedSites = SitesBox.Text.Trim(); s.ExcludedAddresses = AddressesBox.Text.Trim();

        // Save to
        s.DefaultFolder = baseFolder; s.TempDirectory = tempFolder; s.SetFileDateFromServer = SetFileDate.IsChecked == true; s.TorrentFolder = torrentFolder;
        s.ChangeFolderOnLastSelected = ChangeFolderLast.IsChecked == true;
        foreach (var c in _cats.Where(c => !string.IsNullOrWhiteSpace(c.Folder))) if (!TryFolder(c.Folder!, "category")) return;
        CategoryService.Configure(_cats);
        s.CategoriesJson = CategoryService.ToJson();

        // Downloads
        s.AskBeforeDownload = AskBefore.IsChecked == true; s.OnlyAddToQueue = OnlyQueue.IsChecked == true; s.ShowCompleteDialog = ShowComplete.IsChecked == true; s.ShowProgressWindow = ShowProgress.IsChecked == true; s.ShowWindowOnCapture = ShowWindowOnCapture.IsChecked == true;
        s.AskQueueOnLater = AskQueueLater.IsChecked == true; s.AskQueueOnBatch = AskQueueBatch.IsChecked == true; s.IgnoreModifiedOnResume = IgnoreModified.IsChecked == true;
        s.DuplicateAction = DuplicateBox.SelectedIndex switch { 1 => "suffix", 2 => "overwrite", 3 => "skip", _ => "ask" };
        s.UserAgent = UserAgentBox.Text.Trim();
        s.AntivirusEnabled = AvOn.IsChecked == true; s.AntivirusProgram = AvProgramBox.Text.Trim(); s.AntivirusArguments = AvArgsBox.Text.Trim();

        // YouTube & other sites
        s.YtDlpPath = YtPathBox.Text.Trim();
        s.YtCookiesBrowser = CookiesBox.SelectedIndex switch { 1 => "chrome", 2 => "edge", 3 => "firefox", 4 => "brave", _ => "" };

        // Connection
        s.MultiNetwork = MultiNetOn.IsChecked == true;
        s.MaxActive = (int)Slots.Value; s.SpeedKbps = kb; s.SpeedLimitScope = SpeedScope.SelectedIndex == 1 ? "per_file" : "combined"; s.Connections = (int)Connections.Value; s.AutoResume = AutoResume.IsChecked == true; s.KeepAwakeWhileDownloading = KeepAwake.IsChecked == true; s.FfmpegPath = Ffmpeg.Text.Trim();
        s.AdaptiveConnections = SmartConnections.IsChecked == true; s.SmartDownloads = SmartConnections.IsChecked == true;
        s.BoostDownloadSpeed = BoostSpeed.IsChecked == true;

        // BitTorrent
        s.TorrentPort = int.TryParse(TorrentPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 6881;
        s.TorrentDht = TorrentDht.IsChecked == true; s.TorrentPex = TorrentPex.IsChecked == true;
        s.TorrentDownloadKbps = long.TryParse(TorrentDownloadBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var downKbps) ? Math.Max(0, downKbps) : 0;
        s.TorrentUploadKbps = long.TryParse(TorrentUploadBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var upKbps) ? Math.Max(0, upKbps) : 0;
        s.TorrentUploadSlots = (int)TorrentSlots.Value;
        s.TorrentMaxPeers = int.TryParse(TorrentMaxPeersBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPeers) ? maxPeers : 60;
        s.TorrentSeedAfterCompletion = TorrentSeedAfter.IsChecked == true; s.TorrentSeedOnStart = TorrentSeedOnStart.IsChecked == true;
        s.TorrentSeedRatioLimit = double.TryParse(TorrentRatioBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratioPct) ? Math.Max(0, ratioPct) / 100.0 : 1.0;
        s.TorrentDownloadScheduleEnabled = TorrentDlScheduleOn.IsChecked == true;
        s.TorrentDownloadStartTime = TryTime(TorrentDlStart.Text, out var dlStart) ? dlStart : s.TorrentDownloadStartTime;
        s.TorrentDownloadStopTime = TryTime(TorrentDlStop.Text, out var dlStop) ? dlStop : s.TorrentDownloadStopTime;
        s.TorrentSeedScheduleEnabled = TorrentSeedScheduleOn.IsChecked == true;
        s.TorrentSeedStartTime = TryTime(TorrentSeedStart.Text, out var seedStart) ? seedStart : s.TorrentSeedStartTime;
        s.TorrentSeedStopTime = TryTime(TorrentSeedStop.Text, out var seedStop) ? seedStop : s.TorrentSeedStopTime;

        s.ProxyMode = ProxyManual.IsChecked == true ? "manual" : ProxySystem.IsChecked == true ? "system" : "none";
        s.ProxyHost = ProxyHostBox.Text.Trim();
        s.ProxyPort = int.TryParse(ProxyPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var proxyPort) && proxyPort is > 0 and < 65536 ? proxyPort : s.ProxyPort;
        s.ProxyUsername = ProxyUsernameBox.Text.Trim();
        if (ProxyPasswordBox.Password != s.ProxyPassword) s.ProxyPassword = ProxyPasswordBox.Password;   // avoid re-encrypting (and rewriting) an unchanged password on every save
        s.ProxyUseForHttp = ProxyForHttp.IsChecked == true; s.ProxyUseForHttps = ProxyForHttps.IsChecked == true;

        s.RemoteEnabled = RemoteOn.IsChecked == true;
        s.RemotePort = int.TryParse(RemotePortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var remotePort) && remotePort is > 0 and < 65536 ? remotePort : s.RemotePort;

        App.RaiseSettingsChanged();
        DialogResult = true;

        if (languageChanged && Dlg.Show(Owner, "Makan must restart to change the language. Restart now?", "Options", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            App.Restart();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void RandomTorrentPort_Click(object sender, RoutedEventArgs e) => TorrentPortBox.Text = Random.Shared.Next(10000, 65000).ToString(CultureInfo.InvariantCulture);

    /// <summary>What Multi-Network would use right now, in plain words.</summary>
    static string DescribeNetworks()
    {
        var links = App.Manager.CurrentLinks();
        if (links.Count >= 2) return Loc.F("Connected now: {0}", string.Join(" + ", links.Select(l => Loc.T(l.Kind) + " (" + l.Name + ")")));
        return links.Count == 1
            ? Loc.F("Only one network is connected now ({0}). Connect a second one to use this.", Loc.T(links[0].Kind))
            : Loc.T("No network with internet access was found right now.");
    }

    void RefreshRemoteUrl()
    {
        var ip = PortMapper.GetLocalAddress() ?? "<this computer's IP>";
        var port = int.TryParse(RemotePortBox.Text.Trim(), out var p) ? p : App.Settings.RemotePort;
        RemoteUrlBox.Text = $"http://{ip}:{port}/?token={App.Settings.RemoteToken}";
    }

    void RemoteCopy_Click(object sender, RoutedEventArgs e)
    {
        RefreshRemoteUrl();
        try { Clipboard.SetText(RemoteUrlBox.Text); } catch (Exception) { /* clipboard busy */ }
    }

    void RemoteNewToken_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.RemoteToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();
        RefreshRemoteUrl();
        App.RaiseSettingsChanged();
    }

    static bool TryTime(string text, out string normalized)
    {
        normalized = "";
        if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return false;
        normalized = $"{t.Hours:00}:{t.Minutes:00}";
        return true;
    }

    // ---------------------------------------------------------------- YouTube & other sites

    /// <summary>Opens the Options window on the "YouTube &amp; other sites" tab (used when a video needs yt-dlp).</summary>
    public void ShowYouTubeTab()
    {
        foreach (var tab in Enumerable.Range(0, MainTabs.Items.Count)) if (MainTabs.Items[tab] is TabItem { Header: string h } && h.StartsWith("YouTube", StringComparison.Ordinal)) MainTabs.SelectedIndex = tab;
    }

    static string Describe(string? path) => path == null ? Loc.T("not installed") : Loc.T("ready") + "  —  " + path;

    void RefreshToolStatus(bool initial = false)
    {
        var dir = YtDlpTools.DefaultToolsDirectory;
        var yt = YtDlpTools.FindExecutable("yt-dlp.exe", YtPathBox.Text, dir);
        var ffmpeg = YtDlpTools.FindExecutable("ffmpeg.exe", Ffmpeg.Text, dir);
        YtStatus.Text = Describe(yt);
        DenoStatus.Text = Describe(YtDlpTools.FindExecutable("deno.exe", null, dir));
        FfmpegStatus.Text = Describe(ffmpeg);
        if (initial) InstallFfmpeg.IsChecked = ffmpeg == null;
    }

    void YtPath_Changed(object sender, TextChangedEventArgs e) { if (!_loading && IsLoaded) RefreshToolStatus(); }

    void BrowseYt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "yt-dlp|yt-dlp*.exe|Programs|*.exe", Title = Loc.T("Choose yt-dlp") };
        if (dialog.ShowDialog(this) == true) YtPathBox.Text = dialog.FileName;
    }

    async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        var installer = new ToolsInstaller(YtDlpTools.DefaultToolsDirectory);
        InstallButton.IsEnabled = false; InstallBar.Visibility = Visibility.Visible;
        var progress = new Progress<string>(text => InstallStatus.Text = Loc.T(text));
        try
        {
            await installer.InstallYtDlpAsync(progress, CancellationToken.None);                       // always: this is the "update" part
            if (!File.Exists(installer.DenoPath)) await installer.InstallDenoAsync(progress, CancellationToken.None);
            if (InstallFfmpeg.IsChecked == true && !File.Exists(installer.FfmpegPath)) await installer.InstallFfmpegAsync(progress, CancellationToken.None);
            InstallStatus.Text = Loc.T("All tools are ready.");
        }
        catch (Exception ex)
        {
            new DiagnosticsService().Error("Installing the YouTube tools failed", ex);
            InstallStatus.Text = "";
            Dlg.Show(this, Loc.F("The tools could not be installed:\n{0}", ex.Message), "Options", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            InstallBar.Visibility = Visibility.Collapsed; InstallButton.IsEnabled = true;
            RefreshToolStatus();
            App.RefreshYtDlp();
        }
    }
}
