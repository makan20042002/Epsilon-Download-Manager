using System.Windows;
using System.Windows.Input;

namespace MakanDownloadManager;

/// <summary>IDM's "drop target": a small floating icon that accepts links dragged from a browser or text.</summary>
public partial class DropTargetWindow : Window
{
    public DropTargetWindow()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = double.TryParse(App.Db.Get("drop_x"), out var x) ? Math.Clamp(x, area.Left, area.Right - Width) : area.Right - Width - 24;
        Top = double.TryParse(App.Db.Get("drop_y"), out var y) ? Math.Clamp(y, area.Top, area.Bottom - Height) : area.Bottom - Height - 60;
        LocationChanged += (_, _) => { App.Db.Set("drop_x", ((int)Left).ToString()); App.Db.Set("drop_y", ((int)Top).ToString()); };
    }

    static string? TextOf(IDataObject data) =>
        data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText)?.ToString()
        : data.GetDataPresent(DataFormats.Text) ? data.GetData(DataFormats.Text)?.ToString() : null;

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TextOf(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        Card.Opacity = 1;
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => Card.Opacity = 0.85;

    void Window_Drop(object sender, DragEventArgs e)
    {
        Card.Opacity = 0.85;
        var text = TextOf(e.Data);
        if (text != null) (Application.Current.MainWindow as MainWindow)?.HandleDroppedText(text);
        e.Handled = true;
    }

    void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void Window_DoubleClick(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);
    void Open_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ShowAndActivate();
    void Hide_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.SetDropTarget(false);
}
