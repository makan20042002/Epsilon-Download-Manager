using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>Help &gt; About: everything that used to sit on the main screen (creator, website, email) now lives here instead,
/// out of the way until someone actually asks for it. Built in code, like the other small dialogs this session added.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(Window owner)
    {
        Owner = owner;
        Title = Loc.T("About Epsilon Download Manager");
        Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("AppWindow") is Style windowStyle) Style = windowStyle;

        var root = new StackPanel { Margin = new Thickness(22) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var badge = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0) };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentDark");
        var badgeText = new TextBlock { Text = "E", FontSize = 26, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        badge.Child = badgeText;
        header.Children.Add(badge);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Branding.ProductName, FontSize = 17, FontWeight = FontWeights.Bold });
        titles.Children.Add(Muted($"{Loc.T("Version")} {Branding.Version}"));
        header.Children.Add(titles);
        root.Children.Add(header);

        root.Children.Add(new TextBlock { Text = Loc.T(Branding.Tagline), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });

        root.Children.Add(CreatorsRow(Loc.T("Created by"), (Branding.Creator, Branding.CreatorGitHubUrl), (Branding.CoCreator, Branding.CoCreatorGitHubUrl)));
        root.Children.Add(LinkRow(Loc.T("Project"), "GitHub", () => Open(Branding.GitHubUrl)));
        root.Children.Add(LinkRow(Loc.T("Website"), Branding.WebsiteHost, () => Open(Branding.Website)));
        root.Children.Add(LinkRow(Loc.T("Email"), Branding.Email, () => Open("mailto:" + Branding.Email)));

        var close = new Button { Content = Loc.T("Close"), Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
    }

    static TextBlock Muted(string text) { var t = new TextBlock { Text = text, FontSize = 12 }; t.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return t; }

    /// <summary>One row, several names, each its own clickable link to that person's own GitHub page - "Created by
    /// X &amp; Y" with X and Y each going somewhere different when clicked.</summary>
    static UIElement CreatorsRow(string label, params (string Name, string Url)[] people)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = label, Width = 80 }.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));
        var text = new TextBlock { FontWeight = FontWeights.SemiBold };
        for (var i = 0; i < people.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new Run(" & "));
            var (name, url) = people[i];
            var link = new Hyperlink(new Run(name)) { TextDecorations = null };
            link.Click += (_, _) => Open(url);
            text.Inlines.Add(link);
        }
        row.Children.Add(text);
        return row;
    }

    static UIElement LinkRow(string label, string display, Action onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = label, Width = 80 }.Tap(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));
        var link = new Hyperlink(new Run(display)) { TextDecorations = null };
        link.Click += (_, _) => onClick();
        var text = new TextBlock(link);
        row.Children.Add(text);
        return row;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception) { /* no default handler for it; nothing more we can do */ }
    }
}
