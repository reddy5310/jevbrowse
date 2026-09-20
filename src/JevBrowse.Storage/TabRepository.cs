using JevBrowse.Domain;
using Microsoft.Data.Sqlite;

namespace JevBrowse.Storage;

public sealed record TabRow(ResourceId Id, Uri Url, string Title, ResourceState State, ProtectionFlags Protection, int Ordinal, DateTimeOffset LastStateChange, ContextId WorkspaceId, bool Pinned = false);

public sealed class TabRepository
{
    private readonly BrowserDb _db;
    public TabRepository(BrowserDb db) => _db = db;

    public void Upsert(VirtualTab tab, int ordinal)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tabs (id, url, title, state, protection, ordinal, last_state_change, workspace_id, pinned)
            VALUES ($id, $url, $title, $state, $prot, $ord, $ts, $ws, $pin)
            ON CONFLICT(id) DO UPDATE SET url=$url, title=$title, state=$state, protection=$prot, ordinal=$ord, last_state_change=$ts, workspace_id=$ws, pinned=$pin
            """;
        cmd.Parameters.AddWithValue("$id", tab.Id.ToString());
        cmd.Parameters.AddWithValue("$url", tab.Url.ToString());
        cmd.Parameters.AddWithValue("$title", tab.Title);
        // Persisted state is never "live": a renderer does not survive a process, so anything live is stored as Virtual.
        cmd.Parameters.AddWithValue("$state", (int)(tab.State.HasLiveRenderer() ? ResourceState.Virtual : tab.State));
        cmd.Parameters.AddWithValue("$prot", (int)tab.UserProtection); // detected flags are transient
        cmd.Parameters.AddWithValue("$ord", ordinal);
        cmd.Parameters.AddWithValue("$ts", tab.LastStateChange.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$ws", tab.WorkspaceId.ToString());
        cmd.Parameters.AddWithValue("$pin", tab.IsPinned ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void Delete(ResourceId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tabs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<TabRow> LoadAll()
    {
        using var cmd = _db.Connection.CreateCommand();
        // Pinned tabs come back at the top of their workspace, which is the whole point of pinning them.
        cmd.CommandText = "SELECT id, url, title, state, protection, ordinal, last_state_change, workspace_id, pinned FROM tabs ORDER BY pinned DESC, ordinal";
        using var r = cmd.ExecuteReader();
        var rows = new List<TabRow>();
        while (r.Read())
        {
            rows.Add(new TabRow(
                new ResourceId(Guid.ParseExact(r.GetString(0), "N")),
                new Uri(r.GetString(1)),
                r.GetString(2),
                (ResourceState)r.GetInt32(3),
                (ProtectionFlags)r.GetInt32(4),
                r.GetInt32(5),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
                new ContextId(Guid.ParseExact(r.GetString(7), "N")),
                r.GetInt32(8) != 0));
        }
        return rows;
    }

    public SqliteTransaction BeginTransaction() => _db.Connection.BeginTransaction();
}
