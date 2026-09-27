namespace MakanDownloadManager.Services;

/// <summary>Determines whether Makan should keep its data beside the executable.</summary>
public static class PortableModeService
{
    const string MarkerFile = "portable.mode";
    const string EnvironmentVariable = "MAKAN_PORTABLE";

    public static string ApplicationDirectory => AppContext.BaseDirectory;

    public static bool IsPortable
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase))
                return true;

            return File.Exists(Path.Combine(ApplicationDirectory, MarkerFile));
        }
    }

    public static string DataDirectory => IsPortable
        ? Path.Combine(ApplicationDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakanDownloadManager");
}
