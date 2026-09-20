using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Memory;

/// <summary>
/// Wires the kernel to Browser Memory: after EVERY completed navigation, extract readable text and index it, but only
/// if Trust OS allows IndexContent for that page's class and container, judged both before and (again) after the
/// asynchronous extraction, so a page that turned out to have a password field, or that the tab has already
/// navigated away from, is never committed. Sits outside the kernel so the kernel stays free of search concerns.
///
/// Retention: closing a tab does not erase what was indexed (that is the point of a memory); tightening a page's
/// class does. Users clear the index explicitly.
/// </summary>
public sealed class MemoryIndexer
{
    private readonly TabKernel _kernel;
    private readonly IRendererLeaseManager _leases;
    private readonly BrowserMemory _memory;
    private readonly Dictionary<ResourceId, int> _version = [];

    public int Indexed { get; private set; }
    public int Skipped { get; private set; }
    /// <summary>
    /// Product modes that hide Memory (Simple, Private) must also STOP indexing: a feature the user cannot see or
    /// clear must not keep accumulating data. Tightening/forgetting keeps working either way.
    /// </summary>
    public bool Enabled { get; set; } = true;
    public event Action<ResourceId, string>? Decided;

    public MemoryIndexer(TabKernel kernel, IRendererLeaseManager leases, BrowserMemory memory)
    {
        _kernel = kernel;
        _leases = leases;
        _memory = memory;
        _kernel.Changed += OnKernelChanged;
    }

    private async void OnKernelChanged(KernelEvent e)
    {
        try
        {
            if (e.Kind == "policy-tightened")
            {
                // The page's class no longer allows content in the index: forget what we stored for it.
                if (_kernel.Tabs.FirstOrDefault(t => t.Id == e.Id) is { } tab) _memory.Forget(e.Id, tab.Url);
                return;
            }
            if (e.Kind == "loaded") await IndexAsync(e.Id);
        }
        catch (Exception) { /* indexing is best-effort and never affects browsing */ }
    }

    public async Task<bool> IndexAsync(ResourceId id, CancellationToken ct = default)
    {
        if (!Enabled) return false;
        var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !_leases.TryGet(id, out var lease)) return false;
        var may = _kernel.May(tab, DataOperation.IndexContent);
        if (!may.Allowed) { Skipped++; Decided?.Invoke(id, "skip: " + may.Reason); return false; }

        var version = _version[id] = _version.GetValueOrDefault(id) + 1;
        var urlAtStart = tab.Url;
        var text = await lease.ExtractReadableTextAsync(ct);

        // Everything can have changed while we awaited the page: a newer load, a navigation, a password field appearing.
        if (_version.GetValueOrDefault(id) != version || tab.Url != urlAtStart || _kernel.Tabs.All(t => t.Id != id))
        { Skipped++; Decided?.Invoke(id, "skip: page changed during extraction"); return false; }
        var may2 = _kernel.May(tab, DataOperation.IndexContent);
        if (!may2.Allowed) { Skipped++; Decided?.Invoke(id, "skip (after extraction): " + may2.Reason); return false; }
        if (string.IsNullOrWhiteSpace(text)) { Skipped++; Decided?.Invoke(id, "skip: no readable text"); return false; }

        _memory.Index(id, tab.Url, tab.Title, tab.WorkspaceId, text);
        Indexed++;
        Decided?.Invoke(id, $"indexed {text.Length} chars ({_kernel.ClassOf(tab)})");
        return true;
    }
}
