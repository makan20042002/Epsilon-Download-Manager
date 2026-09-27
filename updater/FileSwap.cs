namespace MakanUpdater;

/// <summary>
/// Replaces the files of an installation with the files of a staged update, and undoes everything if anything goes wrong.
/// Existing files are renamed first (a running program can be renamed but not overwritten), so the updater can replace itself,
/// and folders the update does not contain (for example Makan's portable "data" folder) are never touched.
/// </summary>
public static class FileSwap
{
    /// <param name="beforeCopy">Called with the target path before each file is copied (lets a test simulate a failure).</param>
    public static void Apply(string stageDirectory, string installDirectory, Action<string>? beforeCopy = null)
    {
        var stamp = ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var done = new List<(string Destination, string? Backup)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(installDirectory, Path.GetRelativePath(stageDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backup = null;
                if (File.Exists(destination))
                {
                    backup = destination + stamp;
                    File.Move(destination, backup);
                }
                done.Add((destination, backup));
                beforeCopy?.Invoke(destination);
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch
        {
            Rollback(done);
            throw;
        }
        foreach (var (_, backup) in done)
            if (backup != null) TryDelete(backup);      // the old copy of a running program cannot be deleted yet: CleanOld removes it later
    }

    static void Rollback(List<(string Destination, string? Backup)> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (destination, backup) = done[i];
            TryDelete(destination);
            try { if (backup != null && File.Exists(backup)) File.Move(backup, destination, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Removes "*.old-*" leftovers of earlier updates.</summary>
    public static void CleanOld(string installDirectory)
    {
        try { foreach (var file in Directory.EnumerateFiles(installDirectory, "*.old-*", SearchOption.AllDirectories)) TryDelete(file); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
