using MakanDownloadManager.Models;
using MakanDownloadManager.Services;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

static class Program
{
    static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "makan-db-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "downloads.db");
            using (var db = new DownloadDb(path))
            {
                db.Initialize();
                var item = new DownloadItem { Url="https://example.test/private.zip", FilePath=Path.Combine(root,"private.zip"), Cookie="session=secret-token", UserAgent="MakanTest" };
                item.Id = db.Add(item);
                var loaded = db.Get(item.Id)!;
                Check("round-trip", loaded.Cookie == item.Cookie);
                Check("integrity check", db.IntegrityCheck());
                db.Save(loaded);
                Check("backup exists", File.Exists(path + ".backup"));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var raw = File.ReadAllBytes(path);
            Check("cookie is not stored as plaintext", !System.Text.Encoding.UTF8.GetString(raw).Contains("secret-token", StringComparison.Ordinal));
            // Legacy plaintext cookie migration: DB can still be read and is re-encrypted on next save.
            using (var db = new DownloadDb(path)) { db.Initialize(); var x = db.Get(1)!; Check("reopen", x.Cookie == "session=secret-token"); }

            var legacy = Path.Combine(root, "legacy.db");
            using (var old = new SqliteConnection($"Data Source={legacy}"))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = @"
CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE history(id INTEGER PRIMARY KEY,download_id INTEGER,url TEXT,file_path TEXT,status TEXT,started_at TEXT,finished_at TEXT,error TEXT);
INSERT INTO settings VALUES('theme','obsidian-gold'),('download_dir','C:\\Downloads'),('remote_token','must-not-copy'),('first_run_done','1');
INSERT INTO history VALUES(1,1,'https://example.test/complete','C:\\Downloads\\complete.zip','Completed',NULL,'2026-01-01T00:00:00Z',NULL);
INSERT INTO history VALUES(2,2,'https://example.test/active','C:\\Downloads\\active.zip','Downloading',NULL,NULL,NULL);";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacy)));
            var targetPath = Path.Combine(root, "v2.db");
            using (var target = new DownloadDb(targetPath))
            {
                target.Initialize();
                Check("migration copies safe settings", V1MigrationService.CopySafeSettingsFrom(legacy, target) == 2 && target.Get("theme") == "obsidian-gold");
                Check("migration excludes secrets and first-run state", target.Get("remote_token") is null && target.Get("first_run_done") is null);
                Check("migration imports completed history only", V1MigrationService.ImportCompletedHistoryFrom(legacy, target) == 1 && target.LoadHistory().Count == 1);
            }
            SqliteConnection.ClearAllPools();
            Check("legacy database remains byte-for-byte unchanged", sourceHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(legacy))));
            Console.WriteLine("Database tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    static void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
}
