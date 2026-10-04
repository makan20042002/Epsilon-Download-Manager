using Microsoft.Data.Sqlite;
using MakanDownloadManager.Models;

namespace MakanDownloadManager.Services;

public sealed class DownloadDb : IDownloadStore, ISettingsStore, IDisposable
{
    readonly SqliteConnection _connection;
    readonly string _path;
    readonly object _gate = new();
    bool _disposed;

    public DownloadDb(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _connection = new SqliteConnection($"Data Source={_path};Cache=Shared;Mode=ReadWriteCreate");
    }

    public void Initialize()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _connection.Open();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS downloads(
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 url TEXT NOT NULL, file_path TEXT NOT NULL, category TEXT NOT NULL DEFAULT 'Other', priority INTEGER NOT NULL DEFAULT 5,
 status TEXT NOT NULL DEFAULT 'Queued', scheduled_at TEXT, total_bytes INTEGER, done_bytes INTEGER NOT NULL DEFAULT 0,
 cookie TEXT, referrer TEXT, user_agent TEXT, expected_sha256 TEXT, etag TEXT, last_modified TEXT,
 connections INTEGER NOT NULL DEFAULT 4, speed_limit INTEGER NOT NULL DEFAULT 0, status_code INTEGER NOT NULL DEFAULT 0,
 started_at TEXT, finished_at TEXT, retry_count INTEGER NOT NULL DEFAULT 0, last_error TEXT,
 created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT,download_id INTEGER,url TEXT,file_path TEXT,status TEXT,started_at TEXT,finished_at TEXT,error TEXT);
CREATE INDEX IF NOT EXISTS idx_downloads_url ON downloads(url);
CREATE INDEX IF NOT EXISTS idx_downloads_status ON downloads(status);
CREATE INDEX IF NOT EXISTS idx_history_finished ON history(finished_at DESC);";
            cmd.ExecuteNonQuery();
            EnsureColumn("downloads", "retry_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn("downloads", "last_error", "TEXT");
            EnsureColumn("downloads", "etag", "TEXT");
            EnsureColumn("downloads", "last_modified", "TEXT");
            EnsureColumn("downloads", "started_at", "TEXT");
            EnsureColumn("downloads", "finished_at", "TEXT");
            using var integrity = _connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check";
            var result = integrity.ExecuteScalar()?.ToString();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Makan's download database failed SQLite integrity_check: " + result);
            TryCreateBackup();
        }
    }

    /// <summary>Adds a column if the table doesn't already have it (SQLite has no "ADD COLUMN IF NOT EXISTS").
    /// <paramref name="table"/>, <paramref name="column"/> and <paramref name="type"/> are interpolated directly into the SQL:
    /// that is only safe because every call site passes a hardcoded literal from this file (see the EnsureColumn(...) calls
    /// above), never anything from the database, a file, or the network. SQL parameters can bind values but not identifiers
    /// (a table or column name), so there is no parameterized alternative here - if this method is ever changed to accept
    /// a name from outside this file, it must be validated against an allow-list first.</summary>
    void EnsureColumn(string table, string column, string type)
    {
        using var c = _connection.CreateCommand();
        c.CommandText = $"PRAGMA table_info({table})";
        using var r = c.ExecuteReader();
        while (r.Read()) if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        r.Close();
        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public List<DownloadItem> Load()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM downloads ORDER BY priority DESC,id";
            using var r = cmd.ExecuteReader();
            var result = new List<DownloadItem>();
            var plaintextCookies = new List<DownloadItem>();
            while (r.Read())
            {
                var item = Read(r, out var wasPlaintext);
                result.Add(item);
                if (wasPlaintext && !string.IsNullOrEmpty(item.Cookie)) plaintextCookies.Add(item);
            }
            // Migrate legacy plaintext cookies only once, after the reader is closed.
            foreach (var item in plaintextCookies) Save(item);
            return result;
        }
    }

    public DownloadItem? Get(long id)
    {
        lock (_gate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = "SELECT * FROM downloads WHERE id=$id";
            c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            if (!r.Read()) return null;
            var item = Read(r, out var wasPlaintext);
            if (wasPlaintext && !string.IsNullOrEmpty(item.Cookie)) Save(item);
            return item;
        }
    }

    DownloadItem Read(SqliteDataReader r, out bool wasPlaintext)
    {
        var cookie = SecretProtector.DecryptOrPlain(NullableStr(r, "cookie"), out wasPlaintext);
        return new DownloadItem
        {
        Id = I64(r,"id"), Url = Str(r,"url"), FilePath = Str(r,"file_path"), Category = Str(r,"category","General"),
        Priority = I32(r,"priority",5), Status = Str(r,"status","Queued"), ScheduledAt = Date(r,"scheduled_at"),
        TotalBytes = NullableI64(r,"total_bytes"), DoneBytes = I64(r,"done_bytes"), Cookie = cookie,
        Referrer = NullableStr(r,"referrer"), UserAgent = NullableStr(r,"user_agent"), ExpectedSha256 = NullableStr(r,"expected_sha256"),
        ETag = NullableStr(r,"etag"), LastModified = NullableStr(r,"last_modified"), Connections = I32(r,"connections",4),
        SpeedLimitBytesPerSec = I64(r,"speed_limit"), StatusCode = I32(r,"status_code"), StartedAt = Date(r,"started_at"),
        FinishedAt = Date(r,"finished_at"), RetryCount = I32(r,"retry_count"), LastError = NullableStr(r,"last_error")
        };
    }

    static string Str(SqliteDataReader r,string n,string fallback="") { var i=r.GetOrdinal(n); return r.IsDBNull(i)?fallback:r.GetString(i); }
    static string? NullableStr(SqliteDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:r.GetString(i); }
    static long I64(SqliteDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?0:Convert.ToInt64(r.GetValue(i)); }
    static long? NullableI64(SqliteDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:Convert.ToInt64(r.GetValue(i)); }
    static int I32(SqliteDataReader r,string n,int fallback=0) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?fallback:Convert.ToInt32(r.GetValue(i)); }
    static DateTime? Date(SqliteDataReader r,string n) { var i=r.GetOrdinal(n); return r.IsDBNull(i)?null:DateTime.TryParse(r.GetString(i),out var d)?d:null; }

    public long Add(DownloadItem x)
    {
        lock (_gate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = @"INSERT INTO downloads(url,file_path,category,priority,status,scheduled_at,total_bytes,done_bytes,cookie,referrer,user_agent,expected_sha256,etag,last_modified,connections,speed_limit,status_code,started_at,finished_at,retry_count,last_error,created_at,updated_at)
VALUES($u,$f,$cat,$p,$s,$sch,$t,$d,$co,$ref,$ua,$sha,$etag,$lm,$n,$lim,$code,$start,$finish,$retry,$err,$now,$now);SELECT last_insert_rowid();";
            Bind(c,x);
            return Convert.ToInt64(c.ExecuteScalar());
        }
    }

    public void Save(DownloadItem x)
    {
        lock (_gate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = @"UPDATE downloads SET url=$u,file_path=$f,category=$cat,priority=$p,status=$s,scheduled_at=$sch,total_bytes=$t,done_bytes=$d,cookie=$co,referrer=$ref,user_agent=$ua,expected_sha256=$sha,etag=$etag,last_modified=$lm,connections=$n,speed_limit=$lim,status_code=$code,started_at=$start,finished_at=$finish,retry_count=$retry,last_error=$err,updated_at=$now WHERE id=$id";
            Bind(c,x); c.Parameters.AddWithValue("$id",x.Id); c.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = "DELETE FROM downloads WHERE id=$id";
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    static void Bind(SqliteCommand c, DownloadItem x)
    {
        c.Parameters.AddWithValue("$u",x.Url); c.Parameters.AddWithValue("$f",x.FilePath); c.Parameters.AddWithValue("$cat",x.Category);
        c.Parameters.AddWithValue("$p",Math.Clamp(x.Priority,0,10)); c.Parameters.AddWithValue("$s",x.Status);
        c.Parameters.AddWithValue("$sch",(object?)x.ScheduledAt?.ToString("O")??DBNull.Value); c.Parameters.AddWithValue("$t",(object?)x.TotalBytes??DBNull.Value);
        c.Parameters.AddWithValue("$d",Math.Max(0,x.DoneBytes)); c.Parameters.AddWithValue("$co",(object?)(string.IsNullOrEmpty(x.Cookie) ? x.Cookie : SecretProtector.Protect(x.Cookie!))??DBNull.Value);
        c.Parameters.AddWithValue("$ref",(object?)x.Referrer??DBNull.Value); c.Parameters.AddWithValue("$ua",(object?)x.UserAgent??DBNull.Value);
        c.Parameters.AddWithValue("$sha",(object?)x.ExpectedSha256??DBNull.Value); c.Parameters.AddWithValue("$etag",(object?)x.ETag??DBNull.Value);
        c.Parameters.AddWithValue("$lm",(object?)x.LastModified??DBNull.Value); c.Parameters.AddWithValue("$n",Math.Clamp(x.Connections,1,16));
        c.Parameters.AddWithValue("$lim",Math.Max(0,x.SpeedLimitBytesPerSec)); c.Parameters.AddWithValue("$code",x.StatusCode);
        c.Parameters.AddWithValue("$start",(object?)x.StartedAt?.ToString("O")??DBNull.Value); c.Parameters.AddWithValue("$finish",(object?)x.FinishedAt?.ToString("O")??DBNull.Value);
        c.Parameters.AddWithValue("$retry",Math.Max(0,x.RetryCount)); c.Parameters.AddWithValue("$err",(object?)x.LastError??DBNull.Value);
        c.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
    }

    public bool ExistsUrl(string url)
    {
        lock (_gate)
        {
            using var c=_connection.CreateCommand();
            c.CommandText="SELECT EXISTS(SELECT 1 FROM downloads WHERE url=$u AND status<>$cancelled)";
            c.Parameters.AddWithValue("$u",url); c.Parameters.AddWithValue("$cancelled",DownloadStatus.Cancelled.ToString());
            return Convert.ToInt32(c.ExecuteScalar()) == 1;
        }
    }

    public string? Get(string key)
    {
        lock (_gate) { using var c=_connection.CreateCommand(); c.CommandText="SELECT value FROM settings WHERE key=$k"; c.Parameters.AddWithValue("$k",key); return c.ExecuteScalar()?.ToString(); }
    }

    public void Set(string key,string value)
    {
        lock (_gate) { using var c=_connection.CreateCommand(); c.CommandText="INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value"; c.Parameters.AddWithValue("$k",key); c.Parameters.AddWithValue("$v",value); c.ExecuteNonQuery(); }
    }

    public void AddHistory(DownloadItem x,string status,string? error=null)
    {
        lock (_gate)
        {
            using var c=_connection.CreateCommand();
            c.CommandText="INSERT INTO history(download_id,url,file_path,status,started_at,finished_at,error) VALUES($id,$u,$f,$s,$st,$fi,$e)";
            c.Parameters.AddWithValue("$id",x.Id); c.Parameters.AddWithValue("$u",x.Url); c.Parameters.AddWithValue("$f",x.FilePath); c.Parameters.AddWithValue("$s",status);
            c.Parameters.AddWithValue("$st",(object?)x.StartedAt?.ToString("O")??DateTime.UtcNow.ToString("O"));
            c.Parameters.AddWithValue("$fi",(object?)x.FinishedAt?.ToString("O")??DateTime.UtcNow.ToString("O")); c.Parameters.AddWithValue("$e",(object?)error??DBNull.Value); c.ExecuteNonQuery();
        }
    }

    public List<HistoryRow> LoadHistory(int limit=500)
    {
        lock (_gate)
        {
            using var c=_connection.CreateCommand(); c.CommandText="SELECT id,download_id,url,file_path,status,started_at,finished_at,error FROM history ORDER BY finished_at DESC LIMIT $l"; c.Parameters.AddWithValue("$l",Math.Clamp(limit,1,5000));
            using var r=c.ExecuteReader(); var list=new List<HistoryRow>();
            while(r.Read()) list.Add(new HistoryRow(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetString(4),ParseDate(r.GetString(5)),ParseDate(r.GetString(6)),r.IsDBNull(7)?null:r.GetString(7)));
            return list;
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            using var c = _connection.CreateCommand();
            c.CommandText = "DELETE FROM history";
            c.ExecuteNonQuery();
        }
    }

    static DateTime ParseDate(string value) => DateTime.TryParse(value,out var d)?d:DateTime.UtcNow;

    public string DatabasePath => _path;
    public string BackupPath => _path + ".backup";

    public bool IntegrityCheck()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var c = _connection.CreateCommand(); c.CommandText = "PRAGMA integrity_check";
            return string.Equals(c.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool RestoreBackup()
    {
        lock (_gate)
        {
            if (!File.Exists(BackupPath)) return false;
            try
            {
                _connection.Close();
                TryDeleteSidecar(_path + "-wal");
                TryDeleteSidecar(_path + "-shm");
                File.Copy(BackupPath, _path, true);
                return true;
            }
            catch { return false; }
        }
    }

    public bool TryCreateBackup()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            try
            {
                using var checkpoint = _connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                checkpoint.ExecuteNonQuery();
                File.Copy(_path, BackupPath, true);
                return true;
            }
            catch { return false; }
        }
    }

    static void TryDeleteSidecar(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    void ThrowIfDisposed(){if(_disposed)throw new ObjectDisposedException(nameof(DownloadDb));}
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_connection.Dispose();}}
}

public sealed record HistoryRow(long Id,long DownloadId,string Url,string FilePath,string Status,DateTime StartedAt,DateTime FinishedAt,string? Error);
