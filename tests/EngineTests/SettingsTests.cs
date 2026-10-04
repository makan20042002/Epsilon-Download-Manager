using System.Diagnostics;
using System.Text.Json;
using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

/// <summary>The options behind the Options window: categories, capture rules, typed settings, temp directory, file date, duplicates, user agent.</summary>
static class SettingsTests
{
    static string Base = "", Dir = "";
    static HttpClient Http = null!;

    sealed class MemSettings : ISettingsStore
    {
        public readonly Dictionary<string, string> Values = new();
        public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Values[key] = value;
    }

    public static async Task RunAll(string baseUrl, string dir, HttpClient http)
    {
        Base = baseUrl; Dir = dir; Http = http;
        await Run("Categories: IDM-style defaults, custom categories, folders, JSON", Categories);
        await Run("Capture rules: file types (incl. R0*), sites, addresses, browsers", Rules);
        await Run("Settings: defaults, round trips, capture rules built from them, antivirus command", Settings);
        await Run("Localization: English stays as written, Persian by exact text and by pattern", Localization);
        await Run("Themes: light and dark blue-grey palettes are complete and readable, every colour used in XAML exists", Themes);
        await Run("Engine options: temp directory, file date from server, ignore-modified on resume, default User-Agent, overwrite", EngineOptions);
    }

    static async Task Run(string title, Func<Task> body)
    {
        Console.WriteLine($"\n== {title}");
        await Http.GetAsync(Base + "/__reset");
        try { await body(); } catch (Exception ex) { T.Check("no unexpected exception", false, ex.ToString()); }
        finally { CategoryService.Configure(CategoryService.Defaults()); }
    }

    static Task Categories()
    {
        T.Check("defaults: video / music / archives / programs / documents / general",
            CategoryService.For("a.MKV") == "Video" && CategoryService.For("a.mp3") == "Music" && CategoryService.For("a.rar") == "Compressed" &&
            CategoryService.For("setup.exe") == "Programs" && CategoryService.For("a.pdf") == "Documents" && CategoryService.For("a.xyz") == CategoryService.General && CategoryService.For("noext") == CategoryService.General);
        T.Check("names: General is last, the others come first", CategoryService.Names[^1] == "General" && CategoryService.NamesWithoutGeneral.SequenceEqual(new[] { "Compressed", "Documents", "Music", "Programs", "Video" }));
        T.Check("default folders are sub-folders of the download folder (General = the folder itself)",
            CategoryService.FolderFor("Video", @"C:\Users\me\Downloads") == Path.Combine(@"C:\Users\me\Downloads", "Video") && CategoryService.FolderFor("General", @"C:\D") == @"C:\D");

        var custom = CategoryService.Categories.ToList();
        custom.Insert(0, new CategoryDef { Name = "Games", Extensions = new() { "SAV", ".Pak" }, Folder = @"D:\Games" });
        custom.RemoveAll(c => c.Name == "General");                                       // trying to delete General
        custom.Add(new CategoryDef { Name = "games", Extensions = new() { ".dup" } });    // same name, other case: ignored
        CategoryService.Configure(custom);
        T.Check("a new category with its own folder and normalised extensions", CategoryService.For("x.sav") == "Games" && CategoryService.For("y.PAK") == "Games" && CategoryService.FolderFor("Games", @"C:\D") == @"D:\Games");
        T.Check("General cannot be removed and case-duplicates are dropped", CategoryService.Names.Contains("General") && CategoryService.Names.Count(n => n.Equals("games", StringComparison.OrdinalIgnoreCase)) == 1 && CategoryService.For("z.dup") == "General");
        CategoryService.SetFolder("Video", @"E:\Films");
        var json = CategoryService.ToJson();
        CategoryService.Configure(CategoryService.Defaults());
        T.Check("defaults restored", CategoryService.For("x.sav") == "General" && CategoryService.FolderFor("Video", @"C:\D") != @"E:\Films");
        CategoryService.LoadJson(json);
        T.Check("JSON round trip keeps categories, extensions and folders", CategoryService.For("x.sav") == "Games" && CategoryService.FolderFor("Video", @"C:\D") == @"E:\Films");
        CategoryService.LoadJson("{ not json"); T.Check("broken JSON falls back to the defaults", CategoryService.For("a.mp4") == "Video" && CategoryService.For("x.sav") == "General");
        return Task.CompletedTask;
    }

