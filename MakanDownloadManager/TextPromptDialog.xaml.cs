using System.Windows;

namespace MakanDownloadManager;

/// <summary>A one-line question ("Which address?").</summary>
public partial class TextPromptDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public TextPromptDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    void Ok_Click(object sender, RoutedEventArgs e) { if (Value.Length > 0) DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
