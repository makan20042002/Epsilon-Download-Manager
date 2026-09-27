using System.Windows;
using MakanDownloadManager.Services;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

public enum DownloadChoice { Cancel, Start, Later }

/// <summary>IDM's "Download File Info" step: nothing is downloaded until the user picks Start Download or Download Later.</summary>
public partial class DownloadInfoDialog : Window
{
    bool _programmatic;

    public DownloadChoice Choice { get; private set; } = DownloadChoice.Cancel;
    public string FileName => NameBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();
    /// <summary>The user typed their own name (so a name suggested by the server must not replace it).</summary>
    public bool NameEdited { get; private set; }
    /// <summary>The name came from the server's answer (Content-Disposition), not from the URL.</summary>
    public bool NameFromServer { get; private set; }
    /// <summary>The queue "Download Later" puts the download in.</summary>
    public int QueueId => (QueueBox.SelectedItem as DownloadQueue)?.Id ?? App.Queues.Main.Id;

    /// <param name="batch">Several links: the name box is hidden, names come from the servers.</param>
    public DownloadInfoDialog(string title, string address, string fileName, string folder, string info, bool batch = false)
    {
        InitializeComponent();
        Title = title;
        QueueBox.ItemsSource = App.Queues.Queues;
        QueueBox.SelectedIndex = 0;
        if (!App.Settings.AskQueueOnLater) { QueueLabel.Visibility = Visibility.Collapsed; QueueBox.Visibility = Visibility.Collapsed; }
        if (App.Settings.OnlyAddToQueue)
        {
            // Options > Downloads: "Do not start downloading, only add files to the queue"
            StartButton.Visibility = Visibility.Collapsed; StartButton.IsDefault = false;
            LaterButton.IsDefault = true; LaterButton.Content = "Add to queue"; LaterButton.Style = (Style)FindResource("PrimaryButton");
        }
        UrlBox.Text = address;
        FolderBox.Text = folder;
        InfoText.Text = info;
        if (batch) { NameLabel.Visibility = Visibility.Collapsed; NameBox.Visibility = Visibility.Collapsed; }
        else
        {
            SetName(fileName);
            NameBox.TextChanged += (_, _) => { if (!_programmatic) NameEdited = true; };
        }
        Loaded += (_, _) =>
        {
            if (batch) return;
            NameBox.Focus();
            var dot = NameBox.Text.LastIndexOf('.');
            NameBox.Select(0, dot > 0 ? dot : NameBox.Text.Length);   // select the name, keep the extension
        };
    }

    void SetName(string name) { _programmatic = true; NameBox.Text = name; _programmatic = false; }

    public void SetInfo(string text) => InfoText.Text = Loc.T(text);

    /// <summary>Called when the server answers: use its real file name unless the user already typed one.</summary>
    public void SetNameFromServer(string name)
    {
        if (NameEdited || string.IsNullOrWhiteSpace(name)) return;
        SetName(name);
        NameFromServer = true;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save", UseDescriptionForTitle = true, SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }

    bool Valid()
    {
        if (NameBox.Visibility == Visibility.Visible && FileName.Length == 0) { Dlg.Show(this, "Enter a file name.", "Makan", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (Folder.Length == 0) { Dlg.Show(this, "Choose a folder.", "Makan", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        return true;
    }

    void Start_Click(object sender, RoutedEventArgs e) { if (!Valid()) return; Choice = DownloadChoice.Start; DialogResult = true; }
    void Later_Click(object sender, RoutedEventArgs e) { if (!Valid()) return; Choice = DownloadChoice.Later; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { Choice = DownloadChoice.Cancel; DialogResult = false; }
}
