namespace JevBrowse.Storage;

/// <summary>Per-site policy (§9.1 "per-site policy is one click"). Shield switches are keyed by registrable domain; data-class decisions by exact host (see DataClassOverrideForHost).</summary>
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

    /// <summary>
    /// The person's data-class decision that applies to this HOST (null = none). An exact-host decision wins. A decision made before hosts were keyed exactly
    /// was stored under the last two labels of whatever site they were on; it is still honoured, but only when it is STRICTER than Unknown (Authenticated or
    /// Sensitive): applying an old "Public" to every host that shares two labels would loosen sites the person never looked at, so it is ignored until they
    /// decide again for the host itself.
    /// </summary>
    public int? DataClassOverrideForHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT data_class FROM site_settings WHERE site=$s AND exact_host=1";
            cmd.Parameters.AddWithValue("$s", host);
            var r = cmd.ExecuteScalar();
            if (r is not null and not DBNull) return Convert.ToInt32(r);
            if (r is DBNull) { /* an exact row without a class: fall through to the legacy rule */ }
        }
        var labels = host.Split('.');
        var legacyKey = labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
        using var legacy = _db.Connection.CreateCommand();
        legacy.CommandText = "SELECT data_class FROM site_settings WHERE site=$s AND exact_host=0 AND data_class >= 2";   // Authenticated, Sensitive
        legacy.Parameters.AddWithValue("$s", legacyKey);
        var l = legacy.ExecuteScalar();
        return l is null or DBNull ? null : Convert.ToInt32(l);
    }

    /// <summary>The decision stored for exactly this host (null = none), for showing in the dialog. Ignores the old two-label rows.</summary>
    public int? ExactDataClassOverride(string host)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT data_class FROM site_settings WHERE site=$s AND exact_host=1";
        cmd.Parameters.AddWithValue("$s", host.Trim().TrimEnd('.').ToLowerInvariant());
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? null : Convert.ToInt32(r);
    }

    /// <summary>Records a decision for exactly this host.</summary>
    public void SetDataClassOverrideForHost(string host, int? dataClass)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO site_settings (site, shield_enabled, data_class, exact_host, updated_at) VALUES ($s, 1, $d, 1, $t)
            ON CONFLICT(site) DO UPDATE SET data_class=$d, exact_host=1, updated_at=$t
            """;
        cmd.Parameters.AddWithValue("$s", host.Trim().TrimEnd('.').ToLowerInvariant());
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
