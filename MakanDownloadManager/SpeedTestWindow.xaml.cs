using System.Globalization;
using System.Windows;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public partial class SpeedTestWindow : Window
{
    readonly SpeedTestService _speedTest = new();
    CancellationTokenSource? _operation;
    SpeedTestResult? _internet;

    public SpeedTestWindow(Window owner, DownloadItem? selected = null)
    {
        InitializeComponent();
        Owner = owner;
        if (selected != null && Uri.TryCreate(selected.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") LinkBox.Text = selected.Url.Split('#')[0];
        DomesticPanel.Visibility = Loc.IsPersian ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => _operation?.Cancel();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) { _operation.Cancel(); return; }
        _operation = new CancellationTokenSource();
        SetBusy(true, StartButton, "Cancel");
        var progress = new Progress<SpeedTestProgress>(p => { InternetProgress.Value = p.Percent; InternetStatus.Text = Loc.T(p.Stage); });
        try
        {
            _internet = await _speedTest.RunAsync(progress, _operation.Token);
            DownloadText.Text = Mbps(_internet.DownloadMbps);
            UploadText.Text = Mbps(_internet.UploadMbps);
            PingText.Text = _internet.PingMs.ToString("0", CultureInfo.CurrentCulture) + " ms";
            JitterText.Text = _internet.JitterMs.ToString("0.0", CultureInfo.CurrentCulture) + " ms";
            InternetStatus.Text = Loc.F("Finished · {0} used", DownloadItem.FormatBytes(_internet.BytesUsed));
        }
        catch (OperationCanceledException) { InternetStatus.Text = Loc.T("Test cancelled."); }
        catch (Exception ex) { InternetStatus.Text = Loc.T("Speed test failed: ") + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false, StartButton, "Start test"); }
    }

    async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) { _operation.Cancel(); return; }
        _operation = new CancellationTokenSource();
        SetBusy(true, LinkButton, "Cancel");
        var progress = new Progress<SpeedTestProgress>(p => { LinkProgress.Value = p.Percent; LinkResultText.Text = Loc.T(p.Stage); });
        try
        {
            Task<IranRouteResult>? routeTask = Loc.IsPersian ? _speedTest.ClassifyIranAsync(LinkBox.Text, _operation.Token) : null;
            var link = await _speedTest.TestLinkAsync(LinkBox.Text, progress, _operation.Token);
            var comparison = _internet == null ? Loc.T("Run the internet test for a direct comparison.")
                : link.Mbps < _internet.DownloadMbps * 0.65 ? Loc.T("This server is much slower than your internet connection.")
                : link.Mbps > _internet.DownloadMbps * 1.15 ? Loc.T("This server is fast; the earlier internet result may have been temporary.")
                : Loc.T("This server is close to your measured internet speed.");
            LinkResultText.Text = Loc.F("{0}: {1} · {2}", link.Host, Mbps(link.Mbps), comparison);
            if (routeTask != null && await routeTask is { } route)
                DomesticText.Text = route.Classification switch
                {
                    "domestic" => $"احتمالاً ترافیک داخلی · {route.Host} · {route.Address}",
                    "international" => $"احتمالاً ترافیک بین‌الملل · {route.Host} · {route.Address}",
                    _ => $"نوع ترافیک نامشخص است · {route.Host} · {route.Address}".TrimEnd(' ', '·')
                };
        }
        catch (OperationCanceledException) { LinkResultText.Text = Loc.T("Test cancelled."); }
        catch (Exception ex) { LinkResultText.Text = Loc.T("Server test failed: ") + ex.Message; }
        finally { _operation?.Dispose(); _operation = null; SetBusy(false, LinkButton, "Test this server"); }
    }

    void SetBusy(bool busy, System.Windows.Controls.Button active, string caption)
    {
        StartButton.IsEnabled = !busy || ReferenceEquals(active, StartButton);
        LinkButton.IsEnabled = !busy || ReferenceEquals(active, LinkButton);
        active.Content = Loc.T(caption);
    }

    static string Mbps(double value) => value.ToString("0.00", CultureInfo.CurrentCulture) + " Mbps";
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
