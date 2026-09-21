using JevBrowse.Domain;

namespace JevBrowse.VirtualTabs;

/// <summary>
/// Back and Forward across sleep. A live renderer keeps its own history; a renderer that is recreated on wake starts with ONE entry. This keeps what came before
/// and after the page the tab woke on, and lets Back and Forward walk it by REPLACING the current entry (so the live history never grows a second copy of a page).
///
/// Invariant: the true order is <c>Before</c>, then the live first entry, then <c>After</c>, then the rest of the live entries. Back at the live start takes the
/// last of Before; Forward at the live start takes the first of After. A new (non-history) navigation ends Forward: <see cref="ClearForward"/>.
/// Pure, so every ordering can be tested without an engine.
/// </summary>
public sealed class SleepHistory
{
    public const int MaxEntries = 50;

    private readonly List<HistoryEntry> _before = [];
    private readonly List<HistoryEntry> _after = [];

    public IReadOnlyList<HistoryEntry> Before => _before;
    public IReadOnlyList<HistoryEntry> After => _after;

    /// <summary>Starts from what was saved: everything before the page the tab woke on, and everything after it.</summary>
    public void Seed(NavHistory saved)
    {
        _before.Clear(); _after.Clear();
        var i = Math.Clamp(saved.Index, 0, Math.Max(0, saved.Entries.Count - 1));
        _before.AddRange(saved.Entries.Take(i));
        _after.AddRange(saved.Entries.Skip(i + 1));
    }

    public bool CanGoBackFromLiveStart => _before.Count > 0;
    public bool CanGoForwardFromLiveStart => _after.Count > 0;

    /// <summary>Back past the live start: returns the entry to replace the current page with, and remembers the current page as the next Forward.</summary>
    public HistoryEntry? TakeBack(HistoryEntry current)
    {
        if (_before.Count == 0) return null;
        var prev = _before[^1]; _before.RemoveAt(_before.Count - 1);
        _after.Insert(0, current);
        return prev;
    }

    public HistoryEntry? TakeForward(HistoryEntry current)
    {
        if (_after.Count == 0) return null;
        var next = _after[0]; _after.RemoveAt(0);
        _before.Add(current);
        return next;
    }

    /// <summary>A real navigation from here: what used to be ahead is gone, as in any browser. What is behind stays.</summary>
    public void ClearForward() => _after.Clear();

    /// <summary>
    /// The whole history, flattened for saving: <paramref name="live"/> is the renderer's own entries with <paramref name="liveIndex"/> the current one.
    /// Only web addresses are kept (a local page cannot be reloaded by address), and the oldest are dropped past <see cref="MaxEntries"/>.
    /// </summary>
    public NavHistory Flatten(IReadOnlyList<HistoryEntry> live, int liveIndex)
    {
        var all = new List<HistoryEntry>(_before);
        var currentAt = -1;
        for (var i = 0; i < live.Count; i++)
        {
            all.Add(live[i]);
            if (i == liveIndex) currentAt = all.Count - 1;
            if (i == 0) all.AddRange(_after);   // After sits between the live first entry and the rest
        }
        var web = new List<HistoryEntry>(); var index = 0;
        for (var i = 0; i < all.Count; i++)
        {
            if (!IsWeb(all[i].Url)) continue;
            if (i == currentAt) index = web.Count;
            web.Add(all[i]);
        }
        if (web.Count > MaxEntries) { var drop = web.Count - MaxEntries; web = web.Skip(drop).ToList(); index = Math.Max(0, index - drop); }
        return new NavHistory(web, Math.Clamp(index, 0, Math.Max(0, web.Count - 1)));
    }

    private static bool IsWeb(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
