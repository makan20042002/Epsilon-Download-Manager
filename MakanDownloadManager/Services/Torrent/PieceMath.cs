namespace MakanDownloadManager.Services.Torrent;

/// <summary>
/// The arithmetic behind "how much of this file is verified" and the pieces bar. Pure: it only needs the layout
/// of the torrent and a way to ask "is piece N verified?", so it can be tested without a network.
/// </summary>
public static class PieceMath
{
    static long PieceSize(int piece, int pieceLength, long totalLength)
    {
        var count = (int)((totalLength + pieceLength - 1) / pieceLength);
        return piece == count - 1 ? totalLength - (long)pieceLength * (count - 1) : pieceLength;
    }

    /// <summary>Verified bytes of one file, wherever the verified pieces are.</summary>
    public static long VerifiedBytes(long fileOffset, long fileLength, int pieceLength, long totalLength, Func<int, bool> have)
        => Walk(fileOffset, fileLength, pieceLength, totalLength, have, stopAtGap: false);

    /// <summary>Verified bytes counted from the start of the file up to the first missing piece: the part that is safe to play or copy.</summary>
    public static long VerifiedPrefix(long fileOffset, long fileLength, int pieceLength, long totalLength, Func<int, bool> have)
        => Walk(fileOffset, fileLength, pieceLength, totalLength, have, stopAtGap: true);

    static long Walk(long fileOffset, long fileLength, int pieceLength, long totalLength, Func<int, bool> have, bool stopAtGap)
    {
        if (fileLength <= 0 || pieceLength <= 0 || totalLength <= 0) return 0;
        var first = (int)(fileOffset / pieceLength);
        var last = (int)((fileOffset + fileLength - 1) / pieceLength);
        long done = 0;
        for (var piece = first; piece <= last; piece++)
        {
            if (!have(piece)) { if (stopAtGap) break; continue; }
            var pieceStart = (long)piece * pieceLength;
            var start = Math.Max(fileOffset, pieceStart);
            var end = Math.Min(fileOffset + fileLength, pieceStart + PieceSize(piece, pieceLength, totalLength));
            done += Math.Max(0, end - start);
        }
        return Math.Min(done, fileLength);
    }

    /// <summary>The pieces bar: for each of <paramref name="buckets"/> parts of the torrent, the share (0..1) of verified pieces.</summary>
    public static double[] BucketMap(int pieceCount, int buckets, Func<int, bool> have)
    {
        var map = new double[Math.Max(1, buckets)];
        if (pieceCount <= 0) return map;
        for (var b = 0; b < map.Length; b++)
        {
            int from = (int)((long)pieceCount * b / map.Length), to = Math.Max(from + 1, (int)((long)pieceCount * (b + 1) / map.Length));
            to = Math.Min(to, pieceCount);
            if (from >= to) { map[b] = map[Math.Max(0, b - 1)]; continue; }
            var count = 0;
            for (var p = from; p < to; p++) if (have(p)) count++;
            map[b] = count / (double)(to - from);
        }
        return map;
    }
}
