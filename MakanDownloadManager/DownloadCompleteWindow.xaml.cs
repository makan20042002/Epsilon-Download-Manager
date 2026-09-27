using System.Diagnostics;
using System.Windows;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>IDM's "Download complete" window: name, size, and Open / Open folder / Close.</summary>
public partial class DownloadCompleteWindow : Window
{
    static int _open;
    readonly string _path;

    public DownloadCompleteWindow(DownloadItem item)
    {
        InitializeComponent();
        _path = item.FilePath;
        NameText.Text = item.FileName;
        DetailText.Text = item.SizeDisplay.Length > 0 ? item.SizeDisplay + "  ·  " + Path.GetDirectoryName(_path) : Path.GetDirectoryName(_path) ?? "";
        var area = SystemParameters.WorkArea;
        var slot = _open++ % 5;                                   // several finished at once: cascade instead of stacking
        Loaded += (_, _) => { Left = area.Right - ActualWidth - 20 - slot * 14; Top = area.Bottom - ActualHeight - 20 - slot * 26; };
        Closed += (_, _) => _open = Math.Max(0, _open - 1);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        try { if (File.Exists(_path)) Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }); }
        catch (Exception ex) { new DiagnosticsService().Error($"Could not open \"{_path}\"", ex); Dlg.Show(this, ex.Message, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Close();
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(_path) ? $"/select,\"{_path}\"" : $"\"{Path.GetDirectoryName(_path)}\"") { UseShellExecute = true }); }
        catch (Exception ex) { new DiagnosticsService().Error($"Could not open the folder for \"{_path}\"", ex); Dlg.Show(this, ex.Message, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Warning); }
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
