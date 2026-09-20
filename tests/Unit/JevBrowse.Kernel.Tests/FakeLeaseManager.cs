using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;

namespace JevBrowse.Kernel.Tests;

/// <summary>In-memory renderer pool. Records every acquire/release so tests can assert renderer accounting.</summary>
public sealed class FakeLeaseManager : IRendererLeaseManager
{
    private readonly Dictionary<ResourceId, FakeLease> _live = [];
    public int MaxLive { get; set; } = 5;
    public int Acquires { get; private set; }
    /// <summary>Every acquire in the order it began, with the intent it was asked for.</summary>
    public List<(ResourceId Id, RenderIntent Intent)> AcquireLog { get; } = [];
    /// <summary>Awaited inside AcquireAsync: return a task you complete later to simulate a slow restore.</summary>
    public Func<ResourceId, RenderIntent, Task>? AcquireGate { get; set; }
    public int Disposes { get; private set; }
    public int Suspends { get; private set; }
    /// <summary>Set to make the next capture throw (simulates a renderer crash mid-checkpoint).</summary>
    public bool FailNextCapture { get; set; }
    public bool FailNextRelease { get; set; }
    public IReadOnlyCollection<ResourceId> LiveResources => _live.Keys;

    private readonly Dictionary<ResourceId, Func<Uri, bool>> _navPolicies = [];

    public void SetNavigationPolicy(ResourceId id, Func<Uri, bool>? guard)
    {
        if (guard is null) _navPolicies.Remove(id); else _navPolicies[id] = guard;
        if (_live.TryGetValue(id, out var live)) live.NavigationGuard = guard;
    }

    /// <summary>What the renderer would do with the very first navigation, using only the pre-registered policy.</summary>
    public bool WouldAllowInitialNavigation(ResourceId id, Uri url) => !_navPolicies.TryGetValue(id, out var p) || p(url);

    public bool TryGet(ResourceId id, out IRendererLease lease)
    {
        var ok = _live.TryGetValue(id, out var l);
        lease = l!;
        return ok;
    }

    public readonly Dictionary<ResourceId, IdentityContainer> Containers = [];
    public readonly Dictionary<ResourceId, ContextId> IsolationKeys = [];
    /// <summary>Simulated acquisition latency so tests can interleave concurrent lifecycle calls.</summary>
    public TimeSpan AcquireDelay { get; set; }
    private int _inFlight;
    public int MaxConcurrentAcquires { get; private set; }

    public async Task<IRendererLease> AcquireAsync(ResourceId id, Uri url, RenderIntent intent, IdentityContainer container, ContextId isolationKey, CancellationToken ct)
    {
        Acquires++;
        AcquireLog.Add((id, intent));
        // A per-tab hold, so a test can make ONE restore slow and decide exactly when it finishes.
        if (AcquireGate is not null) await AcquireGate(id, intent);
        MaxConcurrentAcquires = Math.Max(MaxConcurrentAcquires, ++_inFlight);
        try { if (AcquireDelay > TimeSpan.Zero) await Task.Delay(AcquireDelay, ct); }
        finally { _inFlight--; }
        Containers[id] = container;
        IsolationKeys[id] = isolationKey;
        var l = new FakeLease(id, url, this);
        if (_navPolicies.TryGetValue(id, out var policy)) l.NavigationGuard = policy;   // before the first navigation
        _live[id] = l;
        return l;
    }

