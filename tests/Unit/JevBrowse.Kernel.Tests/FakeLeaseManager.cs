using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;

namespace JevBrowse.Kernel.Tests;

/// <summary>In-memory renderer pool. Records every acquire/release so tests can assert renderer accounting.</summary>
public sealed class FakeLeaseManager : IRendererLeaseManager
{
    private readonly Dictionary<ResourceId, FakeLease> _live = [];
    public int MaxLive { get; set; } = 5;
    public int Acquires { get; private set; }
    public int Disposes { get; private set; }
    public int Suspends { get; private set; }
    public IReadOnlyCollection<ResourceId> LiveResources => _live.Keys;

    public bool TryGet(ResourceId id, out IRendererLease lease)
    {
        var ok = _live.TryGetValue(id, out var l);
        lease = l!;
        return ok;
    }

    public Task<IRendererLease> AcquireAsync(ResourceId id, Uri url, RenderIntent intent, CancellationToken ct)
    {
        Acquires++;
        var l = new FakeLease(id);
        _live[id] = l;
        return Task.FromResult<IRendererLease>(l);
    }

    public Task ReleaseAsync(ResourceId id, ReleaseDisposition d, CancellationToken ct)
    {
        if (d == ReleaseDisposition.Dispose) { _live.Remove(id); Disposes++; }
        else { _live[id].IsSuspended = true; Suspends++; }
        return Task.CompletedTask;
    }

    public FakeLease this[ResourceId id] => _live[id];
}

public sealed class FakeLease(ResourceId id) : IRendererLease
{
    public ResourceId ResourceId => id;
    public bool IsSuspended { get; set; }
    public bool IsVisible { get; private set; }
    public Uri? LastNavigated { get; private set; }
    public void SetVisible(bool v) => IsVisible = v;
    public void Navigate(Uri url) => LastNavigated = url;
    public Task<bool> TrySuspendAsync() { IsSuspended = true; return Task.FromResult(true); }
    public void Resume() => IsSuspended = false;
    public event Action<NavigationInfo>? NavigationChanged;
    public void RaiseNavigation(Uri u, string t) => NavigationChanged?.Invoke(new(u, t));
}
