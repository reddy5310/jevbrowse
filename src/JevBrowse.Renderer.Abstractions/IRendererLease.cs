using JevBrowse.Domain;

namespace JevBrowse.Renderer.Abstractions;

public enum RenderIntent { Foreground, Background, Prewarm }

/// <summary>ADR 0003: Suspend frees CPU only; Dispose is the only disposition that reclaims memory.</summary>
public enum ReleaseDisposition { Suspend, Dispose }

public sealed record NavigationInfo(Uri Url, string Title);

/// <summary>A temporary live renderer bound to one logical resource. Renderer-engine agnostic.</summary>
/// <summary>PNG bytes when the capture worked; otherwise null and a plain reason. Held in memory by the caller and never persisted.</summary>
public sealed record ScreenshotResult(byte[]? Png, string Detail)
{
    public bool Ok => Png is { Length: > 0 };
}

public interface IRendererLease
{
    ResourceId ResourceId { get; }
    /// <summary>
    /// Trust OS permission to write a screenshot of this page to disk. Default false: an adapter must never capture
    /// or persist pixels unless the kernel has cleared the current class and container (checked again on every
    /// navigation and class change).
    /// </summary>
    bool AllowThumbnails { get; set; }
    bool IsSuspended { get; }
    bool IsVisible { get; }
    void SetVisible(bool visible);
    void Navigate(Uri url);
    Task<bool> TrySuspendAsync();
    void Resume();

    /// <summary>
    /// Capture the semantic checkpoint of the live page. Must never read secret inputs, and must not throw: it
    /// reports what it managed to get so the caller can decide whether that is enough to dispose the renderer.
    /// </summary>
    Task<CaptureResult> CaptureCheckpointAsync(string thumbnailDir, CancellationToken ct);
    /// <summary>Apply scroll position etc. once the page the lease is loading has finished.</summary>
    void ApplyCheckpoint(Checkpoint checkpoint);
    /// <summary>Readable main text with boilerplate reduced (§8). Caller must clear Trust OS IndexContent first.</summary>
    Task<string?> ExtractReadableTextAsync(CancellationToken ct);

    /// <summary>
    /// A picture of the page as the engine renders it, returned in memory and nowhere else. This is NOT the thumbnail path: it never
    /// looks at <see cref="AllowThumbnails"/>, never writes a file, and must work for a page that is not shown, without changing
    /// visibility or taking focus. Whether a picture may be taken at all is decided by the caller (Trust OS and the agent grant); this
    /// only takes it. Must not throw except for cancellation.
    /// </summary>
    Task<ScreenshotResult> CaptureScreenshotAsync(CancellationToken ct);

    // ---- Agent Gateway surface (§12). Structured and narrow: no script evaluation, no raw DOM. ----
    Task<PageMap?> GetPageMapAsync(CancellationToken ct);
    Task<ActionResult> ClickAsync(string selector, CancellationToken ct);
    /// <summary>Resolve what the selector actually targets (text, label, type, form method). Null if it matches nothing.</summary>
    Task<ElementInfo?> DescribeAsync(string selector, CancellationToken ct);
    /// <summary>
    /// When set, every navigation the renderer attempts (clicks, redirects, scripts, not just Navigate requests) must
    /// be approved by this predicate or it is cancelled. The gateway sets it on agent-owned pages.
    /// </summary>
    Func<Uri, bool>? NavigationGuard { get; set; }
    /// <summary>Types into a non-secret field. Implementations must refuse password/credit-card inputs.</summary>
    Task<ActionResult> TypeAsync(string selector, string text, CancellationToken ct);

    event Action<NavigationInfo>? NavigationChanged;
    /// <summary>Fires when the page finishes loading; used for restore timing.</summary>
    event Action? Loaded;
    /// <summary>Live-page conditions that veto demotion: audible, download, dirty form.</summary>
    event Action<ProtectionFlags>? DetectedProtectionChanged;
    /// <summary>Live-page signals Trust OS classifies on: password field, payment field.</summary>
    event Action<PageSignals>? PageSignalsChanged;
}

/// <summary>Architecture §18. Bounded pool of live renderers; the kernel decides who gets one.</summary>
public interface IRendererLeaseManager
{
    int MaxLive { get; set; }
    IReadOnlyCollection<ResourceId> LiveResources { get; }
    bool TryGet(ResourceId id, out IRendererLease lease);
    /// <summary>
    /// The container selects the cookie/storage silo the renderer runs in (§10.1). <paramref name="isolationKey"/>
    /// is the workspace: ephemeral containers get one profile per workspace so two disposable sessions (e.g. two
    /// agents) never share cookies.
    /// </summary>
    Task<IRendererLease> AcquireAsync(ResourceId id, Uri initialUrl, RenderIntent intent, IdentityContainer container, ContextId isolationKey, CancellationToken ct);

    /// <summary>
    /// Register a navigation policy for a resource BEFORE it has a renderer. The manager applies it to the lease at
    /// creation, so it governs the very first navigation and any redirect inside it; attaching a guard to a lease
    /// after acquisition leaves that first load unchecked. Applies to every later lease for the same resource
    /// (restores) until cleared with null.
    /// </summary>
    void SetNavigationPolicy(ResourceId id, Func<Uri, bool>? guard);
    Task ReleaseAsync(ResourceId id, ReleaseDisposition disposition, CancellationToken ct);
}
