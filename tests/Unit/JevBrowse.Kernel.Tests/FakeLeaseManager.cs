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
    /// <summary>Set to make the next capture throw (simulates a renderer crash mid-checkpoint).</summary>
    public bool FailNextCapture { get; set; }
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
        var l = new FakeLease(id, url, this);
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

public sealed class FakeLease(ResourceId id, Uri url, FakeLeaseManager owner) : IRendererLease
{
    public ResourceId ResourceId => id;
    public Uri Url { get; private set; } = url;
    public bool IsSuspended { get; set; }
    public bool IsVisible { get; private set; }
    public double ScrollY { get; set; }
    public Checkpoint? Applied { get; private set; }
    public void SetVisible(bool v) => IsVisible = v;
    public void Navigate(Uri u) => Url = u;
    public Task<bool> TrySuspendAsync() { IsSuspended = true; return Task.FromResult(true); }
    public void Resume() => IsSuspended = false;

    public Task<Checkpoint> CaptureCheckpointAsync(string dir, CancellationToken ct)
    {
        if (owner.FailNextCapture) { owner.FailNextCapture = false; throw new InvalidOperationException("renderer gone"); }
        return Task.FromResult(new Checkpoint(id, Url, "t", 0, ScrollY, null, null, DateTimeOffset.UnixEpoch));
    }

    public void ApplyCheckpoint(Checkpoint cp) { Applied = cp; ScrollY = cp.ScrollY; }

    public event Action<NavigationInfo>? NavigationChanged;
    public event Action? Loaded;
    public event Action<ProtectionFlags>? DetectedProtectionChanged;
    public void RaiseNavigation(Uri u, string t) { Url = u; NavigationChanged?.Invoke(new(u, t)); }
    public void RaiseLoaded() => Loaded?.Invoke();
    public void RaiseDetected(ProtectionFlags f) => DetectedProtectionChanged?.Invoke(f);
}
