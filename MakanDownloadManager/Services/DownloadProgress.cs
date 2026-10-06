namespace MakanDownloadManager.Services;

public sealed record DownloadProgress(
    double Percent,
    string SpeedText,
    string SizeText,
    string Status,
    long Done,
    long? Total);

/// <summary>One connection of a running download, for the progress window.</summary>
public sealed record ConnectionInfo(int Index, long Downloaded, string Info, string Network = "");
