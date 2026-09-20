using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

namespace JevBrowse.Kernel.Tests;

/// <summary>Crash-safety claims, tested as behaviour: a failed migration must leave a database that can still start.</summary>
public class StorageRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jev-db-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "browser.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static void Raw(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool Has(string path, string sql)
    {
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public void A_failed_migration_step_rolls_back_completely_and_the_next_start_recovers()
    {
        using (new BrowserDb(DbPath, targetVersion: 3)) { }                       // an older install at schema v3
        SqliteConnection.ClearAllPools();
        // Step 4 does CREATE TABLE, INSERT, ALTER TABLE, CREATE INDEX, and finally CREATE TABLE context_checkpoints.
        // Pre-create a conflicting table so it fails AFTER the ALTER already ran.
        Raw(DbPath, "CREATE TABLE context_checkpoints (x INTEGER)");

        Assert.ThrowsAny<SqliteException>(() => new BrowserDb(DbPath));

        SqliteConnection.ClearAllPools();
        Assert.Equal(3, ReadVersion());                                            // version did not advance
        Assert.False(Has(DbPath, "SELECT COUNT(*) FROM pragma_table_info('tabs') WHERE name='workspace_id'"));   // the ALTER was rolled back
        Assert.False(Has(DbPath, "SELECT COUNT(*) FROM sqlite_master WHERE name='workspaces'"));

        // The operator (or a fixed build) removes the obstacle; startup must now succeed. Before the fix the ALTER
        // had already been applied, so re-running step 4 failed with "duplicate column" on every start, forever.
        Raw(DbPath, "DROP TABLE context_checkpoints");
        using var db = new BrowserDb(DbPath);
        Assert.Equal(BrowserDb.LatestVersion, db.UserVersion);
        Assert.True(Has(DbPath, "SELECT COUNT(*) FROM pragma_table_info('tabs') WHERE name='workspace_id'"));
    }

    [Fact]
    public void Reopening_a_current_database_is_a_no_op_and_every_step_advances_the_version_exactly_once()
    {
        for (int v = 1; v <= BrowserDb.LatestVersion; v++)
        {
            var p = Path.Combine(_dir, $"v{v}.db");
            using (var d = new BrowserDb(p, targetVersion: v)) Assert.Equal(v, d.UserVersion);
            SqliteConnection.ClearAllPools();
        }
        using var again = new BrowserDb(Path.Combine(_dir, "v7.db"));
        Assert.Equal(BrowserDb.LatestVersion, again.UserVersion);
    }

    private int ReadVersion()
    {
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
