using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Memory;

/// <summary>
/// Wires the kernel to Browser Memory: when a page finishes loading, extract readable text and index it,
/// but only if Trust OS allows IndexContent for that tab's class and container. Sits outside the kernel so the
/// kernel stays free of search concerns (Table A.4).
/// </summary>
public sealed class MemoryIndexer
{
    private readonly TabKernel _kernel;
    private readonly IRendererLeaseManager _leases;
    private readonly BrowserMemory _memory;

    public int Indexed { get; private set; }
    public int Skipped { get; private set; }
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
        if (e.Kind == "closed") { _memory.Forget(e.Id); return; }
        if (e.Kind != "restored") return; // "restored" = page load completed on a fresh lease
        try { await IndexAsync(e.Id); }
        catch (Exception) { /* indexing is best-effort and never affects browsing */ }
    }

    public async Task<bool> IndexAsync(ResourceId id, CancellationToken ct = default)
    {
        var tab = _kernel.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || !_leases.TryGet(id, out var lease)) return false;
        var may = _kernel.May(tab, DataOperation.IndexContent);
        if (!may.Allowed) { Skipped++; Decided?.Invoke(id, "skip: " + may.Reason); return false; }
        var text = await lease.ExtractReadableTextAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) { Skipped++; Decided?.Invoke(id, "skip: no readable text"); return false; }
        _memory.Index(id, tab.Url, tab.Title, tab.WorkspaceId, text);
        Indexed++;
        Decided?.Invoke(id, $"indexed {text.Length} chars ({_kernel.ClassOf(tab)})");
        return true;
    }
}
