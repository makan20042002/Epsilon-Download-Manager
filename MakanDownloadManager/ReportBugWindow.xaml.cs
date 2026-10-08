using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

public partial class ReportBugWindow : Window
{
    public ReportBugWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        RecipientText.Text = Loc.F("Email will be prepared for: {0}", string.Join(", ", Branding.BugReportEmails));
    }

    void Prepare_Click(object sender, RoutedEventArgs e)
    {
        var title = SubjectBox.Text.Trim();
        var description = DescriptionBox.Text.Trim();
        if (title.Length == 0 || description.Length == 0)
        {
            StatusText.Text = Loc.T("Please enter a short title and describe the problem.");
            (title.Length == 0 ? SubjectBox : DescriptionBox).Focus();
            return;
        }

        string? report = null;
        try
        {
            if (DiagnosticsCheck.IsChecked == true)
            {
                var database = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakanDownloadManager", "downloads.db");
                report = new DiagnosticsService().ExportReport(database);
            }

            var category = (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Other";
            var subject = $"[Epsilon bug] {category}: {title}";
            var body = new StringBuilder()
                .AppendLine("Hello Epsilon team,").AppendLine()
                .AppendLine("Problem type: " + category)
                .AppendLine("Epsilon version: " + Branding.Version)
                .AppendLine("Windows: " + Environment.OSVersion)
                .AppendLine().AppendLine("What happened:").AppendLine(description);
            if (!string.IsNullOrWhiteSpace(StepsBox.Text)) body.AppendLine().AppendLine("Steps to reproduce:").AppendLine(StepsBox.Text.Trim());
            if (report != null) body.AppendLine().AppendLine("A diagnostic report was created. I will attach this file manually:").AppendLine(report);
            body.AppendLine().AppendLine("Thank you.");

            var recipients = string.Join(",", Branding.BugReportEmails);
            var mailto = $"mailto:{recipients}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body.ToString())}";
            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            if (report != null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{report}\"") { UseShellExecute = true });
            StatusText.Text = report == null
                ? Loc.T("Your email app is open. Review the message, then send it.")
                : Loc.T("Your email app is open. Attach the selected diagnostic ZIP, review the message, then send it.");
        }
        catch (Exception ex)
        {
            new DiagnosticsService().Error("Preparing a bug-report email failed", ex);
            StatusText.Text = Loc.T("Could not open your email app: ") + ex.Message;
        }
    }
}
