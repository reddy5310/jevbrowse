using JevBrowse.Domain;
using JevBrowse.Storage;

namespace JevBrowse.VirtualTabs;

/// <summary>
/// Writes a page to the history list when it finishes loading, but not from Private or agent sessions and not from a page classed Sensitive or Secret (a bank,
/// a clinic): the same rule the saved Back/Forward history uses, so a visited address is kept only where a visited address may be kept.
/// </summary>
public sealed class HistoryRecorder
{
    private readonly TabKernel _kernel;
    private readonly HistoryRepository _history;
    private readonly Func<DateTimeOffset> _clock;

    public HistoryRecorder(TabKernel kernel, HistoryRepository history, Func<DateTimeOffset>? clock = null)
    {
        _kernel = kernel; _history = history; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _kernel.Changed += e => { if (e.Kind == "loaded") Record(e.Id); };
    }

    /// <summary>Returns whether a row was written.</summary>
    public bool Record(ResourceId tabId)
    {
        var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null || tab.Url.Scheme is not ("http" or "https")) return false;
        if (_kernel.ClassOf(tab) >= DataClass.Sensitive) return false;   // Sensitive, Secret and Ephemeral (Private, agent) all sort at or above Sensitive
        try { _history.Record(tab.Url.AbsoluteUri, tab.Title, _clock()); return true; }
        catch (Exception) { return false; }   // a history list is never worth interrupting a page for
    }
}
