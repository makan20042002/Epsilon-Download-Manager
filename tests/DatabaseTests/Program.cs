using MakanDownloadManager.Models;
using MakanDownloadManager.Services;

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
            Console.WriteLine("Database tests passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    static void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
}
