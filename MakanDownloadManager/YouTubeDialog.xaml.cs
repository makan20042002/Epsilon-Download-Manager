using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

/// <summary>
/// A YouTube (or other yt-dlp) link: shows the choices yt-dlp found - qualities, audio only, subtitles - like IDM's video list,
/// then Start Download / Download Later.
/// </summary>
public partial class YouTubeDialog : Window
{
    sealed record Row(YtOption Option, string Label, string Kind, string Size);

    readonly string _url;
    readonly CancellationTokenSource _cts = new();
    YtVideoInfo? _info;

    public DownloadChoice Choice { get; private set; } = DownloadChoice.Cancel;
    public YtOption? Selected => (OptionList.SelectedItem as Row)?.Option;
    public string Folder => FolderBox.Text.Trim();
    public int QueueId => (QueueBox.SelectedItem as DownloadQueue)?.Id ?? App.Queues.Main.Id;
    /// <summary>The video title with the quality added ("Title [1080p]"), the base of the file name.</summary>
    public string ChosenTitle => Selected is { } o ? TitleFor(o) : _info?.Title ?? "video";

    public YouTubeDialog(string url, string folder)
    {
        InitializeComponent();
        _url = url;
        AddressText.Text = url;
        FolderBox.Text = folder;
        RefreshQueues();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    string TitleFor(YtOption o) => o.Kind switch
    {
        "video" => $"{_info?.Title ?? "video"} [{o.Height}p]",
        "audio" => $"{_info?.Title ?? "video"} [audio]",
        _ => _info?.Title ?? "video"
    };

    async Task LoadAsync()
    {
        Busy.Visibility = Visibility.Visible; SetupPanel.Visibility = Visibility.Collapsed; OptionList.Visibility = Visibility.Collapsed;
        StartButton.IsEnabled = LaterButton.IsEnabled = false;
        TitleText.Text = Loc.T("Reading video information…"); InfoText.Text = "";
        var yt = App.Manager.YtDlp;
        if (yt == null) { ShowSetup(Loc.T("YouTube needs yt-dlp. Open Options > YouTube & other sites and click \"Download / update tools\"."), canOpenOptions: true); return; }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(90));
            _info = await yt.ResolveAsync(_url, limit.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { ShowSetup(Loc.T("yt-dlp took too long to read this page."), canOpenOptions: false); return; }
        catch (YtDlpNotReadyException ex) { ShowSetup(Loc.T(ex.Message), canOpenOptions: true); return; }
        catch (Exception ex) { ShowSetup(Loc.F("This video can't be read: {0}", ex.Message), canOpenOptions: false); return; }

        Busy.Visibility = Visibility.Collapsed;
        TitleText.Text = _info.Title;
        InfoText.Text = string.Join("   ·   ", new[] { _info.Uploader, _info.DurationSeconds is > 0 ? TimeSpan.FromSeconds(_info.DurationSeconds.Value).ToString(_info.DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : null }.Where(x => !string.IsNullOrEmpty(x)));
        var rows = _info.Options.Select(o => new Row(o, o.Kind == "subtitle" ? Loc.T(o.Label.Replace("Subtitles: ", "Subtitles: ")) : Loc.T(o.Label), Loc.T(KindText(o)),
            o.ApproxBytes is > 0 ? "~ " + DownloadItem.FormatBytes(o.ApproxBytes.Value) : "")).ToList();
        OptionList.ItemsSource = rows;
        OptionList.Visibility = Visibility.Visible;
        OptionList.SelectedItem = rows.FirstOrDefault(r => r.Option.Height == 1080) ?? rows.FirstOrDefault(r => r.Option.Kind == "video") ?? rows.FirstOrDefault();
        if (rows.Count == 0) ShowSetup(Loc.T("Nothing to download was found on this page."), canOpenOptions: false);
    }

    static string KindText(YtOption o) => o.Kind switch { "video" => "MP4 video (with sound)", "audio" => "Audio only", _ => "Subtitle file (SRT)" };

    void ShowSetup(string message, bool canOpenOptions)
    {
        Busy.Visibility = Visibility.Collapsed;
        TitleText.Text = _info?.Title ?? Loc.T("Video");
        SetupText.Text = message;
        OptionsButton.Visibility = canOpenOptions ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
    }

    void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        var ok = Selected != null;
        StartButton.IsEnabled = LaterButton.IsEnabled = ok;
        if (!ok) { FileNameText.Text = ""; return; }
        var plan = YtDlpService.PlanFor(Selected!.Key);
        var request = new StreamRequest(YtDlpService.WithSelection(_url, Selected.Key), null, ChosenTitle, plan.OutputExtension, null, _url, null);
        FileNameText.Text = NativeBridge.StreamFileName(request);
    }

    void OpenOptions_Click(object sender, RoutedEventArgs e)
    {
        var options = new SettingsWindow { Owner = this };
        options.ShowYouTubeTab();
        options.ShowDialog();
        _ = LoadAsync();
    }

    void Retry_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = Folder, Description = Loc.T("Save to") };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }

    void RefreshQueues(int? selectedId = null)
    {
        QueueBox.ItemsSource = App.Queues.Queues;
        QueueBox.SelectedItem = App.Queues.Find(selectedId ?? App.Queues.Main.Id) ?? App.Queues.Main;
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var ask = new TextPromptDialog("New schedule queue", "Queue name:", "New queue") { Owner = this };
        if (ask.ShowDialog() != true || string.IsNullOrWhiteSpace(ask.Value)) return;
        var queue = App.Queues.AddQueue(ask.Value.Trim());
        RefreshQueues(queue.Id);
    }

    void Start_Click(object sender, RoutedEventArgs e) { Choice = DownloadChoice.Start; DialogResult = true; }
    void Later_Click(object sender, RoutedEventArgs e) { Choice = DownloadChoice.Later; DialogResult = true; }
    void Cancel_Click(object sender, RoutedEventArgs e) { Choice = DownloadChoice.Cancel; DialogResult = false; }
}
