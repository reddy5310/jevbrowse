namespace JevBrowse.VirtualTabs;

public enum SearchKind { Tab, Workspace, Command, Page }

/// <summary>
/// One thing a person can jump to. <paramref name="Key"/> is opaque to the ranking; the caller uses it to know what to run.
/// A <see cref="Page"/> hit from Browser Memory arrives already matched by its own full-text index (which understands word
/// forms this ranking does not), so it carries <paramref name="PreMatched"/> and is ordered by that score, never dropped here.
/// </summary>
public sealed record SearchEntry(SearchKind Kind, string Title, string Detail, string Key, double? PreMatched = null);

public sealed record SearchResult(SearchEntry Entry, double Score);

/// <summary>
/// Unified search: one box over open tabs, workspaces, commands and pages read before. Pure and local: it ranks what it is
/// given and decides nothing about what may be searched. That decision (private sessions stay out, Browser Memory only
/// holds what Trust OS allowed to be indexed) is made where the candidates are gathered, so it is made once and visibly.
/// </summary>
public static class UnifiedSearch
{
    // Per group, so twenty matching commands cannot push the only matching tab off the list.
    private static int Cap(SearchKind k) => k switch { SearchKind.Tab => 6, SearchKind.Workspace => 4, SearchKind.Command => 8, _ => 6 };

    /// <summary>Every word must match somewhere (title first, then detail). Best matches first; ties keep tabs ahead of workspaces, commands, then pages.</summary>
    public static IReadOnlyList<SearchResult> Rank(string? query, IEnumerable<SearchEntry> candidates)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = new List<SearchResult>();
        foreach (var c in candidates)
        {
            if (c.PreMatched is { } pre) { scored.Add(new SearchResult(c, pre)); continue; }
            if (words.Length == 0) { scored.Add(new SearchResult(c, 0)); continue; }
            double total = 0; var ok = true;
            foreach (var w in words)
            {
                var s = WordScore(c, w);
                if (s <= 0) { ok = false; break; }
                total += s;
            }
            if (ok) scored.Add(new SearchResult(c, total));
        }
        var ordered = words.Length == 0
            ? scored   // nothing typed: keep the order the caller gave (most relevant first is the caller's call)
            : scored.OrderByDescending(r => r.Score).ThenBy(r => (int)r.Entry.Kind).ThenBy(r => r.Entry.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var taken = new Dictionary<SearchKind, int>();
        var result = new List<SearchResult>();
        foreach (var r in ordered.OrderBy(r => words.Length == 0 ? (int)r.Entry.Kind : 0))   // grouped when browsing, by score when searching
        {
            taken.TryGetValue(r.Entry.Kind, out var n);
            if (n >= Cap(r.Entry.Kind)) continue;
            taken[r.Entry.Kind] = n + 1;
            result.Add(r);
        }
        return result;
    }

    private static double WordScore(SearchEntry e, string w)
    {
        var title = e.Title;
        if (title.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return 100;
        // A word inside the title that starts with it ("Pin" in "Pin: never hibernate…" or "wiki" in "Web wiki").
        foreach (var part in title.Split([' ', ':', '-', '/', '.', '(', ')', '…'], StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return 70;
        if (title.Contains(w, StringComparison.OrdinalIgnoreCase)) return 40;
        if (e.Detail.Contains(w, StringComparison.OrdinalIgnoreCase)) return 20;
        return 0;
    }
}
