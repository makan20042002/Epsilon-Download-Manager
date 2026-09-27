using System.Windows;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

public partial class InputDialog : Window
{
    public string Url => UrlBox.Text.Trim();
    public string Cookie => CookieBox.Text.Trim();
    public string Referrer => RefBox.Text.Trim();
    public string? SaveFolder => string.IsNullOrWhiteSpace(FolderBox.Text) ? null : FolderBox.Text.Trim();
    public int ConnectionCount => (int)ConnSlider.Value;
    public long SpeedLimitBytesPerSec => long.TryParse(LimitBox.Text, out var kb) ? Math.Max(0, kb) * 1024 : 0;

    public InputDialog()
    {
        InitializeComponent();
        ConnSlider.Value = App.Manager.DefaultConnections;
        FolderBox.Text = App.Db.Get("download_dir") ?? "";
        Loaded += (_, _) => { UrlBox.Focus(); UrlBox.SelectAll(); };
    }

    public void Prefill(string url) => UrlBox.Text = url;

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save this download", UseDescriptionForTitle = true, SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") DialogResult = true;
        else Dlg.Show("Enter a valid HTTP/HTTPS link.", "Makan", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
