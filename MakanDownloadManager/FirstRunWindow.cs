using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MakanDownloadManager.Services;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

/// <summary>What's left after installing: connect a browser, get the video tools, confirm where files go. Shown once
/// automatically after the very first launch, and any time afterward from Help &gt; Getting Started.</summary>
public sealed class FirstRunWindow : Window
{
    const string ChromeStoreUrl = "https://chromewebstore.google.com/detail/epsilon-download-manager/nglldicodleblllopbkgncogljbdldpd";
    const string FirefoxStoreUrl = "https://addons.mozilla.org/addon/epsilon-download-manager/";
    readonly TextBlock _browserStatus = Stat();
    readonly TextBlock _toolsStatus = Stat();
    readonly TextBlock _folderStatus = Stat();
    readonly Button _installToolsButton = new() { Content = Loc.T("Install now"), Padding = new Thickness(12, 5, 12, 5) };
    readonly ProgressBar _installBar = new() { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, IsIndeterminate = true };

    public FirstRunWindow(Window owner)
    {
        Owner = owner;
        Title = Loc.T("Getting started");
        Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = Loc.T("Welcome to Epsilon Download Manager"), FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(Muted(Loc.T("A couple of things finish setting it up:")));

        var browserButtons = new StackPanel { Orientation = Orientation.Horizontal };
        browserButtons.Children.Add(Btn(Loc.T("Chrome / Edge"), (_, _) => OpenUrl(ChromeStoreUrl)));
        browserButtons.Children.Add(Btn(Loc.T("Firefox"), (_, _) => OpenUrl(FirefoxStoreUrl)));
        root.Children.Add(Section(Loc.T("Browser extension"), _browserStatus, browserButtons));

        root.Children.Add(Section(
            Loc.T("Video downloads (YouTube and similar sites)"), _toolsStatus,
            _installToolsButton.Tap(b => b.Click += InstallTools_Click)));

        root.Children.Add(Section(
            Loc.T("Download folder"), _folderStatus,
            Btn(Loc.T("Change…"), ChangeFolder_Click)));

        root.Children.Add(_installBar);

        var done = new Button { Content = Loc.T("Get started"), Padding = new Thickness(16, 7, 16, 7), Style = TryFindResource("PrimaryButton") as Style, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), IsDefault = true, IsCancel = true };
        done.Click += (_, _) => Close();
        root.Children.Add(done);

        Content = root;
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => App.Settings.FirstRunDone = true;
    }

    static TextBlock Stat() { var t = new TextBlock { Margin = new Thickness(0, 2, 0, 0) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static TextBlock Muted(string text) { var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 16) }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }
    static Button Btn(string text, RoutedEventHandler click) { var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5) }; b.Click += click; return b; }

    static UIElement Section(string title, TextBlock status, FrameworkElement action)
    {
        var box = new Border { Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8) };
        box.SetResourceReference(Border.BackgroundProperty, "Surface");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        text.Children.Add(status);
        Grid.SetColumn(text, 0); grid.Children.Add(text);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1); grid.Children.Add(action);
        box.Child = grid;
        return box;
    }

    void Refresh()
    {
        var connected = WindowsIntegration.Browsers().Any(b => b.Registered);
        _browserStatus.Text = Loc.T(connected ? "Desktop connection ready - install the extension from your browser's official store." : "Browser connection needs repair - reinstall Epsilon, then install the official extension.");

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
