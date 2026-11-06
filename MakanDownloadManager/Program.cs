using System;
using System.IO;
using System.Windows;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = PortableModeService.DataDirectory;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "startup_crash.txt"), ex.ToString());
            }
            catch { }
            MessageBox.Show("Epsilon Download Manager failed to start:\n\n" + ex.Message + "\n\n" + ex.StackTrace, "Epsilon Download Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
