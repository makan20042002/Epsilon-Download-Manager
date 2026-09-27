namespace MakanDownloadManager.Services;

public sealed record SpeedSample(DateTime TimestampUtc, long BytesPerSecond);
