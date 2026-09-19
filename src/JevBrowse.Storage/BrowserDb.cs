using Microsoft.Data.Sqlite;

namespace JevBrowse.Storage;

/// <summary>Durable logical state (Architecture §16: db\browser.db). WAL mode; schema versioned via user_version.</summary>
public sealed class BrowserDb : IDisposable
{
    private const int SchemaVersion = 2;

    public BrowserDb(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString());
        Connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    public SqliteConnection Connection { get; }

    private void Migrate()
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var v = Convert.ToInt32(cmd.ExecuteScalar());
        if (v < 1)
        {
            Exec("""
                CREATE TABLE tabs (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    state INTEGER NOT NULL,
                    protection INTEGER NOT NULL DEFAULT 0,
                    ordinal INTEGER NOT NULL,
                    last_state_change INTEGER NOT NULL
                );
                CREATE INDEX tabs_ordinal ON tabs(ordinal);
                """);
        }
        if (v < 2)
        {
            Exec("""
                CREATE TABLE checkpoints (
                    id TEXT PRIMARY KEY REFERENCES tabs(id) ON DELETE CASCADE,
                    url TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    scroll_x REAL NOT NULL DEFAULT 0,
                    scroll_y REAL NOT NULL DEFAULT 0,
                    favicon_url TEXT,
                    thumbnail_path TEXT,
                    captured_at INTEGER NOT NULL
                );
                """);
        }
        if (v < SchemaVersion) Exec($"PRAGMA user_version={SchemaVersion}");
    }

    public void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Connection.Dispose();
}
