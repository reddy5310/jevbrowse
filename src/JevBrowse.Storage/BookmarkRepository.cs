using JevBrowse.Domain;

namespace JevBrowse.Storage;

/// <summary>Saved bookmarks. The address is the key: saving the same page twice updates it instead of duplicating it.</summary>
public sealed class BookmarkRepository
{
    private readonly BrowserDb _db;
    public BookmarkRepository(BrowserDb db) => _db = db;

    public bool Contains(string url)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM bookmarks WHERE url=$u";
        cmd.Parameters.AddWithValue("$u", url);
        return cmd.ExecuteScalar() is not null;
    }

    public void Save(Bookmark b) => SaveMany([b]);

    /// <summary>Adds bookmarks in one transaction. Existing ones keep their title and date (an import must not rewrite what the person already has). Returns how many were new.</summary>
    public int SaveMany(IEnumerable<Bookmark> items)
    {
        var added = 0;
        using var tx = _db.Connection.BeginTransaction();
        foreach (var b in items)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO bookmarks (url, title, folder, added_at) VALUES ($u, $t, $f, $a)";
            cmd.Parameters.AddWithValue("$u", b.Url);
            cmd.Parameters.AddWithValue("$t", b.Title);
            cmd.Parameters.AddWithValue("$f", b.Folder);
            cmd.Parameters.AddWithValue("$a", (b.AddedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
            added += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return added;
    }

    public bool Remove(string url)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM bookmarks WHERE url=$u";
        cmd.Parameters.AddWithValue("$u", url);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Newest first. <paramref name="filter"/> matches the title, address or folder, case-insensitively, as plain text (never as a pattern).</summary>
    public IReadOnlyList<Bookmark> List(string? filter = null, int limit = 500)
    {
        var all = new List<Bookmark>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT url, title, folder, added_at FROM bookmarks ORDER BY added_at DESC, url";
        using var r = cmd.ExecuteReader();
        var f = (filter ?? "").Trim();
        while (r.Read())
        {
            var b = new Bookmark(r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)));
            if (f.Length == 0 || b.Title.Contains(f, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(f, StringComparison.OrdinalIgnoreCase) || b.Folder.Contains(f, StringComparison.OrdinalIgnoreCase))
                all.Add(b);
            if (all.Count >= limit) break;
        }
        return all;
    }
}
