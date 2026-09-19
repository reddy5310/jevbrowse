using JevBrowse.Domain;

namespace JevBrowse.ResourceOS;

/// <summary>
/// Deterministic scheduler: layer 2 of the decision priority (§11). No AI here, ever. It produces a plan the kernel
/// executes; the kernel re-checks protection at execution time, so this class is advisory-with-teeth, not authority.
/// </summary>
public sealed class DefaultScheduler : IResourceScheduler
{
    private readonly PressureBandTracker _bands = new();
    private readonly Queue<DateTimeOffset> _recentHibernations = new();
    private readonly Dictionary<ResourceId, DateTimeOffset> _lastAutomatedTransition = [];

    public DefaultScheduler(SchedulerPolicy? policy = null) => Policy = policy ?? SchedulerPolicy.For(MemoryMode.Balanced);

    public SchedulerPolicy Policy { get; set; }
    public PressureBand CurrentBand => _bands.Band;

    public ResourcePlan Evaluate(SystemPressure pressure, IReadOnlyList<ResourceRuntime> resources, DateTimeOffset now)
    {
        var band = _bands.Update(pressure.AvailableFraction);
        if (pressure.OnBattery && Policy.Mode == MemoryMode.Balanced) band = (PressureBand)Math.Max((int)band, (int)PressureBand.Yellow);

        var target = Policy.TargetLive(band);
        var live = resources.Where(r => r.State.HasLiveRenderer()).ToList();
        var virtualize = new List<ScheduledAction>();
        var suspend = new List<ScheduledAction>();
        var skipped = new List<ScheduledAction>();

        while (_recentHibernations.Count > 0 && now - _recentHibernations.Peek() > Policy.Window) _recentHibernations.Dequeue();
        var budgetLeft = band == PressureBand.Red ? int.MaxValue : Policy.MaxHibernationsPerWindow - _recentHibernations.Count;

        // Candidates ordered by revisit score ascending: the least likely to be revisited goes first.
        var ranked = live
            .Where(r => !r.IsActive)
            .Select(r => (r, score: RevisitScore(r, now)))
            .OrderBy(x => x.score)
            .ToList();

        int over = live.Count - target;
        foreach (var (r, score) in ranked)
        {
            var idle = now - r.LastActive;
            var reasons = new Dictionary<string, string>
            {
                ["inactive_minutes"] = ((int)idle.TotalMinutes).ToString(),
                ["system_pressure"] = band.ToString().ToUpperInvariant(),
                ["live_renderers"] = $"{live.Count}/{target}",
                ["audible"] = r.Protection.HasFlag(ProtectionFlags.Audible).ToString().ToLower(),
                ["download"] = r.Protection.HasFlag(ProtectionFlags.DownloadActive).ToString().ToLower(),
                ["dirty_form"] = r.Protection.HasFlag(ProtectionFlags.DirtyForm).ToString().ToLower(),
                ["protected"] = (r.Protection != ProtectionFlags.None).ToString().ToLower(),
                ["workspace_priority"] = r.WorkspacePriority.ToString("0.0"),
                ["revisit_score"] = score.ToString("0.00"),
                ["jev_consulted"] = "no",
            };

            string? veto = null;
            if (r.Protection != ProtectionFlags.None) veto = "protected";
            else if (now - r.LastStateChange < Policy.MinResidency) veto = "min_residency";
            else if (_lastAutomatedTransition.TryGetValue(r.Id, out var last) && now - last < Policy.Cooldown) veto = "cooldown";

            bool required = over > 0;                       // pool above budget: must shrink
            bool opportunistic = idle >= Policy.IdleBeforeVirtualize;
            if (!required && !opportunistic) continue;
            reasons["trigger"] = required ? "over_budget" : "idle";

            if (veto is not null) { reasons["skipped"] = veto; skipped.Add(new(r.Id, "virtualize", reasons)); continue; }
            // The window limit throttles opportunistic churn only. Holding the live budget is an invariant:
            // a fast-switching user must not be able to grow the pool past it (found by ThrashSimulationTests).
            if (!required && budgetLeft <= 0) { reasons["skipped"] = "hibernation_window_limit"; skipped.Add(new(r.Id, "virtualize", reasons)); continue; }

            virtualize.Add(new(r.Id, "virtualize", reasons));
            _recentHibernations.Enqueue(now);
            _lastAutomatedTransition[r.Id] = now;
            if (!required) budgetLeft--;
            if (required) over--;
        }

        // Prewarm: only in Green with headroom; cancelled first when pressure rises (§20).
        ResourceId? prewarm = null;
        if (Policy.AllowPrewarm && band == PressureBand.Green && live.Count - virtualize.Count < target)
        {
            prewarm = resources
                .Where(r => r.State == ResourceState.Virtual)
                .Select(r => (r, score: RevisitScore(r, now)))
                .Where(x => x.score > 0.6)
                .OrderByDescending(x => x.score)
                .Select(x => (ResourceId?)x.r.Id)
                .FirstOrDefault();
        }

        return new ResourcePlan(band, target, live.Count, virtualize, suspend, prewarm, skipped);
    }

    /// <summary>Layer-3 local scoring: recency decays over an hour, frequency adds up to +0.3, workspace priority scales.</summary>
    public static double RevisitScore(ResourceRuntime r, DateTimeOffset now)
    {
        var idleMin = Math.Max(0, (now - r.LastActive).TotalMinutes);
        var recency = Math.Exp(-idleMin / 60.0);
        var frequency = Math.Min(0.3, r.VisitCount * 0.03);
        return Math.Clamp((recency + frequency) * r.WorkspacePriority, 0, 1);
    }
}
