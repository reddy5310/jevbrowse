using JevBrowse.Brain;
using JevBrowse.Domain;
using JevBrowse.Storage;
using Microsoft.Data.Sqlite;

namespace JevBrowse.Memory;

public sealed record MemoryHit(ResourceId Id, Uri Url, string Title, string Site, ContextId WorkspaceId, DateTimeOffset CapturedAt, string Snippet, double Score);

/// <summary>
/// Browser Memory (§8): a local knowledge layer over pages Trust OS allowed us to index. Query path is
/// FTS5 (bm25) → local boosts → optional Jev rerank, and never uploads history. Disk growth is bounded by a byte budget.
/// The caller (not this class) is responsible for the Trust OS check before calling Index().
/// </summary>
public sealed class BrowserMemory
{
    private readonly BrowserDb _db;
    private readonly long _budgetBytes;
    private readonly Func<DateTimeOffset> _clock;

    public BrowserMemory(BrowserDb db, long budgetBytes = 200L * 1024 * 1024, Func<DateTimeOffset>? clock = null)
    {
        _db = db;
        _budgetBytes = budgetBytes;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Index(ResourceId id, Uri url, string title, ContextId workspace, string text)
    {
        text = Normalize(text);
        if (text.Length < 200) return; // not an article: nav-only pages, blank tabs, error pages
        if (text.Length > 200_000) text = text[..200_000];
        using var tx = _db.Connection.BeginTransaction();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO memory_docs (id, url, title, site, workspace_id, captured_at, bytes, text)
                VALUES ($id, $url, $title, $site, $ws, $at, $bytes, $text)
                ON CONFLICT(id) DO UPDATE SET url=$url, title=$title, site=$site, workspace_id=$ws, captured_at=$at, bytes=$bytes, text=$text
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$url", url.ToString());
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$site", url.Host.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$ws", workspace.ToString());
            cmd.Parameters.AddWithValue("$at", _clock().ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$bytes", System.Text.Encoding.UTF8.GetByteCount(text));
            cmd.Parameters.AddWithValue("$text", text);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        EnforceBudget();
    }

    public void Forget(ResourceId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_docs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    public (int Docs, long Bytes) Stats()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes),0) FROM memory_docs";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt64(1));
    }

    /// <summary>Keyword search. Local only; nothing leaves the machine.</summary>
    public IReadOnlyList<MemoryHit> Search(string query, int limit = 20, ContextId? workspace = null)
    {
        var fts = ToFtsQuery(query);
        if (fts.Length == 0) return [];
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.id, d.url, d.title, d.site, d.workspace_id, d.captured_at,
                   snippet(memory_fts, 1, '', '', '…', 18) AS snip,
                   bm25(memory_fts, 3.0, 1.0) AS rank
            FROM memory_fts JOIN memory_docs d ON d.rowid = memory_fts.rowid
            WHERE memory_fts MATCH $q
            ORDER BY rank LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$q", fts);
        cmd.Parameters.AddWithValue("$n", limit * 3);
        var now = _clock();
        var hits = new List<MemoryHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ws = new ContextId(Guid.ParseExact(r.GetString(4), "N"));
            var at = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5));
            // bm25 is lower-is-better; convert to a positive score then apply local boosts (§8 "local ranking").
            var score = 1.0 / (1.0 + Math.Max(0, r.GetDouble(7)));
            var ageDays = Math.Max(0, (now - at).TotalDays);
            score *= 1.0 + 0.5 * Math.Exp(-ageDays / 14.0);            // recency: up to +50% within ~2 weeks
            if (workspace is { } w && ws == w) score *= 1.25;           // same workspace
            hits.Add(new MemoryHit(new ResourceId(Guid.ParseExact(r.GetString(0), "N")), new Uri(r.GetString(1)), r.GetString(2), r.GetString(3), ws, at, r.GetString(6), score));
        }
        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    /// <summary>
    /// Optional Jev/LLM rerank of the top local hits. Only titles/snippets of already-indexed (PUBLIC) pages are sent,
    /// never the query history. Falls back to local order on any refusal.
    /// </summary>
    public async Task<IReadOnlyList<MemoryHit>> SearchWithRerankAsync(string query, IBrainRouter brain, ContextId? workspace, CancellationToken ct)
    {
        var local = Search(query, 10, workspace);
        if (local.Count < 3) return local;
        var prompt = $"Query: {query}\n" + string.Join("\n", local.Select((h, i) => $"{i + 1}. {h.Title} — {h.Snippet}"));
        var d = await brain.DecideAsync(new DecisionRequest(BrainTask.RerankSearch, prompt, DataClass.Public, IdentityContainer.Personal, ExplicitUserAction: true), ct);
        if (d.WasDenied) return local;
        var order = d.Output.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim('.'), out var n) ? n - 1 : -1)
            .Where(i => i >= 0 && i < local.Count).Distinct().ToList();
        if (order.Count == 0) return local;
        return order.Select(i => local[i]).Concat(local.Where((_, i) => !order.Contains(i))).ToList();
    }

    private void EnforceBudget()
    {
        var (_, bytes) = Stats();
        if (bytes <= _budgetBytes) return;
        // Drop oldest until under budget: bounded disk growth (Constitution / cost card).
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memory_docs WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid, SUM(bytes) OVER (ORDER BY captured_at DESC) AS running FROM memory_docs
                ) WHERE running > $budget)
            """;
        cmd.Parameters.AddWithValue("$budget", _budgetBytes);
        cmd.ExecuteNonQuery();
    }

    private static string Normalize(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool ws = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { if (!ws) sb.Append(' '); ws = true; }
            else { sb.Append(ch); ws = false; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Turn free text into a safe FTS5 query: quoted tokens joined by implicit AND, trailing prefix match.</summary>
    private static string ToFtsQuery(string q)
    {
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length >= 2).ToList();
        if (tokens.Count == 0) return "";
        return string.Join(" ", tokens.Select((t, i) => i == tokens.Count - 1 ? $"\"{t}\"*" : $"\"{t}\""));
    }
}
