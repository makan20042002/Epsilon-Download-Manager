using System.Text.Json;

namespace MakanDownloadManager.Services;

/// <summary>One download category (IDM's "Save to" categories): which file types it holds and where they go.</summary>
public sealed class CategoryDef
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();     // ".zip", ".rar" ... (lower case, with dot)
    public string? Folder { get; set; }                       // null = <default download folder>\<Name>

    public CategoryDef Clone() => new() { Name = Name, Extensions = Extensions.ToList(), Folder = Folder };
}

/// <summary>
/// Maps file names to categories (Compressed, Documents, Music, Programs, Video and any the user adds) and remembers each category's folder.
/// "General" is the catch-all for everything not listed elsewhere and can't be deleted.
/// </summary>
public static class CategoryService
{
    public const string General = "General";

    static readonly object Gate = new();
    static List<CategoryDef> _categories = Defaults();

    public static List<CategoryDef> Defaults() => new()
    {
        new() { Name = "Compressed", Extensions = Dotted("zip rar 7z tar gz bz2 xz tgz cab ace arj lzh z sit sitx sea") },
        new() { Name = "Documents", Extensions = Dotted("pdf doc docx xls xlsx ppt pptx txt csv epub mobi rtf odt pps") },
        new() { Name = "Music", Extensions = Dotted("mp3 wav flac aac m4a ogg opus wma weba ac3 aif mpa ra") },
        new() { Name = "Programs", Extensions = Dotted("exe msi iso dmg pkg apk deb rpm bat cmd jar appx msix msu bin img") },
        new() { Name = "Video", Extensions = Dotted("mp4 mkv webm avi mov wmv m4v flv ts mpg mpeg mpe 3gp ogv qt rm rmvb") },
        new() { Name = General }
    };

    static List<string> Dotted(string list) => list.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(e => "." + e.ToLowerInvariant()).ToList();

    /// <summary>Category names in display order, "General" last.</summary>
    public static IReadOnlyList<string> Names { get { lock (Gate) return _categories.Select(c => c.Name).ToList(); } }
    public static IReadOnlyList<string> NamesWithoutGeneral { get { lock (Gate) return _categories.Where(c => c.Name != General).Select(c => c.Name).ToList(); } }
    public static IReadOnlyList<CategoryDef> Categories { get { lock (Gate) return _categories.Select(c => c.Clone()).ToList(); } }

    public static string For(string path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        if (ext.Length == 0) return General;
        lock (Gate) return _categories.FirstOrDefault(c => c.Name != General && c.Extensions.Contains(ext))?.Name ?? General;
    }

    /// <summary>Replaces the category list (an empty or missing "General" is added back).</summary>
    public static void Configure(IEnumerable<CategoryDef>? categories)
    {
        var list = (categories ?? Enumerable.Empty<CategoryDef>()).Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => c.Clone()).ToList();
        foreach (var c in list) { c.Name = c.Name.Trim(); c.Extensions = c.Extensions.Select(NormalizeExtension).Where(e => e.Length > 1).Distinct().ToList(); }
        list = list.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var general = list.FirstOrDefault(c => c.Name == General) ?? new CategoryDef { Name = General };
        list.Remove(general);
        general.Extensions = new();                        // General holds "everything else"
        list.Add(general);
        lock (Gate) _categories = list;
    }

    public static string NormalizeExtension(string ext)
    {
        ext = (ext ?? "").Trim().ToLowerInvariant();
        return ext.Length == 0 ? "" : (ext.StartsWith('.') ? ext : "." + ext);
    }

    /// <summary>Folder for a category: its own folder if one was set, otherwise a sub-folder of the default download folder ("General" uses the default folder itself).</summary>
    public static string FolderFor(string category, string defaultDownloadDir)
    {
        CategoryDef? def;
        lock (Gate) def = _categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase));
        if (def != null && !string.IsNullOrWhiteSpace(def.Folder)) return def.Folder!;
        return def == null || def.Name == General ? defaultDownloadDir : Path.Combine(defaultDownloadDir, def.Name);
    }

    public static void SetFolder(string category, string folder)
    {
        lock (Gate) { var def = _categories.FirstOrDefault(c => string.Equals(c.Name, category, StringComparison.OrdinalIgnoreCase)); if (def != null) def.Folder = folder; }
    }

    // ---- persistence (one JSON setting) ------------------------------------------------------------------------------------

    public static string ToJson() { lock (Gate) return JsonSerializer.Serialize(_categories); }

    public static void LoadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { Configure(Defaults()); return; }
        try { Configure(JsonSerializer.Deserialize<List<CategoryDef>>(json)); }
        catch (JsonException) { Configure(Defaults()); }
    }
}
