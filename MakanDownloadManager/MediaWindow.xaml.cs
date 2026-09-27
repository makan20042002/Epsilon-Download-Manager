using System.Windows;
using Microsoft.Win32;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public partial class MediaWindow : Window
{
    readonly MediaService _media = new();
    // Session details handed over by the browser extension (needed for streams behind a login).
    readonly string? _cookie, _referrer, _userAgent;
    CancellationTokenSource? _running;

    public MediaWindow() : this(null, null, null, null) { }

    public MediaWindow(string? url, string? cookie, string? referrer, string? userAgent)
    {
        InitializeComponent();
        _cookie = cookie; _referrer = referrer; _userAgent = userAgent;
        Ffmpeg.Text = App.Db.Get("ffmpeg_path") ?? "ffmpeg.exe";
        if (!string.IsNullOrWhiteSpace(url))
        {
            Url.Text = url;
            Loaded += (_, _) => Inspect_Click(this, new RoutedEventArgs());
        }
        Closed += (_, _) => _running?.Cancel();
    }

    async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Status.Text = "Reading manifest…";
            Streams.ItemsSource = await _media.GetStreamsAsync(Url.Text.Trim(), _cookie, _referrer, _userAgent);
            Status.Text = $"Found {Streams.Items.Count} stream variant(s). Select one to download.";
            if (Streams.Items.Count > 0) Streams.SelectedIndex = Streams.Items.Count - 1; // last = usually best quality
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    async void Grab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var urls = await new SiteGrabber().ExtractAsync(Url.Text.Trim());
            Status.Text = $"Found {urls.Count} HTTP/HTTPS assets.";
            Dlg.Show(string.Join(Environment.NewLine, urls.Take(100)), "Makan Site Grabber");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Streams.SelectedItem is not MediaStreamInfo stream) throw new InvalidOperationException("Select a media stream first.");
            await StartFfmpeg(stream.Url.Split('#')[0], stream.Quality.Replace('x', '_'), stream.AudioUrl);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    async void Source_Click(object sender, RoutedEventArgs e)
    {
        try { await StartFfmpeg(Url.Text.Trim(), "media", null); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    async Task StartFfmpeg(string input, string name, string? audioUrl)
    {
        if (_running != null) { Status.Text = "A stream is already being saved. Please wait for it to finish."; return; }
        var save = new SaveFileDialog
        {
            FileName = Sanitize(name) + ".mp4", Filter = "MP4|*.mp4|MKV|*.mkv|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        if (save.ShowDialog() != true) return;

        var ffmpeg = Ffmpeg.Text.Trim();
        App.Db.Set("ffmpeg_path", ffmpeg); App.Manager.FfmpegPath = ffmpeg;
        _running = new CancellationTokenSource();
        try
        {
            Status.Text = "FFmpeg is saving the stream… (closing this window cancels it)";
            await _media.RunFfmpegAsync(ffmpeg, input, save.FileName, _cookie, _referrer, _userAgent, audioUrl, _running.Token);
            Status.Text = "Complete: " + save.FileName;
        }
        catch (OperationCanceledException) { Status.Text = "Cancelled."; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _running?.Dispose(); _running = null; }
    }

    static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string((string.IsNullOrWhiteSpace(value) ? "media" : value).Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
