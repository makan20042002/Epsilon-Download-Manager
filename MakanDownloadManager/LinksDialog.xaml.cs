using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using Forms = System.Windows.Forms;

namespace MakanDownloadManager;

/// <summary>One line of the "Download all links" window.</summary>
public sealed class LinkRow : INotifyPropertyChanged
{
    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico", ".avif" };
    static readonly HashSet<string> HtmlExts = new(StringComparer.OrdinalIgnoreCase) { "", ".html", ".htm", ".php", ".asp", ".aspx", ".jsp", ".shtml", ".cgi" };
    static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "Matroska video", [".mp4"] = "MP4 video", [".avi"] = "AVI video", [".mov"] = "QuickTime video", [".webm"] = "WebM video", [".flv"] = "Flash video",
        [".m4v"] = "MP4 video", [".wmv"] = "Windows video", [".m3u8"] = "HLS playlist", [".mpd"] = "DASH manifest", [".ts"] = "Transport stream",
        [".mp3"] = "MP3 audio", [".m4a"] = "AAC audio", [".flac"] = "FLAC audio", [".wav"] = "WAV audio", [".ogg"] = "Ogg audio", [".opus"] = "Opus audio",
        [".zip"] = "ZIP archive", [".rar"] = "RAR archive", [".7z"] = "7-Zip archive", [".tar"] = "TAR archive", [".gz"] = "GZip archive",
        [".exe"] = "Application", [".msi"] = "Installer", [".iso"] = "Disc image", [".apk"] = "Android app", [".dmg"] = "Disk image",
        [".pdf"] = "PDF document", [".doc"] = "Word document", [".docx"] = "Word document", [".xls"] = "Excel workbook", [".xlsx"] = "Excel workbook",
        [".ppt"] = "PowerPoint", [".pptx"] = "PowerPoint", [".epub"] = "E-book", [".srt"] = "Subtitles", [".vtt"] = "Subtitles", [".torrent"] = "Torrent file"
    };

    public string Url { get; }
    public string Text { get; }
    public string Kind { get; }
    public string Extension { get; }
    public string Host { get; }
    public string? Cookie { get; }
    public bool IsHtml { get; private set; }
    public bool NameEdited { get; private set; }
    public bool NameFromServer { get; private set; }
    public bool Probed { get; set; }

    string _fileName = "", _typeName = "", _sizeText = "", _savePath = "";
    bool _selected;

    public LinkRow(LinkEntry entry, string? cookie)
    {
        Url = entry.Url; Text = entry.Text ?? ""; Kind = entry.Kind ?? "link"; Cookie = cookie;
        var uri = new Uri(entry.Url);
        Host = uri.Host;
        var name = "";
        try { name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)); } catch (UriFormatException) { }
        Extension = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(name)) name = uri.Host;
        _fileName = DownloadFileNamer.Sanitize(name, "download.bin");
        IsHtml = Kind == "link" && HtmlExts.Contains(Extension);
        _typeName = IsHtml ? "Web page" : IsImage ? "Image" : TypeNameFor(Extension);
        // pre-tick what looks like a real download (video, music, archives, programs, documents)
        _selected = !IsHtml && !IsImage && CategoryService.For(_fileName) != CategoryService.General;
    }

    public bool IsImage => Kind == "image" || ImageExts.Contains(Extension);

    static string TypeNameFor(string ext) =>
        TypeNames.TryGetValue(ext, out var n) ? n : ext.Length > 1 ? ext.TrimStart('.').ToUpperInvariant() + " file" : "File";

    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    /// <summary>Editable in the grid; typing a name marks it as the user's own.</summary>
    public string FileName { get => _fileName; set { if (_fileName == value) return; _fileName = value; NameEdited = true; Changed(); } }
    public string TypeName { get => _typeName; private set { _typeName = value; Changed(); } }
    public string SizeText { get => _sizeText; private set { _sizeText = value; Changed(); } }
    public string SavePath { get => _savePath; set { _savePath = value; Changed(); } }

    /// <summary>The server answered: real size and type (and name, for links like download.php?id=5).</summary>
    public void ApplyProbe(ProbeResult probe)
    {
        Probed = true;
        SizeText = probe.Length is > 0 ? DownloadItem.FormatBytes(probe.Length.Value) : "";
        var type = probe.ContentType ?? "";
        if (type is "text/html" or "application/xhtml+xml") { IsHtml = true; TypeName = "Web page"; Selected = false; return; }
        if (IsHtml) IsHtml = false;                                   // looked like a page, but it is a file
        if (!string.IsNullOrWhiteSpace(probe.FileName) && !NameEdited && !NameFromServer)
        {
            NameFromServer = true; _fileName = probe.FileName; Changed(nameof(FileName));
            var ext = Path.GetExtension(_fileName);
            if (TypeNames.ContainsKey(ext)) TypeName = TypeNameFor(ext);
        }
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) TypeName = "Image";
        else if (TypeName is "Web page" or "File") TypeName = type.Length > 0 ? type : TypeName;
        if (!IsHtml && !IsImage && CategoryService.For(_fileName) != CategoryService.General && !NameEdited) Selected = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>IDM's "Download All Links" window: every link of a page with type and size, tick some, choose where they go.</summary>
