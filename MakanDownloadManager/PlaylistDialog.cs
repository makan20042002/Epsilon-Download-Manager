using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public enum PlaylistChoice { Cancel, DownloadAll, JustThisOne }

/// <summary>"This looks like a playlist of N videos - download them all?" A single quality applies to every video, since
/// asking per video would defeat the point of a one-click bulk download.</summary>
public sealed class PlaylistDialog : Window
{
    static readonly (string Key, string Label)[] Qualities =
    {
        ("v2160", "2160p (4K)"), ("v1440", "1440p"), ("v1080", "1080p"), ("v720", "720p"), ("v480", "480p"),
        ("a", "Audio only (M4A)"), ("a-mp3", "Audio only (MP3)")
    };

    readonly ComboBox _quality = new();

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

        root.Children.Add(new TextBlock { Text = Loc.T("Quality for every video:"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (key, label) in Qualities) _quality.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        _quality.SelectedIndex = 2;   // 1080p: a reasonable default for a whole playlist
        root.Children.Add(_quality);

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
        if (choice == PlaylistChoice.DownloadAll && _quality.SelectedItem is ComboBoxItem { Tag: string key }) SelectedKey = key;
        DialogResult = choice != PlaylistChoice.Cancel;
    }
}
