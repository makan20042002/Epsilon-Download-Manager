using System.Windows;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>Message boxes in the current language (Persian is shown right-to-left).</summary>
public static class Dlg
{
    const string DefaultCaption = "Epsilon Download Manager";

    public static MessageBoxResult Show(string text, string caption = DefaultCaption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        Show(null, text, caption, buttons, icon);

    public static MessageBoxResult Show(Window? owner, string text, string caption = DefaultCaption, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        text = Loc.T(text); caption = Loc.T(caption);
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                return dispatcher.Invoke(() => Show(owner, text, caption, buttons, icon));

            var dialog = new ThemedMessageDialog(text, caption, buttons, icon);
            if (owner != null && owner.IsVisible) dialog.Owner = owner;
            else if (Application.Current?.MainWindow is { IsVisible: true } main) dialog.Owner = main;
            dialog.ShowDialog();
            return dialog.Result;
        }
        catch
        {
            // Startup/recovery fallback: a warning must remain visible even if WPF resources are unavailable.
            var options = Loc.IsRtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
            return owner != null && owner.IsVisible
                ? MessageBox.Show(owner, text, caption, buttons, icon, MessageBoxResult.None, options)
                : MessageBox.Show(text, caption, buttons, icon, MessageBoxResult.None, options);
        }
    }
}
