using Microsoft.Data.Sqlite;

namespace JevBrowse.Storage;

/// <summary>Durable logical state (Architecture §16: db\browser.db). WAL mode; schema versioned via user_version.</summary>
public sealed class BrowserDb : IDisposable
{
    private const int SchemaVersion = 5;

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
        if (v < 3)
        {
            Exec("""
                CREATE TABLE site_settings (
                    site TEXT PRIMARY KEY,
                    shield_enabled INTEGER NOT NULL DEFAULT 1,
                    updated_at INTEGER NOT NULL
                );
                """);
        }
        if (v < 4)
        {
            Exec("""
                CREATE TABLE workspaces (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    background_priority REAL NOT NULL DEFAULT 0.3,
                    notifications_muted INTEGER NOT NULL DEFAULT 0,
                    created_at INTEGER NOT NULL
                );
                INSERT INTO workspaces (id, name, background_priority, notifications_muted, created_at)
                    VALUES ('00000000000000000000000000000000', 'Default', 0.3, 0, 0);
                ALTER TABLE tabs ADD COLUMN workspace_id TEXT NOT NULL DEFAULT '00000000000000000000000000000000';
                CREATE INDEX tabs_workspace ON tabs(workspace_id, ordinal);
                CREATE TABLE context_checkpoints (
                    id TEXT PRIMARY KEY,
                    workspace_id TEXT NOT NULL,
                    workspace_name TEXT NOT NULL,
                    at INTEGER NOT NULL,
                    active_resource TEXT,
                    live_count INTEGER NOT NULL,
                    resources_json TEXT NOT NULL
                );
                CREATE INDEX context_checkpoints_at ON context_checkpoints(at DESC);
                """);
        }
        if (v < 5)
        {
            Exec("""
                ALTER TABLE workspaces ADD COLUMN container INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE site_settings ADD COLUMN data_class INTEGER;
                CREATE TABLE site_permissions (
                    site TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    allowed INTEGER NOT NULL,
                    expires_at INTEGER,
                    granted_at INTEGER NOT NULL,
                    PRIMARY KEY (site, kind)
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
