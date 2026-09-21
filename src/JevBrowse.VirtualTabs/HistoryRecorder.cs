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
    private readonly SiteZoomRepository? _zoom;

    public HistoryRecorder(TabKernel kernel, HistoryRepository history, Func<DateTimeOffset>? clock = null, SiteZoomRepository? siteZoom = null)
    {
        _kernel = kernel; _history = history; _clock = clock ?? (() => DateTimeOffset.UtcNow); _zoom = siteZoom;
        _kernel.Changed += e =>
        {
            if (e.Kind == "loaded") Record(e.Id);
            else if (e.Kind is "signals" or "policy-tightened") ForgetIfNowSensitive(e.Id);   // e.g. a password or card field appeared on a page already in the list
        };
    }

    /// <summary>
    /// A page turned Sensitive AFTER it was recorded (a payment field showed up, or an advisory raised its class): what was kept about its site is removed. Only ordinary
    /// workspaces count: a Private tab is always at least Sensitive and must not erase what an ordinary tab on the same site legitimately left.
    /// </summary>
    private void ForgetIfNowSensitive(ResourceId tabId)
    {
        var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tab is null || tab.Url.Scheme is not ("http" or "https") || _kernel.ContainerOf(tab).IsEphemeral()) return;
        if (_kernel.ClassOf(tab) < DataClass.Sensitive) return;
        try { _history.RemoveHost(tab.Url.Host); _zoom?.Set(tab.Url.Host, ZoomLevels.Default); } catch (Exception) { }
    }

    /// <summary>After a site decision changes (Make Sensitive): removes every stored page and zoom whose host may no longer be kept. Returns how many history rows went.</summary>
    public int Sweep()
    {
        var removed = 0;
        try
        {
            foreach (var host in _history.Hosts())
                if (Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var u) && !_kernel.MayKeepAddress(u)) removed += _history.RemoveHost(host);
            if (_zoom is not null)
                foreach (var host in _zoom.Hosts())
                    if (Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out var u) && !_kernel.MayKeepAddress(u)) _zoom.Set(host, ZoomLevels.Default);
        }
        catch (Exception) { }
        return removed;
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
