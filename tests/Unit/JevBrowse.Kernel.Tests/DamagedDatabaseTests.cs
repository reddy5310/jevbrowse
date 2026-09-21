using System.Security.Cryptography;
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

namespace JevBrowse.Kernel.Tests;

/// <summary>A damaged browser.db must not leave the app unable to start, and must never be deleted: it is set aside so it can still be inspected or recovered.</summary>
public class DamagedDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jev-dmg-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "db", "browser.db");
    public DamagedDatabaseTests() => Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Opening a file that turns out to be damaged can rewrite its 100-byte header (the journal-mode bytes) before the damage is found; every page after it is untouched.
    private static string ShaAfterHeader(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path).AsSpan(100)));

    private void AssertUsableFreshDatabase(BrowserDb db)
    {
        Assert.Equal(BrowserDb.LatestVersion, db.UserVersion);
        db.Exec("INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ('00000000000000000000000000000001','https://example.com','x',0,0,0,0)");
        Assert.Single(new TabRepository(db).LoadAll());
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_moved_aside_intact_and_a_fresh_database_starts()
    {
        File.WriteAllBytes(Db, System.Text.Encoding.ASCII.GetBytes(new string('z', 5000)));
        var before = Sha(Db);

        using var db = BrowserDb.OpenOrRecover(Db, out var moved);

        Assert.NotNull(moved);
        Assert.True(File.Exists(moved), "the damaged file must be kept");
        Assert.Equal(before, Sha(moved!));           // byte for byte: nothing was rewritten or deleted
        AssertUsableFreshDatabase(db);
    }

    [Fact]
    public void A_database_with_damaged_pages_is_moved_aside_and_a_fresh_one_starts()
    {
        using (var good = new BrowserDb(Db))
        {
            for (var i = 0; i < 2000; i++) good.Exec($"INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ('{i:D32}','https://example.com/{i}','{new string('t', 200)}',0,0,{i},0)");
            good.Exec("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
        }
        SqliteConnection.ClearAllPools();
        // Overwrite a stretch in the middle of the file: the header is fine, so opening works and only reading the pages shows the damage.
        using (var fs = new FileStream(Db, FileMode.Open, FileAccess.Write)) { fs.Seek(fs.Length / 2, SeekOrigin.Begin); fs.Write(new byte[8192].Select(_ => (byte)0xAB).ToArray()); }
        var before = ShaAfterHeader(Db);

        using var db = BrowserDb.OpenOrRecover(Db, out var moved);

        Assert.NotNull(moved);
        Assert.Equal(before, ShaAfterHeader(moved!));   // every page as it was
        AssertUsableFreshDatabase(db);
    }

    [Fact]
    public void A_healthy_database_is_never_moved()
    {
        using (var good = new BrowserDb(Db)) good.Exec("INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ('00000000000000000000000000000002','https://example.com','keep',0,0,0,0)");
        SqliteConnection.ClearAllPools();

        using var db = BrowserDb.OpenOrRecover(Db, out var moved);

        Assert.Null(moved);
        Assert.Equal("keep", new TabRepository(db).LoadAll().Single().Title);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Db)!, "*.corrupt-*"));
    }

    [Fact]
    public void A_locked_database_is_not_mistaken_for_a_damaged_one()
    {
        using (var good = new BrowserDb(Db)) good.Exec("INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ('00000000000000000000000000000002','https://example.com','keep',0,0,0,0)");
        SqliteConnection.ClearAllPools();
        // Another process holds an exclusive lock: SQLITE_BUSY is the environment, not the file, so nothing may be quarantined.
        using var holder = new SqliteConnection($"Data Source={Db};Pooling=False");
        holder.Open();
        using (var cmd = holder.CreateCommand()) { cmd.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE;"; cmd.ExecuteNonQuery(); }

        var ex = Record.Exception(() => BrowserDb.OpenOrRecover(Db, out _));

        Assert.IsType<SqliteException>(ex);
        Assert.True(File.Exists(Db));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Db)!, "*.corrupt-*"));
    }

    [Fact]
    public void Two_recoveries_in_the_same_second_do_not_overwrite_each_other()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var moved = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            File.WriteAllText(Db, new string((char)('a' + i), 4000));
            using var db = BrowserDb.OpenOrRecover(Db, out var to, () => fixedTime);
            moved.Add(to!);
            db.Dispose(); SqliteConnection.ClearAllPools();
            File.Delete(Db); foreach (var s in new[] { "-wal", "-shm" }) if (File.Exists(Db + s)) File.Delete(Db + s);
        }
        Assert.Equal(2, moved.Distinct().Count());
        Assert.Equal(new string('a', 4000), File.ReadAllText(moved[0]));
        Assert.Equal(new string('b', 4000), File.ReadAllText(moved[1]));
    }
}
