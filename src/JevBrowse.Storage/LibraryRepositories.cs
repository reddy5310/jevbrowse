using JevBrowse.Domain;

namespace JevBrowse.Storage;

public sealed record HistoryVisit(string Url, string Title, string Host, DateTimeOffset VisitedAt, int Visits);
public sealed record DownloadRecord(string Name, string Path, string SourceHost, DateTimeOffset FinishedAt, bool Completed);

/// <summary>
/// Pages visited in ordinary workspaces, most recent first. One row per address (a count and the latest time), so a busy day stays small.
/// What may be written is decided by the caller through Trust OS; this class only stores.
/// </summary>
public sealed class HistoryRepository
{
    private readonly BrowserDb _db;
    public HistoryRepository(BrowserDb db) => _db = db;
    private const int Cap = 20000;

    public void Record(string url, string title, DateTimeOffset at)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host)) return;
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history_visits (url, title, host, visited_at, visit_count) VALUES ($u, $t, $h, $a, 1)
            ON CONFLICT(url) DO UPDATE SET title=CASE WHEN $t <> '' THEN $t ELSE title END, visited_at=$a, visit_count=visit_count+1
            """;
        cmd.Parameters.AddWithValue("$u", u.AbsoluteUri);
        cmd.Parameters.AddWithValue("$t", title ?? "");
        cmd.Parameters.AddWithValue("$h", u.Host);
        cmd.Parameters.AddWithValue("$a", at.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        using var trim = _db.Connection.CreateCommand();
        trim.CommandText = "DELETE FROM history_visits WHERE url IN (SELECT url FROM history_visits ORDER BY visited_at DESC LIMIT -1 OFFSET $cap)";
        trim.Parameters.AddWithValue("$cap", Cap);
        trim.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryVisit> List(string? filter = null, int limit = 300)
    {
        var res = new List<HistoryVisit>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, host, visited_at, visit_count FROM history_visits ORDER BY visited_at DESC";
        using var r = cmd.ExecuteReader();
        var f = (filter ?? "").Trim();
        while (r.Read() && res.Count < limit)
        {
            var v = new HistoryVisit(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), r.GetInt32(4));
            if (f.Length == 0 || v.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || v.Url.Contains(f, StringComparison.OrdinalIgnoreCase)) res.Add(v);
        }
        return res;
    }

    public bool Remove(string url)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history_visits WHERE url=$u";
        cmd.Parameters.AddWithValue("$u", url);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int Clear()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM history_visits";
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>Files the person downloaded in ordinary workspaces (finished or interrupted). The list is a convenience; deleting a row never deletes the file.</summary>
public sealed class DownloadRepository
{
    private readonly BrowserDb _db;
    public DownloadRepository(BrowserDb db) => _db = db;

    public void Add(DownloadRecord d)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "INSERT INTO downloads (name, path, source_host, finished_at, completed) VALUES ($n, $p, $s, $t, $c)";
        cmd.Parameters.AddWithValue("$n", d.Name);
        cmd.Parameters.AddWithValue("$p", d.Path);
        cmd.Parameters.AddWithValue("$s", d.SourceHost);
        cmd.Parameters.AddWithValue("$t", d.FinishedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$c", d.Completed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<DownloadRecord> List(int limit = 200)
    {
        var res = new List<DownloadRecord>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name, path, source_host, finished_at, completed FROM downloads ORDER BY finished_at DESC, id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) res.Add(new DownloadRecord(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), r.GetInt32(4) != 0));
        return res;
    }

    public int Clear()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM downloads";
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>The zoom the person chose for a site, by exact host. 100% is stored as no row.</summary>
public sealed class SiteZoomRepository
{
    private readonly BrowserDb _db;
    public SiteZoomRepository(BrowserDb db) => _db = db;

    public double Get(string host)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT zoom FROM site_zoom WHERE host=$h";
        cmd.Parameters.AddWithValue("$h", host.ToLowerInvariant());
        return cmd.ExecuteScalar() is double d ? ZoomLevels.Sanitize(d) : ZoomLevels.Default;
    }

    public void Set(string host, double zoom)
    {
        host = host.ToLowerInvariant();
        using var cmd = _db.Connection.CreateCommand();
        if (Math.Abs(zoom - ZoomLevels.Default) < 0.001)
        {
            cmd.CommandText = "DELETE FROM site_zoom WHERE host=$h";
            cmd.Parameters.AddWithValue("$h", host);
        }
        else
        {
            cmd.CommandText = "INSERT INTO site_zoom (host, zoom) VALUES ($h, $z) ON CONFLICT(host) DO UPDATE SET zoom=$z";
            cmd.Parameters.AddWithValue("$h", host);
            cmd.Parameters.AddWithValue("$z", ZoomLevels.Sanitize(zoom));
        }
        cmd.ExecuteNonQuery();
    }
}
