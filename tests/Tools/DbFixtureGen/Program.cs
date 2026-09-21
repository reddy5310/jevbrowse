// Generates a database with the schema and seed data of ONE historic build. It is compiled next to that build's own BrowserDb.cs
// (see scripts/make-db-fixtures.ps1), so the file it writes was produced by the real historic migration code, not by a re-creation.
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

var version = int.Parse(args[0]);
var path = args[1];
if (File.Exists(path)) File.Delete(path);
using (var db = new BrowserDb(path))
{
    using (var q = db.Connection.CreateCommand())
    {
        q.CommandText = "PRAGMA user_version";
        var got = Convert.ToInt32(q.ExecuteScalar());
        if (got != version) throw new InvalidOperationException($"historic build produced schema {got}, expected {version}");
    }
    const long T = 1_780_000_000_000;
    const string Default = "00000000000000000000000000000000";
    const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", C = "cccccccccccccccccccccccccccccccc";
    // Seed only what this version's schema has. The same rows are seeded in every version, so what a person had is comparable after upgrade.
    db.Exec($"INSERT INTO tabs (id,url,title,state,protection,ordinal,last_state_change) VALUES ('{A}','https://example.com/a','Alpha',0,0,0,{T}),('{B}','https://example.org/b','Beta',1,16,1,{T})");
    if (version >= 2) db.Exec($"INSERT INTO checkpoints (id,url,title,scroll_x,scroll_y,favicon_url,thumbnail_path,captured_at) VALUES ('{A}','https://example.com/a#read','Alpha',0,1234.5,NULL,NULL,{T})");
    if (version >= 3) db.Exec($"INSERT INTO site_settings (site,shield_enabled,updated_at) VALUES ('example.com',0,{T}),('example.org',1,{T}),('example.net',1,{T})");
    if (version >= 4)
    {
        db.Exec($"INSERT INTO workspaces (id,name,background_priority,notifications_muted,created_at) VALUES ('{C}','Work',0.6,1,{T})");
        db.Exec($"UPDATE tabs SET workspace_id='{C}' WHERE id='{B}'");
        db.Exec($"INSERT INTO context_checkpoints (id,workspace_id,workspace_name,at,active_resource,live_count,resources_json) VALUES ('cp1','{C}','Work',{T},'{B}',1,'[]')");
    }
    if (version >= 5)
    {
        // Overrides, in the numbering of the time: before version 8 the classes were 1,2,3 and afterwards 2,3,4.
        var (x, y, z) = version >= 8 ? (2, 3, 4) : (1, 2, 3);
        db.Exec($"UPDATE site_settings SET data_class={x} WHERE site='example.com'; UPDATE site_settings SET data_class={y} WHERE site='example.org'; UPDATE site_settings SET data_class={z} WHERE site='example.net'");
        db.Exec($"UPDATE workspaces SET container=2 WHERE id='{C}'");
        db.Exec($"INSERT INTO site_permissions (site,kind,allowed,expires_at,granted_at) VALUES ('example.com',1,1,NULL,{T}),('example.org',2,0,{T + 3_600_000},{T})");
    }
    if (version >= 6) db.Exec($"INSERT INTO decision_log (at,task,source,rule,model,data_class,redacted,redaction_count,input_chars,output_preview,version) VALUES ({T},'summarize','rule','r1',NULL,'Public',0,0,10,'ok','1')");
    if (version >= 7) db.Exec($"INSERT INTO memory_docs (id,url,title,site,workspace_id,captured_at,bytes,text) VALUES ('m1','https://example.com/a','Alpha notes','example.com','{Default}',{T},22,'the quick brown fox jumps')");
    if (version >= 9) db.Exec($"UPDATE tabs SET pinned=1 WHERE id='{A}'");
    // A single self-contained file: no -wal / -shm beside it.
    db.Exec("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
}
SqliteConnection.ClearAllPools();
Console.WriteLine($"wrote {path} at schema {version}");
