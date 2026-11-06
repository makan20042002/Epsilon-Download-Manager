using Microsoft.Data.Sqlite;

namespace MakanDownloadManager.Services;

/// <summary>
/// One-way, read-only migration from the stable V1 profile. V1's database is never opened for writing,
/// moved, renamed or deleted; V2 receives copies in its own database.
/// </summary>
public static class V1MigrationService
{
    const string SettingsMarker = "v1_settings_migration_complete";
    const string HistoryMarker = "v1_completed_history_imported";

    static readonly HashSet<string> SafeSettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "language", "clipboard_watch", "capture_browsers", "file_types", "excluded_sites", "excluded_addresses",
        "download_dir", "temp_dir", "torrent_folder", "change_folder_last", "set_file_date", "categories_json",
        "ask_before_download", "only_queue", "show_complete", "ask_queue_later", "ask_queue_batch", "ignore_modified",
        "delete_action", "duplicate_action", "user_agent", "av_enabled", "av_program", "av_args", "show_progress",
        "show_window_on_capture", "yt_cookies_browser", "playlist_height", "playlist_format", "theme", "max_active",
        "speed_kbps", "speed_limit_scope", "multi_network", "multi_network_excluded", "multi_network_min_mb",
        "multi_network_by_speed", "multi_network_avoid_metered", "multi_network_daily_limit_mb", "multi_network_keep_one_free",
        "multi_network_retry_minutes", "multi_network_ask_new", "multi_network_known", "multi_network_colors", "connections",
        "adaptive_connections", "smart_downloads", "boost_download_speed", "retry_attempts", "retry_window_seconds",
        "sort_tab_widths", "compact_rows", "auto_resume", "keep_awake_downloading", "torrent_port", "torrent_dht", "torrent_pex", "torrent_port_mapping", "torrent_upload_slots",
        "torrent_max_peers", "torrent_seed_after", "torrent_seed_on_start", "torrent_encryption", "torrent_seed_minutes",
        "torrent_seed_ratio", "torrent_download_kbps", "torrent_upload_kbps", "torrent_dl_schedule_on",
        "torrent_dl_start", "torrent_dl_stop", "torrent_seed_schedule_on", "torrent_seed_start", "torrent_seed_stop", "remote_port"
    };

    public static string LegacyDatabasePath => Path.Combine(PortableModeService.LegacyDataDirectory, "downloads.db");
    public static bool IsAvailable => !PortableModeService.IsPortable && File.Exists(LegacyDatabasePath);
    public static bool SettingsWereCopied { get; private set; }
    public static int CopiedSettingCount { get; private set; }

    public static void CopySafeSettingsOnce(DownloadDb target)
    {
        if (!IsAvailable || target.Get(SettingsMarker) == "1") return;
        try
        {
            CopiedSettingCount = CopySafeSettingsFrom(LegacyDatabasePath, target);
            target.Set(SettingsMarker, "1");
            SettingsWereCopied = CopiedSettingCount > 0;
        }
        catch (Exception ex)
        {
            new DiagnosticsService().Error("V1 settings could not be copied; V1 was left unchanged", ex);
        }
    }

    public static bool HistoryAlreadyImported(DownloadDb target) => target.Get(HistoryMarker) == "1";

    public static int ImportCompletedHistory(DownloadDb target)
    {
        if (!IsAvailable || HistoryAlreadyImported(target)) return 0;
        var imported = 0;
        try
        {
            imported = ImportCompletedHistoryFrom(LegacyDatabasePath, target);
            target.Set(HistoryMarker, "1");
            return imported;
        }
        catch (Exception ex)
        {
            new DiagnosticsService().Error("V1 completed history could not be imported; V1 was left unchanged", ex);
            return -1;
        }
    }

    /// <summary>Copies only allow-listed preferences from an explicitly supplied V1 database. Used by migration tests too.</summary>
    public static int CopySafeSettingsFrom(string legacyDatabasePath, DownloadDb target)
    {
        var copied = 0;
        using var source = OpenLegacyReadOnly(legacyDatabasePath);
        using var command = source.CreateCommand();
        command.CommandText = "SELECT key,value FROM settings";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!SafeSettingKeys.Contains(key) || target.Get(key) is not null) continue;
            target.Set(key, reader.GetString(1));
            copied++;
        }
        return copied;
    }

    /// <summary>Copies completed history only. No live/incomplete download rows are read or created.</summary>
    public static int ImportCompletedHistoryFrom(string legacyDatabasePath, DownloadDb target)
    {
        var imported = 0;
        using var source = OpenLegacyReadOnly(legacyDatabasePath);
            using var command = source.CreateCommand();
            command.CommandText = @"SELECT url,file_path,status,started_at,finished_at,error
FROM history
WHERE lower(status) IN ('complete','completed')
ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                target.AddImportedHistory(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "Completed" : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
                imported++;
            }
        return imported;
    }

    static SqliteConnection OpenLegacyReadOnly(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }
}
