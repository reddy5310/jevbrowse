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
    event Action<NavigationInfo>? NavigationChanged;
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
