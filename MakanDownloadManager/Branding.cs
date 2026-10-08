namespace MakanDownloadManager;

/// <summary>Single source of truth for product identity and public branding.</summary>
public static class Branding
{
    public const string ProductName = "Epsilon Download Manager";
    public const string ShortName = "EPSILON";
    public const string Creator = "Makan A.D.";
    public const string CoCreator = "Uhnohh";
    public const string Website = "https://makanlab.tech";
    public const string WebsiteHost = "makanlab.tech";
    public const string Email = "makan20042002@gmail.com";
    public const string SupportEmail = "epsilondownloadmanager@gmail.com";
    public static readonly string[] BugReportEmails = { SupportEmail, Email };
    public const string GitHubUrl = "https://github.com/makan20042002/Epsilon-Download-Manager";
    public const string CreatorGitHubUrl = "https://github.com/makan20042002";
    public const string CoCreatorGitHubUrl = "https://github.com/Uhnohh";
    public const string Tagline = "Fast, intelligent, private — built for Windows";
    public const string Version = "1.7.1";

    public static string Copyright => $"Created by {Creator}  ·  {WebsiteHost}";
}
