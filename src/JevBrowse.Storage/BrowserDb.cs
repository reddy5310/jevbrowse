using Microsoft.Data.Sqlite;

namespace JevBrowse.Storage;

/// <summary>
/// Durable logical state (Architecture §16: db\browser.db). WAL mode; schema versioned via user_version.
/// Every migration step is ONE transaction that also advances user_version, so a crash or failure can only leave the
/// database at a complete earlier version, never half-migrated (which would make the next start fail forever).
/// </summary>
public sealed class BrowserDb : IDisposable
{
    public const int LatestVersion = 9;

    private static readonly (int Version, string Sql)[] Steps =
    [
        (1, """
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
            """),
        (2, """
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
            """),
        (3, """
            CREATE TABLE site_settings (
                site TEXT PRIMARY KEY,
                shield_enabled INTEGER NOT NULL DEFAULT 1,
                updated_at INTEGER NOT NULL
            );
            """),
        (4, """
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
            """),
        (5, """
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
            """),
        (6, """
            CREATE TABLE decision_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at INTEGER NOT NULL,
                task TEXT NOT NULL,
                source TEXT NOT NULL,
                rule TEXT NOT NULL,
                model TEXT,
                data_class TEXT NOT NULL,
                redacted INTEGER NOT NULL,
                redaction_count INTEGER NOT NULL,
                input_chars INTEGER NOT NULL,
                output_preview TEXT NOT NULL,
                version TEXT NOT NULL
            );
            CREATE INDEX decision_log_at ON decision_log(at DESC);
            """),
        // Browser Memory (§8). External-content FTS5 keeps one copy of the text; triggers keep the index in sync.
        (7, """
            CREATE TABLE memory_docs (
                id TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                site TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                captured_at INTEGER NOT NULL,
                bytes INTEGER NOT NULL,
                text TEXT NOT NULL
            );
            CREATE INDEX memory_docs_at ON memory_docs(captured_at DESC);
            CREATE VIRTUAL TABLE memory_fts USING fts5(title, text, content='memory_docs', content_rowid='rowid', tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER memory_ai AFTER INSERT ON memory_docs BEGIN
                INSERT INTO memory_fts(rowid, title, text) VALUES (new.rowid, new.title, new.text);
            END;
            CREATE TRIGGER memory_ad AFTER DELETE ON memory_docs BEGIN
                INSERT INTO memory_fts(memory_fts, rowid, title, text) VALUES ('delete', old.rowid, old.title, old.text);
            END;
            CREATE TRIGGER memory_au AFTER UPDATE ON memory_docs BEGIN
                INSERT INTO memory_fts(memory_fts, rowid, title, text) VALUES ('delete', old.rowid, old.title, old.text);
                INSERT INTO memory_fts(rowid, title, text) VALUES (new.rowid, new.title, new.text);
            END;
            """),
        // DataClass gained Unknown = 1, shifting everything above it. site_settings.data_class holds user overrides
        // as ints, so they are remapped highest-first to avoid collisions. (decision_log stores class NAMES and
        // agent manifests serialise names, so neither needs migrating.)
        (8, """
            UPDATE site_settings SET data_class = 4 WHERE data_class = 3;
            UPDATE site_settings SET data_class = 3 WHERE data_class = 2;
            UPDATE site_settings SET data_class = 2 WHERE data_class = 1;
            """),
        // Pin and Keep active were one control. Splitting them needs no data migration on the protection column:
        // the old UserPinned bit (1 << 4) kept its value and is now named KeepActive, which is what it always did,
        // so an existing "pinned" tab keeps its no-sleep preference. Placement starts empty because it never existed.
        (9, """
            ALTER TABLE tabs ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX tabs_pinned ON tabs(workspace_id, pinned DESC, ordinal);
            """),
    ];

    /// <param name="targetVersion">Migrate only up to this version (used by recovery tests; production uses the latest).</param>
    public BrowserDb(string path, int? targetVersion = null)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString());
        Connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate(targetVersion ?? LatestVersion);
    }

    public SqliteConnection Connection { get; }

    public int UserVersion
    {
        get
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private void Migrate(int target)
    {
        var current = UserVersion;
        foreach (var (version, sql) in Steps.Where(s => s.Version > current && s.Version <= target).OrderBy(s => s.Version))
        {
            using var tx = Connection.BeginTransaction();
            try
            {
                Exec(sql);
                Exec($"PRAGMA user_version={version}");   // transactional: commits or rolls back together with the schema change
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Connection.Dispose();
}