    public Task ReleaseAsync(ResourceId id, ReleaseDisposition d, CancellationToken ct)
    {
        if (FailNextRelease) { FailNextRelease = false; throw new IOException("Simulated renderer release failure"); }
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
    public bool AllowThumbnails { get; set; }
    /// <summary>Directory the fake writes to when it "captures" a deactivation thumbnail (mirrors WebView2Lease.SetVisible).</summary>
    public string? ThumbnailDir { get; set; }
    public void SetVisible(bool v)
    {
        // Real adapter: leaving the foreground captures a screenshot, but only if the kernel allowed it.
        if (!v && IsVisible && AllowThumbnails && ThumbnailDir is not null && ThumbnailToWrite is not null)
        {
            Directory.CreateDirectory(ThumbnailDir);
            File.WriteAllBytes(Path.Combine(ThumbnailDir, ThumbnailToWrite), [1, 2, 3]);
        }
        IsVisible = v;
    }
    public void Navigate(Uri u) => Url = u;
    public Task<bool> TrySuspendAsync() { IsSuspended = true; return Task.FromResult(true); }
    public void Resume() => IsSuspended = false;

    public string? ThumbnailToWrite { get; set; }
    /// <summary>Forces the next capture to report this outcome instead of succeeding.</summary>
    public CaptureOutcome? NextCaptureOutcome { get; set; }

    public Task<CaptureResult> CaptureCheckpointAsync(string dir, CancellationToken ct)
    {
        if (owner.FailNextCapture) { owner.FailNextCapture = false; return Task.FromResult(new CaptureResult(null, CaptureOutcome.Failed, "renderer gone")); }
        if (NextCaptureOutcome is { } forced and not CaptureOutcome.Partial)
        {
            NextCaptureOutcome = null;
            return Task.FromResult(new CaptureResult(null, forced, forced.ToString()));
        }
        string? thumb = null;
        if (AllowThumbnails && ThumbnailToWrite is not null) { thumb = Path.Combine(dir, ThumbnailToWrite); Directory.CreateDirectory(dir); File.WriteAllBytes(thumb, [1, 2, 3]); }
        var partial = NextCaptureOutcome == CaptureOutcome.Partial;
        NextCaptureOutcome = null;
        var cp = new Checkpoint(id, Url, "t", 0, partial ? 0 : ScrollY, null, thumb, DateTimeOffset.UnixEpoch);
        // A partial capture here is the scroll-extraction failure: the address survived, the position did not.
        return Task.FromResult(partial
            ? new CaptureResult(cp, CaptureOutcome.Partial, "position unavailable", PreservedParts.Address | PreservedParts.Preview)
            : new CaptureResult(cp, CaptureOutcome.Captured, "address, position and preview", PreservedParts.Address | PreservedParts.Position | PreservedParts.Preview));
    }

    public void ApplyCheckpoint(Checkpoint cp) { Applied = cp; ScrollY = cp.ScrollY; }
    public string? ReadableText { get; set; }
    /// <summary>Simulates a slow in-page extraction; <see cref="DuringExtract"/> runs while the caller is awaiting it.</summary>
    public TimeSpan ExtractDelay { get; set; }
    public Action? DuringExtract { get; set; }
    public async Task<string?> ExtractReadableTextAsync(CancellationToken ct)
    {
        if (ExtractDelay > TimeSpan.Zero) { DuringExtract?.Invoke(); await Task.Delay(ExtractDelay, ct); }
        return ReadableText;
    }

    public Func<Uri, bool>? NavigationGuard { get; set; }
    /// <summary>Simulates the page/redirect/click attempting to navigate: returns whether the renderer would allow it.</summary>
    public bool TryNavigate(Uri to) => NavigationGuard?.Invoke(to) ?? true;
    public Dictionary<string, ElementInfo> Elements { get; } = [];
    public Task<ElementInfo?> DescribeAsync(string selector, CancellationToken ct) =>
        Task.FromResult(Elements.TryGetValue(selector, out var e) ? e : null);

    public PageMap? Map { get; set; }
    public List<string> Clicked { get; } = [];
    public List<(string Selector, string Text)> Typed { get; } = [];
    public Task<PageMap?> GetPageMapAsync(CancellationToken ct) => Task.FromResult<PageMap?>(Map ?? new PageMap(Url, "t", [], [], [], ""));
    public Task<ActionResult> ClickAsync(string selector, CancellationToken ct) { Clicked.Add(selector); return Task.FromResult(new ActionResult(true, "clicked")); }
    public Task<ActionResult> TypeAsync(string selector, string text, CancellationToken ct)
    {
        if (selector.Contains("password", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(new ActionResult(false, "refused: secret field"));
        Typed.Add((selector, text));
        return Task.FromResult(new ActionResult(true, "typed"));
    }

    public event Action<NavigationInfo>? NavigationChanged;
    public event Action? Loaded;
    public event Action<ProtectionFlags>? DetectedProtectionChanged;
    public event Action<PageSignals>? PageSignalsChanged;
    public void RaiseNavigation(Uri u, string t) { Url = u; NavigationChanged?.Invoke(new(u, t)); }
    public void RaiseLoaded() => Loaded?.Invoke();
    public void RaiseDetected(ProtectionFlags f) => DetectedProtectionChanged?.Invoke(f);
    public void RaiseSignals(PageSignals s) => PageSignalsChanged?.Invoke(s);
}
