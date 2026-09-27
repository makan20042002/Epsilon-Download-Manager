namespace MakanDownloadManager.Services;

public interface ISettingsStore { string? Get(string key); void Set(string key, string value); }

/// <summary>Typed access to the options (the Options window and the rest of the app share these keys and defaults).</summary>
public sealed class AppSettings
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    public const string DefaultAntivirusArguments = "-Scan -ScanType 3 -File \"%1\"";

    readonly ISettingsStore _store;
    public AppSettings(ISettingsStore store) => _store = store;

    string Text(string key, string fallback) => _store.Get(key) is { } v && v.Length > 0 ? v : fallback;
    bool Flag(string key, bool fallback) => _store.Get(key) is { } v ? v == "1" : fallback;
    void Put(string key, bool value) => _store.Set(key, value ? "1" : "0");

    // ---- General
    public string Language { get => Text("language", "en") == "fa" ? "fa" : "en"; set => _store.Set("language", value == "fa" ? "fa" : "en"); }
    public bool ClipboardWatch { get => Flag("clipboard_watch", false); set => Put("clipboard_watch", value); }
    public string CaptureBrowsers { get => Text("capture_browsers", string.Join(",", CaptureRules.AllBrowsers)); set => _store.Set("capture_browsers", value); }

    // ---- File types
    public string FileTypes { get => Text("file_types", CaptureRules.DefaultFileTypes); set => _store.Set("file_types", value); }
    public string ExcludedSites { get => _store.Get("excluded_sites") ?? CaptureRules.DefaultExcludedSites; set => _store.Set("excluded_sites", value); }
    public string ExcludedAddresses { get => _store.Get("excluded_addresses") ?? ""; set => _store.Set("excluded_addresses", value); }

    // ---- Save to
    public string DefaultFolder
    {
        get => Text("download_dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        set => _store.Set("download_dir", value);
    }
    public string TempDirectory { get => _store.Get("temp_dir") ?? ""; set => _store.Set("temp_dir", value); }
    /// <summary>Empty (the default) means torrents save to the same place as everything else (DefaultFolder); a value
    /// here sends every new torrent - magnet, .torrent file, browser-captured or added by the phone remote page - there
    /// instead. Torrents already added keep their existing folder; this only affects new ones.</summary>
    public string TorrentFolder { get => _store.Get("torrent_folder") ?? ""; set => _store.Set("torrent_folder", value); }
    public string TorrentSaveFolder => string.IsNullOrWhiteSpace(TorrentFolder) ? DefaultFolder : TorrentFolder;
    public bool ChangeFolderOnLastSelected { get => Flag("change_folder_last", true); set => Put("change_folder_last", value); }
    public bool SetFileDateFromServer { get => Flag("set_file_date", false); set => Put("set_file_date", value); }
    public string CategoriesJson { get => _store.Get("categories_json") ?? ""; set => _store.Set("categories_json", value); }

    // ---- Downloads
    public bool AskBeforeDownload { get => Flag("ask_before_download", true); set => Put("ask_before_download", value); }
    public bool OnlyAddToQueue { get => Flag("only_queue", false); set => Put("only_queue", value); }
    public bool ShowCompleteDialog { get => Flag("show_complete", true); set => Put("show_complete", value); }
    public bool AskQueueOnLater { get => Flag("ask_queue_later", true); set => Put("ask_queue_later", value); }
    public bool AskQueueOnBatch { get => Flag("ask_queue_batch", true); set => Put("ask_queue_batch", value); }
    public bool IgnoreModifiedOnResume { get => Flag("ignore_modified", false); set => Put("ignore_modified", value); }
    /// <summary>"ask" | "suffix" | "overwrite" | "skip"</summary>
    public string DuplicateAction { get => Text("duplicate_action", "ask"); set => _store.Set("duplicate_action", value); }
    public string UserAgent { get => Text("user_agent", DefaultUserAgent); set => _store.Set("user_agent", value); }
    public bool AntivirusEnabled { get => Flag("av_enabled", false); set => Put("av_enabled", value); }
    public string AntivirusProgram
    {
        get => Text("av_program", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe"));
        set => _store.Set("av_program", value);
    }
    public string AntivirusArguments { get => Text("av_args", DefaultAntivirusArguments); set => _store.Set("av_args", value); }

    /// <summary>Open the IDM-style progress window when a download starts.</summary>
    public bool ShowProgressWindow { get => Flag("show_progress", true); set => Put("show_progress", value); }
    /// <summary>Brings Makan's window to the front the moment a new download arrives (a browser capture, a magnet link
    /// clicked outside the browser, and so on) - not just the small per-download progress popup ShowProgressWindow
    /// controls. On by default: "I clicked download and nothing seemed to happen" is a worse first impression than
    /// the window popping up once in a while.</summary>
    public bool ShowWindowOnCapture { get => Flag("show_window_on_capture", true); set => Put("show_window_on_capture", value); }

    // ---- YouTube & other sites (yt-dlp)
    /// <summary>Empty = the copy Makan installed in its tools folder, or one found on PATH.</summary>
    public string YtDlpPath { get => _store.Get("ytdlp_path") ?? ""; set => _store.Set("ytdlp_path", value); }
    /// <summary>"" | "chrome" | "edge" | "firefox" | "brave"</summary>
    public string YtCookiesBrowser { get => _store.Get("yt_cookies_browser") ?? ""; set => _store.Set("yt_cookies_browser", value); }

    /// <summary>"makan" | "obsidian" | "nebula" | "orange" | "light" | "auto" (follow Windows)</summary>
    public string Theme
    {
        get
        {
            var raw = Text("theme", "makan");
            if (raw is "dark" or "epsilon") return "makan";
            return raw is "makan" or "obsidian" or "nebula" or "orange" or "light" or "auto" ? raw : "makan";
        }
        set => _store.Set("theme", value is "makan" or "obsidian" or "nebula" or "orange" or "light" or "auto" ? value : "makan");
    }

    // ---- Connection / advanced (existing keys)
    public int MaxActive { get => int.TryParse(_store.Get("max_active"), out var v) ? Math.Clamp(v, 1, 16) : 4; set => _store.Set("max_active", Math.Clamp(value, 1, 16).ToString()); }
    public long SpeedKbps { get => long.TryParse(_store.Get("speed_kbps"), out var v) ? Math.Max(0, v) : 0; set => _store.Set("speed_kbps", Math.Max(0, value).ToString()); }
    public int Connections { get => int.TryParse(_store.Get("connections"), out var v) ? Math.Clamp(v, 1, 16) : 8; set => _store.Set("connections", Math.Clamp(value, 1, 16).ToString()); }
    public bool AutoResume { get => Flag("auto_resume", false); set => Put("auto_resume", value); }
    public string FfmpegPath { get => _store.Get("ffmpeg_path") ?? ""; set => _store.Set("ffmpeg_path", value); }
    // ---- smart connections (Options > Connection)
    public bool AdaptiveConnections { get => Flag("adaptive_connections", true); set => Put("adaptive_connections", value); }
    public bool SmartDownloads { get => Flag("smart_downloads", true); set => Put("smart_downloads", value); }

    // ---- BitTorrent
    public int TorrentPort { get => int.TryParse(_store.Get("torrent_port"), out var v) && v is 0 or (>= 1024 and <= 65535) ? v : 6881; set => _store.Set("torrent_port", (value is 0 or (>= 1024 and <= 65535) ? value : 6881).ToString()); }
    public bool TorrentDht { get => Flag("torrent_dht", true); set => Put("torrent_dht", value); }
    public bool TorrentPex { get => Flag("torrent_pex", true); set => Put("torrent_pex", value); }
    /// <summary>Ask the router to forward the listening port automatically (UPnP, then NAT-PMP). No UI toggle yet;
    /// on by default since it only helps peer connectivity and never blocks anything if the router doesn't answer.</summary>
    public bool TorrentPortMapping { get => Flag("torrent_port_mapping", true); set => Put("torrent_port_mapping", value); }
    public int TorrentUploadSlots { get => int.TryParse(_store.Get("torrent_upload_slots"), out var v) ? Math.Clamp(v, 1, 50) : 4; set => _store.Set("torrent_upload_slots", Math.Clamp(value, 1, 50).ToString()); }
    public int TorrentMaxPeers { get => int.TryParse(_store.Get("torrent_max_peers"), out var v) ? Math.Clamp(v, 5, 500) : 60; set => _store.Set("torrent_max_peers", Math.Clamp(value, 5, 500).ToString()); }
    /// <summary>Keep sharing after a torrent finishes.</summary>
    /// <summary>Off by default: a finished torrent just stops (no upload) until the person deliberately shares it by choosing
    /// Resume in its details window. Turning this on restores the common torrent-client default of seeding automatically.</summary>
    public bool TorrentSeedAfterCompletion { get => Flag("torrent_seed_after", false); set => Put("torrent_seed_after", value); }
    /// <summary>Stop sharing at this upload/download ratio; 0 = never stop on ratio.</summary>
    public double TorrentSeedRatioLimit { get => double.TryParse(_store.Get("torrent_seed_ratio"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : 1.0; set => _store.Set("torrent_seed_ratio", Math.Max(0, value).ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    /// <summary>Torrent download speed limit in KB/s; 0 = unlimited. Separate from the toolbar's Speed Limiter, which only
    /// covers ordinary downloads - a torrent client's own bandwidth page always keeps its limits apart from anything else.</summary>
    public long TorrentDownloadKbps { get => long.TryParse(_store.Get("torrent_download_kbps"), out var v) ? Math.Max(0, v) : 0; set => _store.Set("torrent_download_kbps", Math.Max(0, value).ToString()); }
    /// <summary>Torrent upload speed limit in KB/s; 0 = unlimited.</summary>
    public long TorrentUploadKbps { get => long.TryParse(_store.Get("torrent_upload_kbps"), out var v) ? Math.Max(0, v) : 0; set => _store.Set("torrent_upload_kbps", Math.Max(0, value).ToString()); }
    public bool TorrentSeedOnStart { get => Flag("torrent_seed_on_start", true); set => Put("torrent_seed_on_start", value); }
    /// <summary>Shown once, right after the very first launch - a short "here's what's left to set up" checklist.</summary>
    public bool FirstRunDone { get => Flag("first_run_done", false); set => Put("first_run_done", value); }

    // ---- Remote status page: an alternative to a native phone app - view/add downloads from a browser on the same network
    public bool RemoteEnabled { get => Flag("remote_enabled", false); set => Put("remote_enabled", value); }
    /// <summary>Off by default: pauses Windows Update / BITS / Delivery Optimization while a download is actively
    /// running (needs administrator rights, which Makan does not have unless the person explicitly grants them).</summary>
    public bool BoostDownloadSpeed { get => Flag("boost_download_speed", false); set => Put("boost_download_speed", value); }

    // ---- Proxy / SOCKS (like IDM's Proxy/Socks tab) - regular HTTP(S) downloads only, not torrents or yt-dlp
    /// <summary>"none" | "system" | "manual".</summary>
    public string ProxyMode { get => _store.Get("proxy_mode") is "system" or "manual" ? _store.Get("proxy_mode")! : "none"; set => _store.Set("proxy_mode", value is "system" or "manual" ? value : "none"); }
    public string ProxyHost { get => _store.Get("proxy_host") ?? ""; set => _store.Set("proxy_host", value.Trim()); }
    public int ProxyPort { get => int.TryParse(_store.Get("proxy_port"), out var v) && v is > 0 and < 65536 ? v : 8080; set => _store.Set("proxy_port", (value is > 0 and < 65536 ? value : 8080).ToString()); }
    public bool ProxyUseForHttp { get => Flag("proxy_use_http", true); set => Put("proxy_use_http", value); }
    public bool ProxyUseForHttps { get => Flag("proxy_use_https", true); set => Put("proxy_use_https", value); }
    public string ProxyUsername { get => _store.Get("proxy_username") ?? ""; set => _store.Set("proxy_username", value); }
    /// <summary>Encrypted at rest with the same per-Windows-account protection everything else sensitive uses.</summary>
    public string ProxyPassword
    {
        get => SecretProtector.DecryptOrPlain(_store.Get("proxy_password"), out _) ?? "";
        set => _store.Set("proxy_password", string.IsNullOrEmpty(value) ? "" : SecretProtector.Protect(value));
    }
    public int RemotePort { get => int.TryParse(_store.Get("remote_port"), out var v) && v is > 0 and < 65536 ? v : 8990; set => _store.Set("remote_port", (value is > 0 and < 65536 ? value : 8990).ToString()); }
    /// <summary>Generated once and kept; anyone who has it can see and add downloads, so it is never shown in a log.</summary>
    public string RemoteToken
    {
        get
        {
            var existing = _store.Get("remote_token");
            if (!string.IsNullOrEmpty(existing)) return existing;
            var made = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();
            _store.Set("remote_token", made);
            return made;
        }
        set => _store.Set("remote_token", value);
    }

    // ---- BitTorrent schedule: separate windows for actively downloading vs. seeding (a torrent's two very different phases)
    public bool TorrentDownloadScheduleEnabled { get => Flag("torrent_dl_schedule_on", false); set => Put("torrent_dl_schedule_on", value); }
    public string TorrentDownloadStartTime { get => _store.Get("torrent_dl_start") ?? "08:00"; set => _store.Set("torrent_dl_start", value); }
    public string TorrentDownloadStopTime { get => _store.Get("torrent_dl_stop") ?? "23:00"; set => _store.Set("torrent_dl_stop", value); }
    public bool TorrentSeedScheduleEnabled { get => Flag("torrent_seed_schedule_on", false); set => Put("torrent_seed_schedule_on", value); }
    public string TorrentSeedStartTime { get => _store.Get("torrent_seed_start") ?? "23:00"; set => _store.Set("torrent_seed_start", value); }
    public string TorrentSeedStopTime { get => _store.Get("torrent_seed_stop") ?? "07:00"; set => _store.Set("torrent_seed_stop", value); }

    public CaptureRules BuildCaptureRules() => new()
    {
        FileTypes = FileTypes, ExcludedSites = ExcludedSites, ExcludedAddresses = ExcludedAddresses, Browsers = CaptureBrowsers
    };
}

/// <summary>Builds the command line for the optional "check downloaded files with an antivirus" step.</summary>
public static class AntivirusCommand
{
    /// <summary>%1 (or {file}) in the arguments becomes the downloaded file's full path, quoted.</summary>
    public static (string Program, string Arguments) Build(string program, string arguments, string filePath)
    {
        var quoted = "\"" + filePath.Replace("\"", "") + "\"";
        var args = (arguments ?? "").Replace("\"%1\"", quoted).Replace("%1", quoted).Replace("\"{file}\"", quoted).Replace("{file}", quoted);
        return (program.Trim().Trim('"'), args);
    }
}
