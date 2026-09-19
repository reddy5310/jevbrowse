using JevBrowse.Domain;

namespace JevBrowse.Renderer.Abstractions;

public enum RenderIntent { Foreground, Background, Prewarm }

/// <summary>ADR 0003: Suspend frees CPU only; Dispose is the only disposition that reclaims memory.</summary>
public enum ReleaseDisposition { Suspend, Dispose }

public sealed record NavigationInfo(Uri Url, string Title);

/// <summary>A temporary live renderer bound to one logical resource. Renderer-engine agnostic.</summary>
public interface IRendererLease
{
    ResourceId ResourceId { get; }
    bool IsSuspended { get; }
    bool IsVisible { get; }
    void SetVisible(bool visible);
    void Navigate(Uri url);
    Task<bool> TrySuspendAsync();
    void Resume();

    /// <summary>Capture the semantic checkpoint of the live page. Must never read secret inputs.</summary>
    Task<Checkpoint> CaptureCheckpointAsync(string thumbnailDir, CancellationToken ct);
    /// <summary>Apply scroll position etc. once the page the lease is loading has finished.</summary>
    void ApplyCheckpoint(Checkpoint checkpoint);

    event Action<NavigationInfo>? NavigationChanged;
    /// <summary>Fires when the page finishes loading; used for restore timing.</summary>
    event Action? Loaded;
    /// <summary>Live-page conditions that veto demotion: audible, download, dirty form.</summary>
    event Action<ProtectionFlags>? DetectedProtectionChanged;
}

/// <summary>Architecture §18. Bounded pool of live renderers; the kernel decides who gets one.</summary>
public interface IRendererLeaseManager
{
    int MaxLive { get; set; }
    IReadOnlyCollection<ResourceId> LiveResources { get; }
    bool TryGet(ResourceId id, out IRendererLease lease);
    Task<IRendererLease> AcquireAsync(ResourceId id, Uri initialUrl, RenderIntent intent, CancellationToken ct);
    Task ReleaseAsync(ResourceId id, ReleaseDisposition disposition, CancellationToken ct);
}
