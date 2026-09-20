namespace JevBrowse.Domain;

public readonly record struct ContextId(Guid Value)
{
    public static ContextId New() => new(Guid.NewGuid());
    public static readonly ContextId Default = new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Context OS workspace (§7): a scheduling domain, not a folder. Priority feeds the Resource OS; inactive
/// workspaces are virtualized sooner. Identity/container binding arrives with Trust OS (Phase 6).
/// </summary>
public sealed class Workspace
{
    public Workspace(ContextId id, string name) { Id = id; Name = name; }
    public ContextId Id { get; }
    public string Name { get; set; }
    /// <summary>Scheduling weight when this workspace is NOT active. 1.0 = same as active; 0.3 = default background.</summary>
    public double BackgroundPriority { get; set; } = 0.3;
    public bool NotificationsMuted { get; set; }
    /// <summary>
    /// Trust OS binding (§10.1): which cookie/storage/permission silo this workspace's tabs render in.
    /// Immutable: a live renderer cannot change profile, so relabelling would make the label lie. Crossing an
    /// identity boundary means creating a new tab (see TabKernel.MoveToWorkspaceAsync).
    /// </summary>
    public IdentityContainer Container { get; init; } = IdentityContainer.Personal;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Time Travel checkpoint (§7.1): what the user was doing, not renderer state. Small enough to record every few
/// minutes; restoring one recreates resources as VIRTUAL and loads only the active one.
/// </summary>
public sealed record ContextCheckpoint(
    Guid Id,
    ContextId WorkspaceId,
    string WorkspaceName,
    DateTimeOffset At,
    ResourceId? ActiveResource,
    IReadOnlyList<ContextCheckpointEntry> Resources,
    int LiveCount);

public sealed record ContextCheckpointEntry(ResourceId Id, Uri Url, string Title, bool WasLive);
