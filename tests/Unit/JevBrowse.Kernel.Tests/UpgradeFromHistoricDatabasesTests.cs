using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// Upgrades from databases written by the real historic builds (tests/Fixtures/db/vN.db, made by scripts/make-db-fixtures.ps1 from the BrowserDb.cs of
/// the commit that introduced each version), not from databases this code re-creates. Also pins the shipped migration steps: a step that has been
/// released must never be edited, only followed by a new one.
/// </summary>
public class UpgradeFromHistoricDatabasesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jev-up-" + Guid.NewGuid().ToString("N"));
    public UpgradeFromHistoricDatabasesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    public static IEnumerable<object[]> Versions => Enumerable.Range(1, 9).Select(v => new object[] { v });

    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Default = "00000000000000000000000000000000";

    private string Copy(int version)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "db", $"v{version}.db");
        Assert.True(File.Exists(src), $"fixture missing: {src}");
        var dst = Path.Combine(_dir, $"upgrade-v{version}.db");
        File.Copy(src, dst);   // never open the fixture itself: opening can write
        return dst;
    }

    private static long Scalar(BrowserDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Text(BrowserDb db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Fact]
    public void The_fixtures_really_are_the_versions_they_claim_to_be()
    {
        foreach (var v in Enumerable.Range(1, 9))
        {
            using var c = new SqliteConnection($"Data Source={Copy(v)};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(v, Convert.ToInt32(cmd.ExecuteScalar()));
        }
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void A_database_from_every_historic_version_upgrades_intact(int from)
    {
        var path = Copy(from);
        using (var db = new BrowserDb(path))
        {
            Assert.Equal(BrowserDb.LatestVersion, db.UserVersion);
            Assert.Equal("ok", Text(db, "PRAGMA integrity_check"));
            Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM pragma_foreign_key_check"));

            // What the person had is still there, in every version.
            Assert.Equal(2, Scalar(db, "SELECT COUNT(*) FROM tabs"));
            Assert.Equal("Alpha", Text(db, $"SELECT title FROM tabs WHERE id='{A}'"));
            Assert.Equal("https://example.org/b", Text(db, $"SELECT url FROM tabs WHERE id='{B}'"));
            if (from >= 2) Assert.Equal(1234.5, Convert.ToDouble(Text(db, $"SELECT scroll_y FROM checkpoints WHERE id='{A}'"), System.Globalization.CultureInfo.InvariantCulture), 3);
            if (from >= 3) Assert.Equal(0, Scalar(db, "SELECT shield_enabled FROM site_settings WHERE site='example.com'"));
            if (from >= 4)
            {
                Assert.Equal("Work", Text(db, $"SELECT w.name FROM tabs t JOIN workspaces w ON w.id=t.workspace_id WHERE t.id='{B}'"));
                Assert.Equal(1, Scalar(db, $"SELECT COUNT(*) FROM workspaces WHERE id='{Default}'"));   // the Default workspace
            }
            else
            {
                // A version 1 to 3 database had no workspaces; every tab lands in Default.
                Assert.Equal(2, Scalar(db, $"SELECT COUNT(*) FROM tabs WHERE workspace_id='{Default}'"));
            }
            if (from >= 5) Assert.Equal(2, Scalar(db, "SELECT COUNT(*) FROM site_permissions"));
            if (from >= 6) Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM decision_log"));
            if (from >= 7)
            {
                Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM memory_docs"));
                Assert.Equal(1, Scalar(db, "SELECT COUNT(*) FROM memory_fts WHERE memory_fts MATCH 'fox'"));   // the full-text index came through
            }
            if (from >= 9) Assert.Equal(1, Scalar(db, $"SELECT pinned FROM tabs WHERE id='{A}'"));
            else Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM tabs WHERE pinned<>0"));   // pin did not exist; nothing becomes pinned by accident

            // The data-class renumbering (version 8) happens exactly once: overrides from 5..7 shift up by one, those already in the new numbering do not.
            if (from >= 5)
            {
                Assert.Equal(2, Scalar(db, "SELECT data_class FROM site_settings WHERE site='example.com'"));
                Assert.Equal(3, Scalar(db, "SELECT data_class FROM site_settings WHERE site='example.org'"));
                Assert.Equal(4, Scalar(db, "SELECT data_class FROM site_settings WHERE site='example.net'"));
                Assert.Equal(0, Scalar(db, "SELECT COUNT(*) FROM site_settings WHERE exact_host<>0"));   // older decisions stay marked as two-label rows
            }

            // And the app's own repository can read it.
            Assert.Equal(2, new TabRepository(db).LoadAll().Count);
        }

        // Reopening the upgraded file changes nothing.
        using var again = new BrowserDb(path);
        Assert.Equal(BrowserDb.LatestVersion, again.UserVersion);
        Assert.Equal("ok", Text(again, "PRAGMA integrity_check"));
    }

    [Fact]
    public void A_database_written_by_a_NEWER_build_is_refused_untouched_and_never_quarantined()
    {
        var path = Copy(9);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False")) { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
        SqliteConnection.ClearAllPools();
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var ex = Assert.Throws<UnsupportedDatabaseVersionException>(() => new BrowserDb(path));
        Assert.Equal((99, BrowserDb.LatestVersion), (ex.Found, ex.Supported));
        // The recovery path must not mistake it for damage: a newer database is somebody's healthy data.
        Assert.Throws<UnsupportedDatabaseVersionException>(() => BrowserDb.OpenOrRecover(path, out _));
        SqliteConnection.ClearAllPools();

        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));   // byte for byte: not written, not migrated, not moved
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*"));
        Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0, "no write reached a WAL");
    }

    // Recorded from the source history (git show of each commit's BrowserDb.cs), independently of the current file. If one of these fails,
    // a released migration step was edited: add a new step instead, because databases already migrated with the old text will not run it again.
    private static readonly Dictionary<int, string> Shipped = new()
    {
        [1] = "f30c0fe528c4", [2] = "f06179841d31", [3] = "0aa4f761c50a", [4] = "a45e01e80dda", [5] = "bbc57c72b4ff",
        [6] = "0ad8f3f247fd", [7] = "9f6b81febe53", [8] = "0b5e602eae91", [9] = "1054a3ea79a3", [10] = "abd187375ead",
    };

    [Fact]
    public void Released_migration_steps_are_never_edited()
    {
        var steps = ((int Version, string Sql)[])typeof(BrowserDb).GetField("Steps", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var (version, sql) in steps)
        {
            var body = string.Join("\n", sql.Trim().Split('\n').Select(l => l.Trim()));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()[..12];
            if (Shipped.TryGetValue(version, out var expected)) Assert.True(expected == hash, $"migration step {version} was edited (hash {hash}, released {expected})");
            else Assert.Fail($"step {version} is new: record its hash ({hash}) in Shipped once it is released");
        }
        Assert.Equal(BrowserDb.LatestVersion, steps.Length);
    }
}
