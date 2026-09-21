namespace JevBrowse.Storage;

/// <summary>Permission grants per site/kind with optional expiry (Table A.7 "temporary grants supported").</summary>
public sealed class SitePermissionsRepository
{
    public sealed record Row(string Site, int Kind, bool Allowed, DateTimeOffset? ExpiresAt, DateTimeOffset GrantedAt);

    private readonly BrowserDb _db;
    public SitePermissionsRepository(BrowserDb db) => _db = db;

    public Row? Get(string site, int kind)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT allowed, expires_at, granted_at FROM site_permissions WHERE site=$s AND kind=$k";
        cmd.Parameters.AddWithValue("$s", site);
        cmd.Parameters.AddWithValue("$k", kind);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Row(site, kind, r.GetInt32(0) != 0, r.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
    }

    public void Set(string site, int kind, bool allowed, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO site_permissions (site, kind, allowed, expires_at, granted_at) VALUES ($s, $k, $a, $e, $g)
            ON CONFLICT(site, kind) DO UPDATE SET allowed=$a, expires_at=$e, granted_at=$g
            """;
        cmd.Parameters.AddWithValue("$s", site);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$a", allowed ? 1 : 0);
        cmd.Parameters.AddWithValue("$e", (object?)expiresAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$g", now.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every remembered decision for exactly this container+origin key that is still in force (expired ones are not shown: they no longer decide anything).</summary>
    public IReadOnlyList<Row> ListForKey(string key, DateTimeOffset now)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT kind, allowed, expires_at, granted_at FROM site_permissions WHERE site=$s AND (expires_at IS NULL OR expires_at > $n) ORDER BY kind";
        cmd.Parameters.AddWithValue("$s", key);
        cmd.Parameters.AddWithValue("$n", now.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new Row(key, r.GetInt32(0), r.GetInt32(1) != 0, r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3))));
        return list;
    }

    /// <summary>Forgets remembered decisions for exactly this key: one kind, or all of them. Returns how many were removed. Other origins and identities are untouched.</summary>
    public int Delete(string key, int? kind = null)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = kind is null ? "DELETE FROM site_permissions WHERE site=$s" : "DELETE FROM site_permissions WHERE site=$s AND kind=$k";
        cmd.Parameters.AddWithValue("$s", key);
        if (kind is not null) cmd.Parameters.AddWithValue("$k", kind.Value);
        return cmd.ExecuteNonQuery();
    }

    public int PurgeExpired(DateTimeOffset now)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM site_permissions WHERE expires_at IS NOT NULL AND expires_at <= $n";
        cmd.Parameters.AddWithValue("$n", now.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery();
    }
}
