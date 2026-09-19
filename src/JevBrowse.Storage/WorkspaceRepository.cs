using System.Text.Json;
using JevBrowse.Domain;

namespace JevBrowse.Storage;

public sealed class WorkspaceRepository
{
    private readonly BrowserDb _db;
    public WorkspaceRepository(BrowserDb db) => _db = db;

    public IReadOnlyList<Workspace> LoadAll()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, name, background_priority, notifications_muted, created_at FROM workspaces ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<Workspace>();
        while (r.Read())
            list.Add(new Workspace(new ContextId(Guid.ParseExact(r.GetString(0), "N")), r.GetString(1))
            {
                BackgroundPriority = r.GetDouble(2),
                NotificationsMuted = r.GetInt32(3) != 0,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
            });
        return list;
    }

    public void Upsert(Workspace w)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO workspaces (id, name, background_priority, notifications_muted, created_at)
            VALUES ($id, $name, $prio, $muted, $at)
            ON CONFLICT(id) DO UPDATE SET name=$name, background_priority=$prio, notifications_muted=$muted
            """;
        cmd.Parameters.AddWithValue("$id", w.Id.ToString());
        cmd.Parameters.AddWithValue("$name", w.Name);
        cmd.Parameters.AddWithValue("$prio", w.BackgroundPriority);
        cmd.Parameters.AddWithValue("$muted", w.NotificationsMuted ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", w.CreatedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void Delete(ContextId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM workspaces WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    // ---- Time Travel ----

    private sealed record EntryDto(string Id, string Url, string Title, bool Live);

    public void SaveCheckpoint(ContextCheckpoint c)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO context_checkpoints (id, workspace_id, workspace_name, at, active_resource, live_count, resources_json)
            VALUES ($id, $ws, $name, $at, $active, $live, $json)
            """;
        cmd.Parameters.AddWithValue("$id", c.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$ws", c.WorkspaceId.ToString());
        cmd.Parameters.AddWithValue("$name", c.WorkspaceName);
        cmd.Parameters.AddWithValue("$at", c.At.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$active", (object?)c.ActiveResource?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$live", c.LiveCount);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(c.Resources.Select(e => new EntryDto(e.Id.ToString(), e.Url.ToString(), e.Title, e.WasLive))));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ContextCheckpoint> ListCheckpoints(int limit = 200)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id, workspace_id, workspace_name, at, active_resource, live_count, resources_json FROM context_checkpoints ORDER BY at DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ContextCheckpoint>();
        while (r.Read())
        {
            var entries = (JsonSerializer.Deserialize<List<EntryDto>>(r.GetString(6)) ?? [])
                .Select(e => new ContextCheckpointEntry(new ResourceId(Guid.ParseExact(e.Id, "N")), new Uri(e.Url), e.Title, e.Live)).ToList();
            list.Add(new ContextCheckpoint(
                Guid.ParseExact(r.GetString(0), "N"),
                new ContextId(Guid.ParseExact(r.GetString(1), "N")),
                r.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                r.IsDBNull(4) ? null : new ResourceId(Guid.ParseExact(r.GetString(4), "N")),
                entries,
                r.GetInt32(5)));
        }
        return list;
    }

    /// <summary>Retention: keep the newest N per workspace and everything younger than maxAge (§7.1 local-first, bounded).</summary>
    public int PruneCheckpoints(int keepPerWorkspace, TimeSpan maxAge, DateTimeOffset now)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM context_checkpoints WHERE at < $cutoff AND id NOT IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY workspace_id ORDER BY at DESC) AS rn FROM context_checkpoints
                ) WHERE rn <= $keep)
            """;
        cmd.Parameters.AddWithValue("$cutoff", (now - maxAge).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$keep", keepPerWorkspace);
        return cmd.ExecuteNonQuery();
    }
}
