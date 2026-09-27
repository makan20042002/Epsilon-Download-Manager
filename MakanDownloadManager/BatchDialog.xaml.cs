using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>IDM's "Add batch download": one address with a * that is replaced by a range of numbers or letters.</summary>
public partial class BatchDialog : Window
{
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();

    public BatchDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { PatternBox.Focus(); UpdatePreview(); };
    }

    /// <summary>Builds the list from the form; throws ArgumentException with a readable message when something is wrong.</summary>
    IReadOnlyList<string> Build()
    {
        var kind = LettersRadio.IsChecked == true ? WildcardKind.Letters : WildcardKind.Numbers;
        if (kind == WildcardKind.Numbers)
        {
            if (!int.TryParse(FromBox.Text.Trim(), out var from) || !int.TryParse(ToBox.Text.Trim(), out var to) || !int.TryParse(StepBox.Text.Trim(), out var step))
                throw new ArgumentException("From, to and step must be whole numbers.");
            return UrlText.ExpandWildcard(PatternBox.Text.Trim(), kind, from, to, step, ZeroFill.IsChecked == true);
        }
        var a = LetterFromBox.Text.Trim(); var z = LetterToBox.Text.Trim();
        if (a.Length != 1 || z.Length != 1) throw new ArgumentException("Type one letter in each box.");
        return UrlText.ExpandWildcard(PatternBox.Text.Trim(), kind, 0, 0, 1, false, a[0], z[0]);
    }

    void UpdatePreview()
    {
        if (PreviewText == null) return;
        if (string.IsNullOrWhiteSpace(PatternBox.Text)) { PreviewText.Text = ""; return; }
        try
        {
            var urls = Build();
            PreviewText.Text = $"{urls.Count} addresses, from\n{urls[0]}\nto\n{urls[^1]}";
        }
        catch (ArgumentException ex) { PreviewText.Text = ex.Message; }
    }

    void Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdatePreview(); }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        try { Urls = Build(); }
        catch (ArgumentException ex) { Dlg.Show(this, ex.Message, "Add Batch Download", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!Uri.TryCreate(Urls[0], UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        { Dlg.Show(this, "The address must start with http:// or https://", "Add Batch Download", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