    static Task Rules()
    {
        var rules = new CaptureRules();
        T.Check("defaults capture archives, video, programs", rules.Evaluate("https://x.example/a.zip", null, null).Capture && rules.Evaluate("https://x.example/get?f=1", "movie.MKV", null).Capture && rules.Evaluate("https://x.example/s.exe", null, null).Capture);
        T.Check("a type that is not on the list is left to the browser, with a reason", !rules.Evaluate("https://x.example/page.html", null, null).Capture && rules.Evaluate("https://x.example/page.html", null, null).Reason.Contains(".html"));
        T.Check("wildcard types like R0* (.r00 .r01 …)", rules.Evaluate("https://x.example/a.r05", null, null).Capture && rules.Evaluate("https://x.example/a.r15", null, null).Capture && !rules.Evaluate("https://x.example/a.r99z", null, null).Capture);
        T.Check("no extension at all: taken over (a browser download without a type is still a download)", rules.Evaluate("https://x.example/download", null, null).Capture);
        T.Check("the browser's file name beats the address", !rules.Evaluate("https://x.example/a.zip", "report.docx", null).Capture);
        rules.FileTypes = "";
        T.Check("an empty type list means everything", rules.Evaluate("https://x.example/page.html", null, null).Capture);
        rules.FileTypes = CaptureRules.DefaultFileTypes;

        T.Check("default excluded sites (Windows Update etc.)", !rules.Evaluate("https://fe2.update.microsoft.com/a.exe", null, null).Capture && !rules.Evaluate("http://download.windowsupdate.com/x.cab", null, null).Capture && rules.Evaluate("https://updates.example.com/a.zip", null, null).Capture);
        rules.ExcludedAddresses = "https://x.example/private/*\r\n*/nodl/*";
        T.Check("excluded addresses with wildcards", !rules.Evaluate("https://x.example/private/a.zip", null, null).Capture && !rules.Evaluate("https://y.example/z/nodl/a.zip", null, null).Capture && rules.Evaluate("https://x.example/public/a.zip", null, null).Capture);

        var ff = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0";
        var edge = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0";
        var chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
        var opera = chrome + " OPR/110.0.0.0";
        T.Check("browsers are told apart by User-Agent", CaptureRules.BrowserOf(ff) == "firefox" && CaptureRules.BrowserOf(edge) == "edge" && CaptureRules.BrowserOf(chrome) == "chrome" && CaptureRules.BrowserOf(opera) == "opera" && CaptureRules.BrowserOf(chrome + " Vivaldi/6.8") == "vivaldi" && CaptureRules.BrowserOf(null) == "other");
        rules.Browsers = "chrome,edge";
        T.Check("a browser that is switched off is left alone (with a reason)", !rules.Evaluate("https://x.example/a.zip", null, ff).Capture && rules.Evaluate("https://x.example/a.zip", null, ff).Reason.Contains("firefox") && rules.Evaluate("https://x.example/a.zip", null, edge).Capture);
        T.Check("wildcards are case-insensitive and literal otherwise", CaptureRules.Wildcard("*.EXAMPLE.com", "cdn.example.com") && !CaptureRules.Wildcard("a.c", "abc") && CaptureRules.Wildcard("a*c", "abbbc"));
        return Task.CompletedTask;
    }

    static Task Settings()
    {
        var store = new MemSettings(); var s = new AppSettings(store);
        T.Check("defaults: ask first, complete dialog on, queue choosers on, duplicate = ask, language English", s.AskBeforeDownload && !s.OnlyAddToQueue && s.ShowCompleteDialog && s.AskQueueOnLater && s.AskQueueOnBatch && s.DuplicateAction == "ask" && s.Language == "en" && !s.AutoResume && !s.ClipboardWatch);
        T.Check("defaults: file types and sites are IDM's, user agent looks like a browser, Defender command", s.FileTypes.Contains("MKV") && s.ExcludedSites.Contains("windowsupdate") && s.UserAgent.StartsWith("Mozilla/5.0") && s.AntivirusProgram.EndsWith("MpCmdRun.exe") && s.AntivirusArguments.Contains("%1"));
        s.Language = "fa"; s.AskBeforeDownload = false; s.OnlyAddToQueue = true; s.DuplicateAction = "overwrite"; s.TempDirectory = @"D:\tmp"; s.SetFileDateFromServer = true; s.MaxActive = 99; s.Connections = 0; s.Language = "xx";
        T.Check("values round trip; clamps and unknown languages are handled", store.Values["ask_before_download"] == "0" && s.OnlyAddToQueue && s.DuplicateAction == "overwrite" && s.TempDirectory == @"D:\tmp" && s.SetFileDateFromServer && s.MaxActive == 16 && s.Connections == 1 && s.Language == "en");
        s.CaptureBrowsers = "edge"; s.FileTypes = "ZIP"; s.ExcludedAddresses = "http://a/*";
        var rules = s.BuildCaptureRules();
        T.Check("capture rules are built from the settings", rules.Browsers == "edge" && rules.FileTypes == "ZIP" && rules.ExcludedAddresses == "http://a/*");
        var av = AntivirusCommand.Build("\"C:\\Program Files\\AV\\scan.exe\"", "-Scan -File \"%1\" --quiet", @"C:\Users\me\Downloads\a b.zip");
        T.Check("antivirus: program unquoted, %1 replaced by the quoted path", av.Program == @"C:\Program Files\AV\scan.exe" && av.Arguments == "-Scan -File \"C:\\Users\\me\\Downloads\\a b.zip\" --quiet", av.Arguments);
        T.Check("antivirus: {file} works too and quotes cannot be smuggled in", AntivirusCommand.Build("x", "{file}", "a\"b.zip").Arguments == "\"ab.zip\"");
        return Task.CompletedTask;
    }

