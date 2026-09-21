// A child process for crash tests. The test starts it, waits for its ready file, then KILLS it (TerminateProcess: no finally blocks, no flush, no dispose),
// which is what a crash, a task-manager kill or a forced logoff looks like to the files it had open. Then the test inspects what was left behind.
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

var mode = args[0];
switch (mode)
{
    case "write": Write(args[1], args[2], args[3]); break;
    case "migrate": Migrate(args[1], args[2]); break;
    case "migrate-hold": MigrateHold(args[1], args[2], int.Parse(args[3])); break;
    case "profile": Profile(args[1], args[2]); break;
    default: Console.Error.WriteLine("unknown mode"); return 2;
}
return 0;

// Commits transactions of 5 rows each, forever. After each COMMIT returns it appends the iteration number to <acked>: "the database told me it is durable".
static void Write(string dbPath, string ready, string acked)
{
    using var db = new BrowserDb(dbPath);
    File.WriteAllText(ready, "ready");
    var ackFile = new FileStream(acked, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    var pad = new string('x', 2000);   // enough volume that the WAL checkpoints while we run
    for (var i = 0; ; i++)
    {
        using (var tx = db.Connection.BeginTransaction())
        {
            for (var j = 0; j < 5; j++)
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ($id,$u,$t,0,0,$o,0)";
                cmd.Parameters.AddWithValue("$id", $"{i:D8}{j:D24}");
                cmd.Parameters.AddWithValue("$u", "https://example.com/" + pad);
                cmd.Parameters.AddWithValue("$t", $"iter-{i}");
                cmd.Parameters.AddWithValue("$o", i * 5 + j);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var line = System.Text.Encoding.ASCII.GetBytes(i + "\n");
        ackFile.Write(line); ackFile.Flush(true);
    }
}

// Opens a database that is behind (the test made it version 7 with a lot of rows), so BrowserDb's own migration runs and takes long enough to be killed in the middle.
static void Migrate(string dbPath, string started)
{
    File.WriteAllText(started, "starting");
    using var db = new BrowserDb(dbPath);
    File.WriteAllText(started + ".done", "done");
}

// Migrates, and when the step with the given version has run its SQL but NOT yet committed, says so and waits to be killed: a migration that is exactly half-done.
static void MigrateHold(string dbPath, string inside, int step)
{
    using var db = new BrowserDb(dbPath, beforeCommit: v => { if (v == step) { File.WriteAllText(inside, "inside " + v); Thread.Sleep(Timeout.Infinite); } });
}

// Owns an ephemeral (Private/Disposable-style) profile the way the real lease manager does, with a file inside it held open, then waits to be killed.
static void Profile(string root, string ready)
{
    var store = new EphemeralProfileStore(root);
    var path = store.Create();
    var held = new FileStream(Path.Combine(path, "Cookies"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
    held.Write(new byte[1024]); held.Flush();
    File.WriteAllText(Path.Combine(path, "Local State"), "{\"secret\":\"session-cookie\"}");
    File.WriteAllText(ready, path);
    Thread.Sleep(Timeout.Infinite);
}
