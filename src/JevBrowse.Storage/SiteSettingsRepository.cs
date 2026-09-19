namespace JevBrowse.Storage;

/// <summary>Per-site policy (§9.1 "per-site policy is one click"). Keyed by registrable domain.</summary>
public sealed class SiteSettingsRepository
{
    private readonly BrowserDb _db;
    public SiteSettingsRepository(BrowserDb db) => _db = db;

    public bool IsShieldEnabled(string site)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT shield_enabled FROM site_settings WHERE site=$s";
        cmd.Parameters.AddWithValue("$s", site);
        var r = cmd.ExecuteScalar();
        return r is null || Convert.ToInt32(r) != 0;
    }

    public void SetShieldEnabled(string site, bool enabled)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO site_settings (site, shield_enabled, updated_at) VALUES ($s, $e, $t)
            ON CONFLICT(site) DO UPDATE SET shield_enabled=$e, updated_at=$t
            """;
        cmd.Parameters.AddWithValue("$s", site);
        cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>User override of the data class for a site (null = let the classifier decide).</summary>
    public int? DataClassOverride(string site)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT data_class FROM site_settings WHERE site=$s";
        cmd.Parameters.AddWithValue("$s", site);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    public void SetDataClassOverride(string site, int? dataClass)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO site_settings (site, shield_enabled, data_class, updated_at) VALUES ($s, 1, $d, $t)
            ON CONFLICT(site) DO UPDATE SET data_class=$d, updated_at=$t
            """;
        cmd.Parameters.AddWithValue("$s", site);
        cmd.Parameters.AddWithValue("$d", (object?)dataClass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> DisabledSites()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT site FROM site_settings WHERE shield_enabled=0 ORDER BY site";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
