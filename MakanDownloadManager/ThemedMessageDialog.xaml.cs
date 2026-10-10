using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>Theme-aware replacement for the rectangular Windows message box used throughout Epsilon.</summary>
public partial class ThemedMessageDialog : Window
{
    readonly MessageBoxButton _buttons;
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ThemedMessageDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        _buttons = buttons;
        Title = caption;
        CaptionText.Text = caption;
        MessageText.Text = text;
        FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ConfigureIcon(icon);
        ConfigureButtons(buttons);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    void ConfigureIcon(MessageBoxImage icon)
    {
        var (glyph, brushKey) = icon switch
        {
            MessageBoxImage.Warning => ("!", "Warning"),
            MessageBoxImage.Error => ("×", "Danger"),
            MessageBoxImage.Information => ("i", "Accent"),
            MessageBoxImage.Question => ("?", "Accent"),
            _ => ("i", "Accent")
        };
        IconGlyph.Text = glyph;
        if (TryFindResource(brushKey) is Brush brush)
        {
            IconGlyph.Foreground = brush;
            IconBadge.BorderBrush = brush;
        }
    }

    void ConfigureButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("OK", MessageBoxResult.OK, primary: true, isDefault: true);
                AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("Yes", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("No", MessageBoxResult.No, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Yes", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("No", MessageBoxResult.No);
                AddButton("Cancel", MessageBoxResult.Cancel, isCancel: true);
                break;
        }
    }

    void AddButton(string label, MessageBoxResult result, bool primary = false, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = Loc.T(label),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Style = (Style)FindResource(primary ? "DialogPrimaryButton" : "DialogButton")
        };
        button.Click += (_, _) => Finish(result);
        ButtonsPanel.Children.Add(button);
    }

    void Finish(MessageBoxResult result)
    {
        Result = result;
        DialogResult = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Finish(CancelResult());

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Finish(CancelResult());
    }

    MessageBoxResult CancelResult() => _buttons switch
    {
        MessageBoxButton.YesNo => MessageBoxResult.No,
        MessageBoxButton.OK => MessageBoxResult.OK,
        _ => MessageBoxResult.Cancel
    };

    void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
