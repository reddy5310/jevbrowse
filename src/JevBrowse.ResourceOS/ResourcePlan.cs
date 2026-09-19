using JevBrowse.Domain;

namespace JevBrowse.ResourceOS;

/// <summary>Scheduler's view of one logical resource. Built by the kernel; contains no renderer objects.</summary>
public sealed record ResourceRuntime(
    ResourceId Id,
    ResourceState State,
    ProtectionFlags Protection,
    bool IsActive,
    DateTimeOffset LastActive,
    DateTimeOffset LastStateChange,
    int VisitCount,
    double WorkspacePriority = 1.0);

/// <summary>
/// Decision record, Architecture §11.1. Every automated action carries the inputs that produced it so the UI
/// can show "why" and offer an override. Reasons are strings on purpose: they are for humans.
/// </summary>
public sealed record ScheduledAction(ResourceId Id, string Action, IReadOnlyDictionary<string, string> Reasons)
{
    public string Explain() => $"{Action} {Id}\n" + string.Join("\n", Reasons.Select(kv => $"  {kv.Key}: {kv.Value}"));
}

public sealed record ResourcePlan(
    PressureBand Band,
    int TargetLiveRenderers,
    int LiveNow,
    IReadOnlyList<ScheduledAction> Virtualize,
    IReadOnlyList<ScheduledAction> Suspend,
    ResourceId? PrewarmCandidate,
    IReadOnlyList<ScheduledAction> Skipped)
{
    public bool IsNoOp => Virtualize.Count == 0 && Suspend.Count == 0 && PrewarmCandidate is null;
}

public interface IResourceScheduler
{
    SchedulerPolicy Policy { get; set; }
    ResourcePlan Evaluate(SystemPressure pressure, IReadOnlyList<ResourceRuntime> resources, DateTimeOffset now);
}
