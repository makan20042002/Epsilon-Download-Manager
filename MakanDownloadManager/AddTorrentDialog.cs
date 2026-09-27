using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>"Add a magnet link or a .torrent file" - built in code, like the torrent details window, since it is small and theme-aware without a XAML file.</summary>
public sealed class AddTorrentDialog : Window
{
    readonly TextBox _box = new() { AcceptsReturn = false, MinHeight = 60, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6) };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    /// <summary>The magnet link or .torrent file path chosen, once <see cref="Window.ShowDialog"/> returns true.</summary>
    public string Url { get; private set; } = "";

    /// <summary>Fills the text box with something already at hand (e.g. a magnet link found on the clipboard when the dialog opens).</summary>
    public void Prefill(string text) => _box.Text = text;

    public AddTorrentDialog(Window owner)
    {
        Owner = owner;
        Title = Loc.T("Add torrent");
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = Loc.T("Paste a magnet link:"), Margin = new Thickness(0, 0, 0, 6) });
        root.Children.Add(_box);
        root.Children.Add(_error);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");

        var orRow = new TextBlock { Text = Loc.T("or"), Margin = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        orRow.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(orRow);

        var browse = new Button { Content = Loc.T("Browse for a .torrent file…"), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        browse.Click += Browse_Click;
        root.Children.Add(browse);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = Loc.T("Cancel"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var ok = new Button { Content = Loc.T("Add"), Padding = new Thickness(14, 6, 14, 6), Style = TryFindResource("PrimaryButton") as Style, IsDefault = true };
        ok.Click += Ok_Click;
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _box.Focus();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Torrent files") + " (*.torrent)|*.torrent|" + Loc.T("All files") + " (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) { _box.Text = dialog.FileName; _error.Visibility = Visibility.Collapsed; }
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = _box.Text.Trim();
        if (!DownloadManager.IsTorrentUrl(text))
        {
            _error.Text = Loc.T("That doesn't look like a magnet link or a .torrent file.");
            _error.Visibility = Visibility.Visible;
            return;
        }
        Url = text;
        DialogResult = true;
    }
}
