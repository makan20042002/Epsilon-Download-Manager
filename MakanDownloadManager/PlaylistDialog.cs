using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public enum PlaylistChoice { Cancel, DownloadAll, JustThisOne }

/// <summary>"This looks like a playlist of N videos - download them all?" A single quality applies to every video, since
/// asking per video would defeat the point of a one-click bulk download.</summary>
public sealed class PlaylistDialog : Window
{
    static readonly (int Height, string Label)[] Qualities =
    {
        (2160, "2160p (4K)"), (1440, "1440p"), (1080, "1080p"), (720, "720p"), (480, "480p"), (360, "360p")
    };

    readonly ComboBox _quality = new();
    readonly ComboBox _format = new();
    readonly CheckBox _remember = new() { Content = Loc.T("Use these choices as my default for future playlists"), IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };

    public PlaylistChoice Choice { get; private set; } = PlaylistChoice.Cancel;
    public string SelectedKey { get; private set; } = "v1080";

    public PlaylistDialog(Window owner, string playlistTitle, int videoCount)
    {
        Owner = owner;
        Title = Loc.T("Playlist found");
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = Loc.T("This looks like a playlist:"), Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new TextBlock { Text = playlistTitle, FontWeight = FontWeights.Bold, FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        var count = new TextBlock { Text = Loc.F("{0} videos", videoCount), Margin = new Thickness(0, 0, 0, 18) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(count);

        root.Children.Add(new TextBlock { Text = Loc.T("Preferred format for every item:"), Margin = new Thickness(0, 0, 0, 6) });
        _format.Items.Add(new ComboBoxItem { Content = Loc.T("MP4 video"), Tag = "mp4" });
        _format.Items.Add(new ComboBoxItem { Content = Loc.T("MKV video"), Tag = "mkv" });
        _format.Items.Add(new ComboBoxItem { Content = Loc.T("M4A audio only"), Tag = "m4a" });
        _format.Items.Add(new ComboBoxItem { Content = Loc.T("MP3 audio only"), Tag = "mp3" });
        _format.SelectedIndex = App.Settings.PlaylistPreferredFormat switch { "mkv" => 1, "m4a" => 2, "mp3" => 3, _ => 0 };
        _format.SelectionChanged += (_, _) => _quality.IsEnabled = (_format.SelectedItem as ComboBoxItem)?.Tag is "mp4" or "mkv";
        root.Children.Add(_format);

        root.Children.Add(new TextBlock { Text = Loc.T("Preferred quality for every video:"), Margin = new Thickness(0, 12, 0, 6) });
        foreach (var (height, label) in Qualities) _quality.Items.Add(new ComboBoxItem { Content = label, Tag = height });
        _quality.SelectedItem = _quality.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (int)x.Tag == App.Settings.PlaylistPreferredHeight) ?? _quality.Items[2];
        _quality.IsEnabled = App.Settings.PlaylistPreferredFormat is "mp4" or "mkv";
        root.Children.Add(_quality);
        root.Children.Add(_remember);

        var buttons = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };
        var all = new Button { Content = Loc.F("Download all {0} videos", videoCount), Padding = new Thickness(14, 8, 14, 8), Style = TryFindResource("PrimaryButton") as Style, HorizontalAlignment = HorizontalAlignment.Stretch };
        all.Click += (_, _) => Finish(PlaylistChoice.DownloadAll);
        buttons.Children.Add(all);
        var justOne = new Button { Content = Loc.T("Just this one video"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        justOne.Click += (_, _) => Finish(PlaylistChoice.JustThisOne);
        buttons.Children.Add(justOne);
        var cancel = new Button { Content = Loc.T("Cancel"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, IsCancel = true };
        cancel.Click += (_, _) => Finish(PlaylistChoice.Cancel);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
    }

    void Finish(PlaylistChoice choice)
    {
        Choice = choice;
        if (choice == PlaylistChoice.DownloadAll)
        {
            var format = (_format.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";
            var height = (_quality.SelectedItem as ComboBoxItem)?.Tag is int h ? h : 1080;
            SelectedKey = format switch { "m4a" => "a", "mp3" => "a-mp3", "mkv" => $"v{height}-mkv", _ => $"v{height}" };
            if (_remember.IsChecked == true)
            {
                App.Settings.PlaylistPreferredFormat = format;
                App.Settings.PlaylistPreferredHeight = height;
            }
        }
        DialogResult = choice != PlaylistChoice.Cancel;
    }
}
