using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MakanDownloadManager.Services;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

/// <summary>What's left after installing: connect a browser, get the video tools, confirm where files go. Shown once
/// automatically after the very first launch, and any time afterward from Help &gt; Getting Started.</summary>
public sealed class FirstRunWindow : Window
{
    readonly TextBlock _browserStatus = Stat();
    readonly TextBlock _toolsStatus = Stat();
    readonly TextBlock _folderStatus = Stat();
    readonly Button _installToolsButton = new() { Content = Loc.T("Install now"), Padding = new Thickness(12, 5, 12, 5) };
    readonly ProgressBar _installBar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, IsIndeterminate = true };

    public FirstRunWindow(Window owner)
    {
        Owner = owner;
        Title = Loc.T("Getting started");
        Width = 650; MinWidth = 540; MinHeight = 520; MaxHeight = 760;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        var header = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 17, 20, 17), Margin = new Thickness(0, 0, 0, 18) };
        header.SetResourceReference(Border.BackgroundProperty, "Surface");
        header.SetResourceReference(Border.BorderBrushProperty, "Accent");
        header.BorderThickness = new Thickness(1, 1, 1, 3);
        var headerText = new StackPanel();
        var eyebrow = new TextBlock { Text = "EPSILON  •  QUICK START", FontSize = 11, FontWeight = FontWeights.Bold };
        eyebrow.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        headerText.Children.Add(eyebrow);
        headerText.Children.Add(new TextBlock { Text = Loc.T("Welcome to Epsilon Download Manager"), FontSize = 23, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 4) });
        headerText.Children.Add(Muted(Loc.T("Connect your browser, choose a download folder, and optionally install the video tools."), new Thickness(0)));
        header.Child = headerText;
        root.Children.Add(header);

        var browserButtons = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
        browserButtons.Children.Add(StoreButton("◉  " + Loc.T("Chrome Web Store"), Branding.ChromeExtensionUrl));
        browserButtons.Children.Add(StoreButton("◆  " + Loc.T("Firefox Add-ons"), Branding.FirefoxExtensionUrl));
        root.Children.Add(Section("1", Loc.T("Install the official browser extension"),
            Loc.T("Use the verified store version for automatic updates and safe browser integration."), _browserStatus, browserButtons));

        root.Children.Add(Section("2",
            Loc.T("Video downloads (YouTube and similar sites)"), Loc.T("Install the optional tools only if you download videos or streams."), _toolsStatus,
            _installToolsButton.Tap(b => b.Click += InstallTools_Click)));

        root.Children.Add(Section("3",
            Loc.T("Download folder"), Loc.T("Choose where new downloads are saved. You can change this later in Options."), _folderStatus,
            Btn(Loc.T("Change…"), ChangeFolder_Click)));

        root.Children.Add(_installBar);

        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hint = Muted(Loc.T("You can open this guide again from Help → Getting started."), new Thickness(0, 7, 18, 0));
        Grid.SetColumn(hint, 0); footer.Children.Add(hint);
        var done = new Button { Content = Loc.T("Get started"), Padding = new Thickness(20, 9, 20, 9), Style = TryFindResource("PrimaryButton") as Style, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        done.Click += (_, _) => Close();
        Grid.SetColumn(done, 1); footer.Children.Add(done);
        root.Children.Add(footer);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => App.Settings.FirstRunDone = true;
    }

    static TextBlock Stat() { var t = new TextBlock { Margin = new Thickness(0, 2, 0, 0) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static TextBlock Muted(string text, Thickness? margin = null) { var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 16) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static Button Btn(string text, RoutedEventHandler click) { var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5) }; b.Click += click; return b; }
    static Button StoreButton(string text, string url)
    {
        var button = Btn(text, (_, _) => OpenUrl(url));
        button.Margin = new Thickness(0, 0, 9, 0);
        button.Padding = new Thickness(14, 8, 14, 8);
        return button;
    }

    static UIElement Section(string number, string title, string explanation, TextBlock status, FrameworkElement action)
    {
        var box = new Border { Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        box.SetResourceReference(Border.BackgroundProperty, "Surface");
        box.SetResourceReference(Border.BorderBrushProperty, "Border");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        var badgeText = new TextBlock { Text = number, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Panel");
        badge.Child = badgeText;
        Grid.SetColumn(badge, 0); grid.Children.Add(badge);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(Muted(explanation, new Thickness(0, 3, 0, 0)));
        text.Children.Add(status);
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Margin = new Thickness(0, 10, 0, 0);
        text.Children.Add(action);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        box.Child = grid;
        return box;
    }

    void Refresh()
    {
        var connected = WindowsIntegration.Browsers().Any(b => b.Registered);
        _browserStatus.Text = Loc.T(connected ? "Desktop connection ready. Install the extension from your browser's official store." : "Browser connection needs repair. Reinstall Epsilon, then install the official extension.");

        var ready = App.Manager.YtDlp is { } yt && File.Exists(yt.ExePath);
        _toolsStatus.Text = Loc.T(ready ? "Ready." : "Not installed yet - needed only for YouTube-style sites.");
        _installToolsButton.IsEnabled = !ready;
        _installToolsButton.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;

        _folderStatus.Text = App.Settings.DefaultFolder;
    }

    static void OpenUrl(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    async void InstallTools_Click(object sender, RoutedEventArgs e)
    {
        _installToolsButton.IsEnabled = false; _installBar.Visibility = Visibility.Visible;
        try
        {
            var installer = new ToolsInstaller(YtDlpTools.DefaultToolsDirectory);
            var progress = new Progress<string>(_ => { });
            await installer.InstallYtDlpAsync(progress, System.Threading.CancellationToken.None);
            if (!File.Exists(installer.DenoPath)) await installer.InstallDenoAsync(progress, System.Threading.CancellationToken.None);
        }
        catch (Exception ex) { new DiagnosticsService().Error("Installing the video tools failed (from the first-run checklist)", ex); }
        finally { _installBar.Visibility = Visibility.Collapsed; App.RefreshYtDlp(); Refresh(); }
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = App.Settings.DefaultFolder, Description = Loc.T("Save to") };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) { App.Settings.DefaultFolder = dialog.SelectedPath; Refresh(); }
    }
}
