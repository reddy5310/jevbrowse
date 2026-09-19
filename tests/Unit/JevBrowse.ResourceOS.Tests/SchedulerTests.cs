using JevBrowse.Domain;
using JevBrowse.ResourceOS;

namespace JevBrowse.ResourceOS.Tests;

public class SchedulerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch.AddDays(1);
    private const long GB = 1L << 30;

    private static SystemPressure Pressure(double availFraction, bool battery = false) =>
        new((long)(16 * GB * availFraction), 16 * GB, 0, battery, false, T0);

    private static ResourceRuntime Live(string tag, TimeSpan idle, TimeSpan residency, ProtectionFlags prot = ProtectionFlags.None, bool active = false, int visits = 1) =>
        new(new ResourceId(Guid.Parse($"00000000-0000-0000-0000-{tag.PadLeft(12, '0')}")), ResourceState.Warm, prot, active, T0 - idle, T0 - residency, visits);

    [Fact]
    public void Band_hysteresis_prevents_flapping()
    {
        var t = new PressureBandTracker();
        Assert.Equal(PressureBand.Green, t.Update(0.30));
        Assert.Equal(PressureBand.Yellow, t.Update(0.24));   // enters Yellow below 0.25
        Assert.Equal(PressureBand.Yellow, t.Update(0.26));   // 0.26 < 0.28 exit: still Yellow
        Assert.Equal(PressureBand.Yellow, t.Update(0.27));
        Assert.Equal(PressureBand.Green, t.Update(0.29));    // past margin
        Assert.Equal(PressureBand.Red, t.Update(0.05));      // worsening is immediate
        Assert.Equal(PressureBand.Red, t.Update(0.10));      // 0.10 < 0.11 exit
        Assert.Equal(PressureBand.Orange, t.Update(0.12));
    }

    [Fact]
    public void Green_with_recent_activity_is_a_noop()
    {
        var s = new DefaultScheduler();
        var res = Enumerable.Range(1, 5).Select(i => Live(i.ToString(), TimeSpan.FromMinutes(i), TimeSpan.FromMinutes(10))).ToList();
        var plan = s.Evaluate(Pressure(0.5), res, T0);
        Assert.True(plan.IsNoOp);
        Assert.Equal(8, plan.TargetLiveRenderers);
    }

    [Fact]
    public void Over_budget_evicts_lowest_revisit_score_never_active_never_protected()
    {
        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 3 });
        var res = new List<ResourceRuntime>
        {
            Live("1", TimeSpan.Zero, TimeSpan.FromMinutes(5), active: true),
            Live("2", TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(5)),                          // oldest → evict
            Live("3", TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(5), ProtectionFlags.Audible),  // older but protected
            Live("4", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
            Live("5", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5)),
        };
        var plan = s.Evaluate(Pressure(0.5), res, T0);
        Assert.Equal(2, plan.Virtualize.Count); // 5 live, target 3
        Assert.Equal(["000000000002", "000000000005"], plan.Virtualize.Select(a => a.Id.ToString()[^12..]).ToArray());
        Assert.Contains(plan.Skipped, a => a.Id == res[2].Id && a.Reasons["skipped"] == "protected");
        Assert.Equal("no", plan.Virtualize[0].Reasons["jev_consulted"]);
    }

    [Fact]
    public void Min_residency_protects_freshly_restored_tabs_even_under_orange()
    {
        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 2 });
        var res = new List<ResourceRuntime>
        {
            Live("1", TimeSpan.Zero, TimeSpan.Zero, active: true),
            Live("2", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5)), // restored 5 s ago
            Live("3", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5)),
        };
        var plan = s.Evaluate(Pressure(0.12), res, T0);
        Assert.Equal(PressureBand.Orange, plan.Band);
        Assert.Single(plan.Virtualize);
        Assert.Equal(res[2].Id, plan.Virtualize[0].Id);
        Assert.Contains(plan.Skipped, a => a.Id == res[1].Id && a.Reasons["skipped"] == "min_residency");
    }

    [Fact]
    public void Window_limit_throttles_idle_evictions_but_never_over_budget_ones()
    {
        // 6 idle tabs, budget 8: all evictions are opportunistic → limited to 2 per window.
        var policy = SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 8, MaxHibernationsPerWindow = 2 };
        var res = Enumerable.Range(1, 6).Select(i => Live(i.ToString(), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5))).ToList();
        var plan = new DefaultScheduler(policy).Evaluate(Pressure(0.5), res, T0);
        Assert.Equal(2, plan.Virtualize.Count);
        Assert.Equal(4, plan.Skipped.Count(a => a.Reasons["skipped"] == "hibernation_window_limit"));
        Assert.All(plan.Virtualize, a => Assert.Equal("idle", a.Reasons["trigger"]));

        // Same tabs, budget 1: 5 evictions are required to hold the budget and bypass the limit.
        var tight = new DefaultScheduler(policy with { MaxLive = 1 }).Evaluate(Pressure(0.5), res, T0);
        Assert.Equal(5, tight.Virtualize.Count(a => a.Reasons["trigger"] == "over_budget"));

        var red = new DefaultScheduler(policy).Evaluate(Pressure(0.03), res, T0);
        Assert.Equal(6, red.Virtualize.Count);
        Assert.Equal(1, red.TargetLiveRenderers);
    }

    [Fact]
    public void Cooldown_blocks_repeat_automation_on_same_resource()
    {
        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 1, MinResidency = TimeSpan.FromSeconds(5), Cooldown = TimeSpan.FromSeconds(45) });
        var r = Live("1", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        Assert.Single(s.Evaluate(Pressure(0.5), [r], T0).Virtualize);
        // user restored it 10 s later then moved on to another tab (pool now over budget);
        // at T0+30 residency (20 s) is satisfied but cooldown (45 s) is not
        var again = r with { LastStateChange = T0.AddSeconds(10), LastActive = T0.AddSeconds(10) };
        var other = Live("2", TimeSpan.Zero, TimeSpan.Zero, active: true);
        var plan = s.Evaluate(Pressure(0.5), [again, other], T0.AddSeconds(30));
        Assert.Empty(plan.Virtualize);
        Assert.Contains(plan.Skipped, a => a.Reasons["skipped"] == "cooldown");
        // and after cooldown it is eligible again
        Assert.Single(s.Evaluate(Pressure(0.5), [again, other], T0.AddSeconds(60)).Virtualize);
    }

    [Fact]
    public void Battery_in_balanced_mode_tightens_to_yellow()
    {
        var s = new DefaultScheduler();
        var plan = s.Evaluate(Pressure(0.6, battery: true), [], T0);
        Assert.Equal(PressureBand.Yellow, plan.Band);
        Assert.Equal(6, plan.TargetLiveRenderers);
    }

    [Fact]
    public void Prewarm_only_in_green_with_headroom_and_high_revisit()
    {
        var s = new DefaultScheduler();
        var hot = Live("1", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), visits: 10) with { State = ResourceState.Virtual };
        var cold = Live("2", TimeSpan.FromHours(5), TimeSpan.FromMinutes(5)) with { State = ResourceState.Virtual };
        Assert.Equal(hot.Id, s.Evaluate(Pressure(0.5), [hot, cold], T0).PrewarmCandidate);
        Assert.Null(new DefaultScheduler().Evaluate(Pressure(0.2), [hot, cold], T0).PrewarmCandidate);
    }

    [Fact]
    public void Explain_is_human_readable()
    {
        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 1 });
        var plan = s.Evaluate(Pressure(0.12), [Live("1", TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(5))], T0);
        var text = plan.Virtualize[0].Explain();
        Assert.Contains("inactive_minutes: 31", text);
        Assert.Contains("system_pressure: ORANGE", text);
        Assert.Contains("jev_consulted: no", text);
    }
}
