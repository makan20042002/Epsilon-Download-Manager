namespace MakanDownloadManager;

/// <summary>Stable product identities for Epsilon V2. V2 replaces V1 in place (same program names, same installer id); its data folder stays separate, so V1 settings are only imported, never changed.</summary>
public static class ProductIdentity
{
    public const string ProductName = "Epsilon Download Manager";
    public const string Version = "2.0.0";
    public const string ProcessName = "EpsilonDownloadManager";
    public const string ExecutableName = "EpsilonDownloadManager.exe";
    public const string NativeHostExecutableName = "EpsilonNativeHost.exe";
    public const string UpdaterExecutableName = "EpsilonUpdater.exe";
    public const string DataDirectoryName = "EpsilonDownloadManager2";
    public const string LegacyDataDirectoryName = "MakanDownloadManager";
    public const string SingleInstanceName = "EpsilonDownloadManager2.SingleInstance";
    public const string NativeHostName = "com.makan.downloadmanager";
    public const string NativePipeName = "com.makan.downloadmanager.v2";
}