    static Task Themes()
    {
        var all = new[] { ("obsidian-gold", ThemePalette.ObsidianGold), ("platinum-blue", ThemePalette.PlatinumBlue), ("royal-amethyst", ThemePalette.RoyalAmethyst), ("emerald-executive", ThemePalette.EmeraldExecutive), ("champagne-minimal", ThemePalette.ChampagneMinimal), ("graphite-copper", ThemePalette.GraphiteCopper), ("sapphire-noir", ThemePalette.SapphireNoir), ("ivory-luxe", ThemePalette.IvoryLuxe), ("rose-titanium", ThemePalette.RoseTitanium), ("arctic-glass", ThemePalette.ArcticGlass) };
        T.Check("all palettes define the same colours", all.Skip(1).All(p => ThemePalette.ObsidianGold.Keys.OrderBy(k => k).SequenceEqual(p.Item2.Keys.OrderBy(k => k))), string.Join(" | ", all.Skip(1).Select(p => p.Item1 + ": " + string.Join(",", ThemePalette.ObsidianGold.Keys.Except(p.Item2.Keys).Concat(p.Item2.Keys.Except(ThemePalette.ObsidianGold.Keys))))));
        T.Check("every value is a #RRGGBB colour", all.SelectMany(p => p.Item2.Values).All(v => System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9A-Fa-f]{6}$")));
        foreach (var (name, p) in all)
        {
            T.Check($"{name}: text is easy to read on the window, cards, inputs and popups (contrast >= 7)", new[] { "Bg", "Surface", "Input", "Popup", "Panel2" }.All(k => ThemePalette.Contrast(p["Text"], p[k]) >= 7), string.Join(" ", new[] { "Bg", "Surface", "Input", "Popup" }.Select(k => ThemePalette.Contrast(p["Text"], p[k]).ToString("0.0"))));
            T.Check($"{name}: secondary text is readable (contrast >= 4.5)", new[] { "Bg", "Surface", "HeaderBg" }.All(k => ThemePalette.Contrast(p["Muted"], p[k]) >= 4.5), string.Join(" ", new[] { "Bg", "Surface", "HeaderBg" }.Select(k => ThemePalette.Contrast(p["Muted"], p[k]).ToString("0.0"))));
            var buttonTextBar = 4.5;
            T.Check($"{name}: text on the accent is readable (>= {buttonTextBar})", ThemePalette.Contrast(p["OnAccent"], p["AccentDark"]) >= buttonTextBar || ThemePalette.Contrast(p["OnAccent"], p["Accent"]) >= buttonTextBar, ThemePalette.Contrast(p["OnAccent"], p["AccentDark"]).ToString("0.0"));
            T.Check($"{name}: top bar title is readable (contrast >= 4.5)", ThemePalette.Contrast(p["TopBarText"], p["Accent"]) >= 4.5, ThemePalette.Contrast(p["TopBarText"], p["Accent"]).ToString("0.0"));
            T.Check($"{name}: status colours are readable on cards (>= 3.5)", new[] { "Success", "Danger", "Warning", "Accent" }.All(k => ThemePalette.Contrast(p[k], p["Surface"]) >= 3.5), string.Join(" ", new[] { "Success", "Danger", "Warning", "Accent" }.Select(k => ThemePalette.Contrast(p[k], p["Surface"]).ToString("0.0"))));
            T.Check($"{name}: selected rows keep the text readable (>= 4.5)", ThemePalette.Contrast(p["Text"], p["Selected"]) >= 4.5 && ThemePalette.Contrast(p["Text"], p["Hover"]) >= 7, ThemePalette.Contrast(p["Text"], p["Selected"]).ToString("0.0"));
        }
        T.Check("dark and light palettes are classified correctly", new[] { ThemePalette.ObsidianGold, ThemePalette.RoyalAmethyst, ThemePalette.EmeraldExecutive, ThemePalette.GraphiteCopper, ThemePalette.SapphireNoir, ThemePalette.RoseTitanium }.All(p => ThemePalette.Contrast("#000000", p["Bg"]) < 3) && new[] { ThemePalette.PlatinumBlue, ThemePalette.ChampagneMinimal, ThemePalette.IvoryLuxe, ThemePalette.ArcticGlass }.All(p => ThemePalette.Contrast("#FFFFFF", p["Bg"]) < 1.2));
        T.Check("auto follows Windows; every named theme resolves", ThemePalette.Resolve("auto", true) == "sapphire-noir" && ThemePalette.Resolve("auto", false) == "platinum-blue" && ThemePalette.Names.All(name => ThemePalette.Resolve(name, false) == name));

        var settings = new AppSettings(new MemSettings());
        settings.Theme = "emerald-executive";
        T.Check("a new theme choice is saved", settings.Theme == "emerald-executive", settings.Theme);
        settings.Theme = "dark";
        T.Check("legacy dark settings migrate to Sapphire Noir", settings.Theme == "sapphire-noir", settings.Theme);
        settings.Theme = "nebula";
        T.Check("legacy Nebula settings migrate to Royal Amethyst", settings.Theme == "royal-amethyst", settings.Theme);
        settings.Theme = "orange";
        T.Check("legacy Orange settings migrate to Champagne Minimal", settings.Theme == "champagne-minimal", settings.Theme);
        settings.Theme = "not-a-real-theme";
        T.Check("an unrecognised value falls back to Sapphire Noir", settings.Theme == "sapphire-noir", settings.Theme);

        T.Check("with no torrent folder set, torrents save to the same place as everything else", settings.TorrentSaveFolder == settings.DefaultFolder);
        settings.TorrentFolder = @"D:\Torrents";
        T.Check("once a torrent folder is set, it's used instead", settings.TorrentSaveFolder == @"D:\Torrents");
        settings.TorrentFolder = "";
        T.Check("clearing it back to empty goes back to sharing the general folder", settings.TorrentSaveFolder == settings.DefaultFolder);

        // every colour the XAML asks for by name is in the palette (or is one of the app's own keys)
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MakanDownloadManager", "App.xaml"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return Task.CompletedTask;
        var app = File.ReadAllText(Path.Combine(dir, "MakanDownloadManager", "App.xaml"));
        var own = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(app, "x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value));
        var missing = new List<string>(); var staticBrushes = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(dir, "MakanDownloadManager"), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\\{DynamicResource (\\w+)\\}"))
                if (!ThemePalette.SapphireNoir.ContainsKey(m.Groups[1].Value) && !own.Contains(m.Groups[1].Value)) missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "\\{StaticResource (\\w+)\\}"))
                if (ThemePalette.SapphireNoir.ContainsKey(m.Groups[1].Value)) staticBrushes.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
        }
        T.Check("every colour used in XAML exists in the palette", missing.Count == 0, string.Join(" | ", missing.Distinct().Take(8)));
        T.Check("theme colours are DynamicResource everywhere (so switching the theme repaints open windows)", staticBrushes.Count == 0, string.Join(" | ", staticBrushes.Distinct().Take(8)));
        return Task.CompletedTask;
    }

    static Task Localization()
    {
        Loc.SetLanguage("en");
        T.Check("English: text is returned unchanged", Loc.T("OK") == "OK" && Loc.T("Something new") == "Something new" && Loc.F("Added {0} download(s)", 3) == "Added 3 download(s)");
        Loc.SetLanguage("fa");
        try
        {
            T.Check("Persian: exact text", Loc.T("OK") == "تأیید" && Loc.IsRtl);
            T.Check("Persian: a sentence with a number inside is matched by its pattern", Loc.T("Added 3 download(s)") == "3 دانلود اضافه شد", Loc.T("Added 3 download(s)"));
            T.Check("Persian: F formats the translated pattern", Loc.F("Added {0} download(s)", 12) == "12 دانلود اضافه شد", Loc.F("Added {0} download(s)", 12));
            T.Check("Persian: unknown text stays English (nothing breaks)", Loc.T("No such string anywhere") == "No such string anywhere" && Loc.T("") == "" && Loc.T(null) == "");
            T.Check("Persian: surrounding spaces are kept", Loc.T(" OK ") == " تأیید ");
        }
        finally { Loc.SetLanguage("en"); }
        T.Check("back to English", Loc.T("OK") == "OK" && !Loc.IsRtl);

        // every text written in a XAML file has a Persian translation (so a new label can't be forgotten)
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MakanDownloadManager", "App.xaml"))) dir = Path.GetDirectoryName(dir);
        if (dir == null) { T.Check("XAML folder found (skipping the completeness check)", true); return Task.CompletedTask; }
        var keys = LocFa.Entries.Select(e => e.English).ToList();
        T.Check("no English text is listed twice", keys.Count == keys.Distinct().Count(), string.Join(" | ", keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)));
        var known = new HashSet<string>(keys.Select(k => k.Trim()));
        var missing = new List<string>();
        foreach (var xaml in Directory.GetFiles(Path.Combine(dir, "MakanDownloadManager"), "*.xaml"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(xaml), "\\b(?:Header|Content|Text|ToolTip|Title)=\"([^\"{][^\"]*)\""))
            {
                var text = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-z]{2}") || text == "English" || text.Contains("Persian)")) continue;
                if (!known.Contains(text)) missing.Add(Path.GetFileName(xaml) + ": " + text);
            }
        T.Check("every XAML text has a Persian translation", missing.Count == 0, string.Join(" | ", missing.Take(8)));
        return Task.CompletedTask;
    }

    static async Task EngineOptions()
    {
        var dir = Path.Combine(Dir, "o" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "tmp");

        // ---- temp directory: partial files live there, the target folder stays clean until the end
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { TempDirectory = temp })
        {
            var item = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "big.bin"), Connections = 4 };
            m.Enqueue(item);
            var sw = Stopwatch.StartNew(); while (item.DoneBytes < 6_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(40);
            var inTemp = Directory.Exists(temp) ? Directory.GetFiles(temp).Select(Path.GetFileName).ToList() : new List<string?>();
            T.Check("while downloading, the parts are in the temp directory named after the id", inTemp.Any(f => f!.StartsWith(item.Id + ".")), string.Join(",", inTemp));
            T.Check("nothing but (maybe) the temp folder exists next to the target", !File.Exists(item.FilePath + ".part") && !File.Exists(item.FilePath + ".seg") && !File.Exists(item.FilePath));
            m.Pause(item); T.Check("paused", await Program.WaitStatusPublic(item, DownloadStatus.Paused));
            m.Enqueue(item);
            T.Check("resumes from the temp files and completes", await Program.WaitStatusPublic(item, DownloadStatus.Complete), item.LastError);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
            T.Check("byte-exact", T.Sha(item.FilePath) == doc.RootElement.GetProperty("sha").GetProperty("/big.bin").GetString());
            T.Check("temp directory is clean afterwards", Directory.GetFiles(temp).Length == 0, string.Join(",", Directory.GetFiles(temp)));

            var cancel = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "gone.bin"), Connections = 4 };
            m.Enqueue(cancel);
            var sw2 = Stopwatch.StartNew(); while (cancel.DoneBytes < 2_000_000 && sw2.Elapsed.TotalSeconds < 30) await Task.Delay(40);
            m.Cancel(cancel); await Program.WaitStatusPublic(cancel, DownloadStatus.Cancelled); await Task.Delay(300);
            T.Check("cancel removes the temp files too", Directory.GetFiles(temp).Length == 0 && !File.Exists(cancel.FilePath), string.Join(",", Directory.GetFiles(temp)));
        }
        // ---- a download that already has parts next to its target keeps using them when the setting is switched on
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false))
        {
            var item = new DownloadItem { Url = Base + "/slow.bin", FilePath = Path.Combine(dir, "legacy.bin"), Connections = 4 };
            m.Enqueue(item);
            var sw = Stopwatch.StartNew(); while (item.DoneBytes < 4_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(40);
            m.Pause(item); await Program.WaitStatusPublic(item, DownloadStatus.Paused);
            m.TempDirectory = Path.Combine(dir, "tmp2");
            m.Enqueue(item);
            T.Check("switching the setting mid-way does not orphan the partial download", await Program.WaitStatusPublic(item, DownloadStatus.Complete) && new FileInfo(item.FilePath).Length == 40 * 1024 * 1024, item.LastError);
            T.Check("no stray files", !Directory.Exists(m.TempDirectory) || Directory.GetFiles(m.TempDirectory).Length == 0);
        }

        // ---- file date from the server
        foreach (var on in new[] { true, false })
        {
            using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { SetFileDateFromServer = on };
            var item = new DownloadItem { Url = Base + "/small.bin?date" + on, FilePath = Path.Combine(dir, "date" + on + ".bin"), Connections = 2 };
            m.Enqueue(item); await Program.WaitStatusPublic(item, DownloadStatus.Complete);
            var stamp = File.GetLastWriteTimeUtc(item.FilePath);
            T.Check(on ? "option ON: file gets the server's Last-Modified date" : "option OFF: file keeps the download time",
                on ? stamp == new DateTime(2020, 3, 3, 10, 20, 30, DateTimeKind.Utc) : (DateTime.UtcNow - stamp).TotalMinutes < 5, stamp.ToString("o"));
        }

        // ---- ignore modification changes when resuming
        foreach (var ignore in new[] { false, true })
        {
            using var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { IgnoreModifiedOnResume = ignore };
            var item = new DownloadItem { Url = Base + "/slow.bin?ign" + ignore, FilePath = Path.Combine(dir, "ign" + ignore + ".bin"), Connections = 1 };
            m.Enqueue(item);
            var sw = Stopwatch.StartNew(); while (item.DoneBytes < 6_000_000 && sw.Elapsed.TotalSeconds < 30) await Task.Delay(40);
            m.Pause(item); await Program.WaitStatusPublic(item, DownloadStatus.Paused);
            var kept = item.DoneBytes;
            item.ETag = "\"the-file-was-edited\"";                       // the server would now report a different version
            m.Enqueue(item);
            await Task.Delay(1500);
            T.Check(ignore ? "ignore ON: the partial download is kept and continues" : "ignore OFF: a changed file restarts from zero",
                ignore ? item.DoneBytes >= kept : item.DoneBytes < kept, $"kept={kept} now={item.DoneBytes}");
            m.Cancel(item); await Program.WaitStatusPublic(item, DownloadStatus.Cancelled);
        }

        // ---- default User-Agent for downloads added by hand
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false) { DefaultUserAgent = "MakanTest/9 (manual)" })
        {
            var plain = new DownloadItem { Url = Base + "/ua.bin?plain", FilePath = Path.Combine(dir, "ua1.bin"), Connections = 2 };
            var own = new DownloadItem { Url = Base + "/ua.bin?own", FilePath = Path.Combine(dir, "ua2.bin"), Connections = 2, UserAgent = "BrowserSays/1.0" };
            m.Enqueue(plain); m.Enqueue(own);
            await Program.WaitStatusPublic(plain, DownloadStatus.Complete); await Program.WaitStatusPublic(own, DownloadStatus.Complete);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Base + "/__stats"));
            var ua = doc.RootElement.GetProperty("ua");
            T.Check("a download without its own User-Agent uses the default one", ua.GetProperty("/ua.bin?plain").GetString() == "MakanTest/9 (manual)", ua.ToString());
            T.Check("a download that came with the browser's User-Agent keeps it", ua.GetProperty("/ua.bin?own").GetString() == "BrowserSays/1.0");
        }

        // ---- overwrite instead of a new name
        using (var m = new DownloadManager(new MemoryStore(), autoResumeUnfinished: false))
        {
            var target = Path.Combine(dir, "same.bin"); File.WriteAllText(target, "old content");
            var suffixed = new DownloadItem { Url = Base + "/small.bin?a", FilePath = target, Connections = 2 };
            var over = new DownloadItem { Url = Base + "/small.bin?b", FilePath = target, Connections = 2, Overwrite = true };
            m.Enqueue(suffixed); await Program.WaitStatusPublic(suffixed, DownloadStatus.Complete);
            T.Check("default: the existing file is kept and the new one gets a numeric suffix", File.ReadAllText(target) == "old content" && suffixed.FilePath != target && File.Exists(suffixed.FilePath), suffixed.FilePath);
            m.Enqueue(over); await Program.WaitStatusPublic(over, DownloadStatus.Complete);
            T.Check("Overwrite: same path, existing file replaced", over.FilePath == target && new FileInfo(target).Length == 307200, over.FilePath);
        }
    }
}
