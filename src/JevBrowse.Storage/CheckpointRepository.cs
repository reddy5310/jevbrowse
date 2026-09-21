using JevBrowse.Domain;

namespace JevBrowse.Storage;

public sealed class CheckpointRepository
{
    private readonly BrowserDb _db;
    public CheckpointRepository(BrowserDb db) => _db = db;

    public void Upsert(Checkpoint c)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkpoints (id, url, title, scroll_x, scroll_y, favicon_url, thumbnail_path, captured_at, history_json)
            VALUES ($id, $url, $title, $sx, $sy, $fav, $thumb, $at, $hist)
            ON CONFLICT(id) DO UPDATE SET url=$url, title=$title, scroll_x=$sx, scroll_y=$sy, favicon_url=$fav, thumbnail_path=$thumb, captured_at=$at, history_json=$hist
            """;
        cmd.Parameters.AddWithValue("$id", c.Id.ToString());
        cmd.Parameters.AddWithValue("$url", c.Url.ToString());
        cmd.Parameters.AddWithValue("$title", c.Title);
        cmd.Parameters.AddWithValue("$sx", c.ScrollX);
        cmd.Parameters.AddWithValue("$sy", c.ScrollY);
        cmd.Parameters.AddWithValue("$fav", (object?)c.FaviconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)c.ThumbnailPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", c.CapturedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$hist", c.History is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(c.History));
        cmd.ExecuteNonQuery();
    }

    public Checkpoint? Get(ResourceId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, scroll_x, scroll_y, favicon_url, thumbnail_path, captured_at, history_json FROM checkpoints WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Checkpoint(id, new Uri(r.GetString(0)), r.GetString(1), r.GetDouble(2), r.GetDouble(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)), ReadHistory(r.IsDBNull(7) ? null : r.GetString(7)));
    }

    private static NavHistory? ReadHistory(string? json)
    {
        if (json is null) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<NavHistory>(json); }
        catch (Exception) { return null; }   // a history that cannot be read is simply not restored
    }

    public void Delete(ResourceId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM checkpoints WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }
}
