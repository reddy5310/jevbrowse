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

    /// <summary>
    /// One document per PAGE, not per tab: navigating within a tab adds the new page instead of replacing the old
    /// one. Key = "{tabId}:{hash of URL without fragment}", so the owning tab is recoverable and revisiting a page
    /// refreshes its entry.
    /// </summary>
    private static string DocKey(ResourceId id, Uri url)
    {
        var norm = url.GetLeftPart(UriPartial.Query);
        var h = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(norm)))[..8].ToLowerInvariant();
        return $"{id}:{h}";
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
            cmd.Parameters.AddWithValue("$id", DocKey(id, url));
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

    /// <summary>Forget one page (used when Trust OS tightens the class of the page a tab is showing).</summary>
    public void Forget(ResourceId id, Uri url)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_docs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", DocKey(id, url));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forget everything indexed from one host, for every page and every tab (the person told us how to treat that site).</summary>
    public int ForgetSite(string host)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_docs WHERE site=$h";
        cmd.Parameters.AddWithValue("$h", host.Trim().TrimEnd('.').ToLowerInvariant());
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Forget everything indexed from a tab (explicit user request).</summary>
    public void ForgetTab(ResourceId id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_docs WHERE id=$id OR id LIKE $p";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$p", id + ":%");
        cmd.ExecuteNonQuery();
    }

    /// <summary>Delete the whole index. Closing a tab does NOT do this: what you read stays findable until you say otherwise.</summary>
    public int Clear()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM memory_docs";
        return cmd.ExecuteNonQuery();
    }

    public (int Docs, long Bytes) Stats()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes),0) FROM memory_docs";
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt32(0), r.GetInt64(1));
    }

    /// <summary>Actual size of the database file (index + text + everything else), which is what the disk budget is really about.</summary>
    public long DatabaseFileBytes()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
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
            // FTS5 bm25() is NEGATIVE and more negative = more relevant. The magnitude is the relevance; flattening
            // it (as an earlier version did) threw the text match away and left only the recency boost deciding.
            var relevance = Math.Max(1e-9, -r.GetDouble(7));
            var score = relevance;
            var ageDays = Math.Max(0, (now - at).TotalDays);
            score *= 1.0 + 0.5 * Math.Exp(-ageDays / 14.0);            // recency: up to +50% within ~2 weeks
            if (workspace is { } w && ws == w) score *= 1.25;           // same workspace
            var tabId = r.GetString(0).Split(':')[0];
            hits.Add(new MemoryHit(new ResourceId(Guid.ParseExact(tabId, "N")), new Uri(r.GetString(1)), r.GetString(2), r.GetString(3), ws, at, r.GetString(6), score));
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

        // Preferred path: typed scores from Jev (numbers, auditable). Falls back to the chat-style rerank, then local order.
        if (brain is BrainRouter router && router.HasDecisionProvider)
        {
            var state = $"Query: {query}\n" + string.Join("\n", local.Select((h, i) => $"{i + 1}. {h.Title} — {h.Snippet}"));
            // The user pressed search and asked for reranking: an explicit action, not a background call.
            var answers = await router.JudgeAsync("rerank", state, Judgements.RerankQuestions(local.Select(h => h.Title).ToList()), DataClass.Public, IdentityContainer.Personal, ct, automatic: false);
            if (answers is not null && answers.Scores.Count > 0)
                return local.Select((h, i) => (h, s: answers.Scores.TryGetValue($"c{i}", out var sc) ? sc.Score * sc.Confidence + h.Score * 0.1 : h.Score * 0.1))
                            .OrderByDescending(x => x.s).Select(x => x.h).ToList();
        }

        var prompt = $"Query: {query}\n" + string.Join("\n", local.Select((h, i) => $"{i + 1}. {h.Title} — {h.Snippet}"));
        var d = await brain.DecideAsync(new DecisionRequest(BrainTask.RerankSearch, prompt, DataClass.Public, IdentityContainer.Personal, ExplicitUserAction: true), ct);
        if (d.WasDenied) return local;
        var order = d.Output.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim('.'), out var n) ? n - 1 : -1)
            .Where(i => i >= 0 && i < local.Count).Distinct().ToList();
        if (order.Count == 0) return local;
        return order.Select(i => local[i]).Concat(local.Where((_, i) => !order.Contains(i))).ToList();
    }

    /// <summary>The FTS index and page overhead roughly double what the raw text costs on disk; budget against that, not the text alone.</summary>
    private const double StorageOverhead = 2.0;

    private void EnforceBudget()
    {
        var (_, textBytes) = Stats();
        var bytes = (long)(textBytes * StorageOverhead);
        if (bytes <= _budgetBytes) return;
        // Drop oldest until under budget: bounded disk growth (Constitution / cost card).
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memory_docs WHERE rowid IN (
                SELECT rowid FROM (
                    SELECT rowid, SUM(bytes) OVER (ORDER BY captured_at DESC) AS running FROM memory_docs
                ) WHERE running > $budget)
            """;
        cmd.Parameters.AddWithValue("$budget", (long)(_budgetBytes / StorageOverhead));   // raw-text share of the disk budget
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