public partial class LinksDialog : Window
{
    readonly ObservableCollection<LinkRow> _rows = new();
    readonly ICollectionView _view;
    readonly LinksPrompt _prompt;
    readonly CancellationTokenSource _cts = new();
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool _dirty;

    public DownloadChoice Choice { get; private set; } = DownloadChoice.Cancel;
    public int QueueId => (QueueBox.SelectedItem as DownloadQueue)?.Id ?? App.Queues.Main.Id;
    public IReadOnlyList<LinkRow> Selected => _view.Cast<LinkRow>().Where(r => r.Selected).ToList();

    public LinksDialog(LinksPrompt prompt)
    {
        InitializeComponent();
        _prompt = prompt;
        Title = string.IsNullOrWhiteSpace(prompt.PageTitle) ? "Download All Links with Makan" : "Download All Links – " + prompt.PageTitle;
        RefreshQueues();
        CategoryBox.ItemsSource = CategoryService.Names.ToList(); CategoryBox.SelectedIndex = CategoryService.Names.ToList().IndexOf("Video");
        FolderBox.Text = MainWindow.FolderForCategory(CategoryService.General);

        foreach (var link in prompt.Links) _rows.Add(new LinkRow(link, prompt.Cookies.TryGetValue(new Uri(link.Url).Host, out var c) ? c : null));
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is LinkRow r && !(HideHtml.IsChecked == true && r.IsHtml) && !(HideImages.IsChecked == true && r.IsImage);
        Grid.ItemsSource = _view;
        UpdateSavePaths();
        UpdateCount();

        _refresh.Tick += (_, _) => { if (_dirty) { _dirty = false; _view.Refresh(); UpdateCount(); } };
        _refresh.Start();
        Loaded += (_, _) => _ = ProbeAllAsync();
        Closed += (_, _) => { _cts.Cancel(); _refresh.Stop(); };
        foreach (var row in _rows) row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(LinkRow.Selected) or nameof(LinkRow.FileName)) { UpdateCount(); if (e.PropertyName == nameof(LinkRow.FileName)) UpdateSavePaths(); } };
    }

    // ---------------------------------------------------------------- sizes and types (checked in the background)

    async Task ProbeAllAsync()
    {
        using var gate = new SemaphoreSlim(6);
        var work = _rows.Where(r => !r.IsImage).Take(400).Select(async row =>
        {
            try
            {
                await gate.WaitAsync(_cts.Token);
                try
                {
                    var probe = new DownloadItem { Url = row.Url, Cookie = row.Cookie, Referrer = _prompt.Referrer, UserAgent = _prompt.UserAgent };
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    limit.CancelAfter(TimeSpan.FromSeconds(10));
                    var result = await App.Manager.ProbeAsync(probe, limit.Token);
                    Dispatcher.Invoke(() => { row.ApplyProbe(result); UpdateSavePaths(row); _dirty = true; });
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { row.Probed = true; }   // unreachable or refused: leave the row as it is
        });
        await Task.WhenAll(work);
    }

    // ---------------------------------------------------------------- where the files go

    void SaveMode_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateSavePaths(); }
    void FolderBox_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && OneFolder.IsChecked == true) UpdateSavePaths(); }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where to save all files", UseDescriptionForTitle = true, SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        FolderBox.Text = dialog.SelectedPath;
        OneFolder.IsChecked = true;
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

    string FolderFor(LinkRow row)
    {
        if (OneFolder.IsChecked == true && !string.IsNullOrWhiteSpace(FolderBox.Text)) return FolderBox.Text.Trim();
        if (OneCategory.IsChecked == true) return MainWindow.FolderForCategory(CategoryBox.SelectedItem as string ?? CategoryService.General);
        return MainWindow.FolderFor(row.FileName);
    }

    void UpdateSavePaths(LinkRow? only = null)
    {
        IEnumerable<LinkRow> targets = only == null ? _rows : new[] { only };
        foreach (var row in targets)
            row.SavePath = Path.Combine(FolderFor(row), DownloadFileNamer.Sanitize(row.FileName, "download.bin"));
    }

    // ---------------------------------------------------------------- filters and buttons

    void Filter_Changed(object sender, RoutedEventArgs e) { if (_view == null) return; _view.Refresh(); UpdateCount(); }
    void CheckAll_Click(object sender, RoutedEventArgs e) { foreach (var row in _view.Cast<LinkRow>()) row.Selected = true; }
    void UncheckAll_Click(object sender, RoutedEventArgs e) { foreach (var row in _view.Cast<LinkRow>()) row.Selected = false; }
    void UpdateCount() { if (CountText != null && _view != null) CountText.Text = $"{Selected.Count} of {_view.Cast<LinkRow>().Count()} shown links ticked"; }

    void Finish(DownloadChoice choice)
    {
        if (Selected.Count == 0) { Dlg.Show(this, "Tick at least one link.", "Makan", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Choice = choice; DialogResult = true;
    }
    void Start_Click(object sender, RoutedEventArgs e) => Finish(DownloadChoice.Start);
    void Later_Click(object sender, RoutedEventArgs e) => Finish(DownloadChoice.Later);
    void Cancel_Click(object sender, RoutedEventArgs e) { Choice = DownloadChoice.Cancel; DialogResult = false; }
}
