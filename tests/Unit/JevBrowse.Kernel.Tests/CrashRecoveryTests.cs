using System.Diagnostics;
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// Real interruption, not simulated failure: a child process (tests/Tools/CrashHarness) is started and then KILLED with TerminateProcess while it is writing,
/// migrating or holding a throwaway profile. Nothing in the child gets to flush, dispose or clean up. The test then inspects the files it left.
/// What this can show: a process death (crash, task-manager kill, forced logoff). What it cannot: a power cut or a failing disk, where the OS cache is lost too.
/// The database runs WAL with synchronous=NORMAL, which keeps the file consistent through a power cut but may lose the last few commits; that is not tested here.
/// </summary>
public class CrashRecoveryTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jev-crash-" + Guid.NewGuid().ToString("N"));
    public CrashRecoveryTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static Process Start(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "CrashHarness.dll");
        Assert.True(File.Exists(dll), "CrashHarness.dll missing next to the tests");
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static void WaitFor(string file, Process p, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!File.Exists(file))
        {
            if (p.HasExited) throw new InvalidOperationException("harness exited early: " + p.StandardError.ReadToEnd());
            if (DateTime.UtcNow > until) throw new TimeoutException("harness did not become ready: " + file);
            Thread.Sleep(10);
        }
    }

    private static void Crash(Process p) { p.Kill(entireProcessTree: false); p.WaitForExit(); }   // TerminateProcess: nothing in the child runs again

    private static long Scalar(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }

    // ---- interrupted writes ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_process_killed_mid_write_leaves_a_consistent_database_with_every_acknowledged_commit()
    {
        var rng = new Random(20260921);
        var (maxAcked, partials) = (0, 0);
        for (var trial = 0; trial < 20; trial++)
        {
            var db = Path.Combine(_dir, $"w{trial}", "browser.db"); Directory.CreateDirectory(Path.GetDirectoryName(db)!);
            var ready = Path.Combine(_dir, $"w{trial}", "ready"); var acked = Path.Combine(_dir, $"w{trial}", "acked");
            using var p = Start("write", db, ready, acked);
            WaitFor(ready, p);
            Thread.Sleep(rng.Next(30, 400));   // somewhere inside the write loop, including across WAL checkpoints
            Crash(p);

            var lastAcked = File.ReadAllLines(acked).Where(l => l.Length > 0).Select(int.Parse).DefaultIfEmpty(-1).Max();
            maxAcked = Math.Max(maxAcked, lastAcked);
            using var reopened = new BrowserDb(db);   // recovery runs here: WAL replay, then the migration check
            Assert.Equal(BrowserDb.LatestVersion, reopened.UserVersion);
            Assert.Equal("ok", Convert.ToString(new Func<object?>(() => { using var c = reopened.Connection.CreateCommand(); c.CommandText = "PRAGMA integrity_check"; return c.ExecuteScalar(); })()));

            // Atomicity: every iteration is present with all 5 rows or not at all.
            using var q = reopened.Connection.CreateCommand();
            q.CommandText = "SELECT title, COUNT(*) FROM tabs GROUP BY title";
            using var r = q.ExecuteReader();
            var present = new HashSet<int>();
            while (r.Read())
            {
                if (r.GetInt32(1) != 5) partials++;
                present.Add(int.Parse(r.GetString(0)["iter-".Length..]));
            }
            // Durability against a process kill: everything the database acknowledged is there.
            for (var i = 0; i <= lastAcked; i++) Assert.True(present.Contains(i), $"trial {trial}: iteration {i} was acknowledged but is missing");
        }
        Assert.Equal(0, partials);
        _out.WriteLine($"20 kills at random points; most iterations acknowledged before a kill: {maxAcked}; partial transactions found: {partials}");
    }

    // ---- interrupted migration ---------------------------------------------------------------------------------------------------------------

    private string BigVersion7Database(string name, int rows)
    {
        var path = Path.Combine(_dir, name + ".db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "db", "v7.db"), path);
        using var c = new SqliteConnection($"Data Source={path}"); c.Open();
        using var cmd = c.CreateCommand();
        // site_settings.data_class overrides in the old numbering (1..3); step 8 must renumber each of them exactly once.
        cmd.CommandText = $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<{rows}) INSERT INTO site_settings (site,shield_enabled,updated_at,data_class) SELECT 'site' || i, 1, 0, 1 + (i % 3) FROM n";
        cmd.ExecuteNonQuery();
        return path;
    }

    private static (long c1, long c2, long c3, long c4) ClassCounts(SqliteConnection c) => (
        Scalar(c, "SELECT COUNT(*) FROM site_settings WHERE data_class=1"), Scalar(c, "SELECT COUNT(*) FROM site_settings WHERE data_class=2"),
        Scalar(c, "SELECT COUNT(*) FROM site_settings WHERE data_class=3"), Scalar(c, "SELECT COUNT(*) FROM site_settings WHERE data_class=4"));

    [Theory]
    [InlineData(8)]   // the data renumbering: UPDATEs have run, uncommitted
    [InlineData(9)]   // a column added and an index built, uncommitted
    public void A_process_killed_INSIDE_a_migration_step_before_it_commits_changes_nothing_and_the_next_start_completes_it(int step)
    {
        var db = BigVersion7Database("hold" + step, 30_000);
        if (step == 9)   // start from a finished version 8 so step 9 is the one in flight
        {
            using var b = new BrowserDb(db, targetVersion: 8);
        }
        SqliteConnection.ClearAllPools();
        var inside = Path.Combine(_dir, $"inside{step}");
        using var p = Start("migrate-hold", db, inside, step.ToString());
        WaitFor(inside, p);          // the step's SQL has executed; its transaction is open
        Crash(p);

        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            Assert.Equal(step - 1, (int)Scalar(c, "PRAGMA user_version"));   // the half-done step left no trace of itself
            var (c1, c2, c3, c4) = ClassCounts(c);
            if (step == 8) Assert.Equal((10_001, 10_001, 10_001, 0), (c1, c2, c3, c4));
            else Assert.Equal((0, 10_001, 10_001, 10_001), (c1, c2, c3, c4));
            Assert.Equal(0, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('tabs') WHERE name='pinned'"));   // the column is not there: the ALTER rolled back too
            Assert.Equal("ok", new Func<string>(() => { using var q = c.CreateCommand(); q.CommandText = "PRAGMA integrity_check"; return (string)q.ExecuteScalar()!; })());
        }
        SqliteConnection.ClearAllPools();
        using var next = new BrowserDb(db);
        Assert.Equal(BrowserDb.LatestVersion, next.UserVersion);
        var (d1, d2, d3, d4) = ClassCounts(next.Connection);
        Assert.Equal((0, 10_001, 10_001, 10_001), (d1, d2, d3, d4));
        Assert.Equal(1, Scalar(next.Connection, "SELECT COUNT(*) FROM pragma_table_info('tabs') WHERE name='pinned'"));
    }

    // ---- throwaway profiles ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_killed_session_leaves_its_profile_on_disk_and_the_next_start_sweeps_it_including_the_secrets_in_it()
    {
        var root = Path.Combine(_dir, "ephemeral"); var ready = Path.Combine(_dir, "profile.ready");
        using var p = Start("profile", root, ready);
        WaitFor(ready, p);
        var profile = File.ReadAllText(ready);
        Assert.True(File.Exists(Path.Combine(profile, "Local State")));

        // Another instance of the app starting up while the session is alive must not touch it.
        using (var other = new EphemeralProfileStore(root)) { other.Sweep(); }
        Assert.True(Directory.Exists(profile), "a live session's profile was swept by another instance");

        Crash(p);
        Assert.True(Directory.Exists(profile), "a crash cannot clean up after itself: the profile is expected to still be there");

        using (var next = new EphemeralProfileStore(root)) { next.Sweep(); }   // what WebView2LeaseManager does at start
        Assert.False(Directory.Exists(profile));
        Assert.False(File.Exists(profile + ".lock"));
    }

    [Fact]
    public async Task Cleanup_retries_while_something_still_holds_a_file_and_gives_up_without_losing_track()
    {
        var root = Path.Combine(_dir, "retry");
        using var store = new EphemeralProfileStore(root);
        var path = store.Create();
        var held = new FileStream(Path.Combine(path, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);   // antivirus, a straggling engine process

        var end = store.EndAsync(path);
        await Task.Delay(700);
        Assert.False(end.IsCompleted, "should still be retrying while the file is held");
        held.Dispose();
        Assert.True(await end);                       // released: the retry succeeds
        Assert.False(Directory.Exists(path));

        // Held for the whole retry budget: EndAsync reports failure, the directory stays, and a later sweep removes it once the handle is gone.
        var path2 = store.Create();
        var held2 = new FileStream(Path.Combine(path2, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.False(await store.EndAsync(path2));
        Assert.True(Directory.Exists(path2));
        held2.Dispose();
        store.Sweep();
        Assert.False(Directory.Exists(path2));
    }
}
