using System.Windows;
using System.Windows.Threading;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>A queue finished and is set to turn the computer off: give the user a chance to say no (like IDM's 60-second warning).</summary>
public partial class PowerCountdownWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly int _total;
    int _left;

    public bool Proceed { get; private set; }

    public PowerCountdownWindow(PowerAction action, int seconds)
    {
        InitializeComponent();
        _total = _left = Math.Max(5, seconds);
        var verb = action switch { PowerAction.Hibernate => "hibernate", PowerAction.Sleep => "go to sleep", PowerAction.Restart => "restart", _ => "shut down" };
        MessageText.Text = $"All downloads in the queue are finished. The computer will {verb} in:";
        Show_();
        _timer.Tick += (_, _) =>
        {
            _left--;
            Show_();
            if (_left <= 0) { Proceed = true; Finish(); }
        };
        _timer.Start();
    }

    void Show_() { CountText.Text = _left.ToString(); Bar.Value = 100.0 * (_total - _left) / _total; }
    void Finish() { _timer.Stop(); DialogResult = Proceed; }
    void Now_Click(object sender, RoutedEventArgs e) { Proceed = true; Finish(); }
    void Cancel_Click(object sender, RoutedEventArgs e) { Proceed = false; Finish(); }
    protected override void OnClosed(EventArgs e) { _timer.Stop(); base.OnClosed(e); }
}
