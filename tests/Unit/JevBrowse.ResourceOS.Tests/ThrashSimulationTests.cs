using JevBrowse.Domain;
using JevBrowse.ResourceOS;

namespace JevBrowse.ResourceOS.Tests;

/// <summary>
/// Replays a synthetic 2-hour session: 30 tabs, a user who switches every ~20 s, and memory pressure that saws
/// between comfortable and critical. The scheduler must converge and must not thrash (Architecture §20, Table A.12).
/// </summary>
public class ThrashSimulationTests
{
    private sealed class SimTab
    {
        public ResourceId Id = ResourceId.New();
        public ResourceState State = ResourceState.Virtual;
        public DateTimeOffset LastActive, LastStateChange;
        public int Visits;
        public int Virtualizations;
        public DateTimeOffset LastVirtualized = DateTimeOffset.MinValue;
    }

    [Fact]
    public void Two_hour_sawtooth_session_does_not_thrash()
    {
        var rng = new Random(42);
        var t0 = DateTimeOffset.UnixEpoch.AddDays(1);
        var tabs = Enumerable.Range(0, 30).Select(_ => new SimTab { LastActive = t0.AddHours(-1), LastStateChange = t0.AddHours(-1) }).ToList();
        var policy = SchedulerPolicy.For(MemoryMode.Balanced);
        var scheduler = new DefaultScheduler(policy);
        SimTab? active = null;
        var quickReVirtualize = 0;
        var totalVirtualizations = 0;
        var maxLiveSeen = 0;
        var liveOverTargetTicks = 0;

        for (int sec = 0; sec < 7200; sec += 5)
        {
            var now = t0.AddSeconds(sec);

            // user: switch tab every ~20 s, favouring a hot set of 6
            if (sec % 20 == 0)
            {
                var pick = rng.NextDouble() < 0.7 ? tabs[rng.Next(6)] : tabs[rng.Next(tabs.Count)];
                if (active is not null && active != pick && active.State == ResourceState.Hot) active.State = ResourceState.Warm;
                if (!pick.State.HasLiveRenderer()) { pick.State = ResourceState.Hot; pick.LastStateChange = now; }
                else pick.State = ResourceState.Hot;
                pick.LastActive = now; pick.Visits++;
                active = pick;
            }

            // pressure: 10-minute sawtooth from 40% available down to 6%
            var phase = (sec % 600) / 600.0;
            var avail = 0.40 - 0.34 * phase;
            var pressure = new SystemPressure((long)(16L * (1 << 30) * avail), 16L * (1 << 30), 0, false, false, now);

            var runtime = tabs.Select(t => new ResourceRuntime(t.Id, t.State, ProtectionFlags.None, t == active, t.LastActive, t.LastStateChange, t.Visits)).ToList();
            var plan = scheduler.Evaluate(pressure, runtime, now);

            foreach (var a in plan.Virtualize)
            {
                var t = tabs.Single(x => x.Id == a.Id);
                Assert.NotSame(active, t);
                if (now - t.LastVirtualized < TimeSpan.FromMinutes(2)) quickReVirtualize++;
                t.State = ResourceState.Virtual; t.LastStateChange = now; t.LastVirtualized = now; t.Virtualizations++;
                totalVirtualizations++;
            }

            var live = tabs.Count(t => t.State.HasLiveRenderer());
            maxLiveSeen = Math.Max(maxLiveSeen, live);
            if (live > plan.TargetLiveRenderers + 1) liveOverTargetTicks++;
        }

        // Convergence: the pool never runs far beyond budget for long.
        Assert.True(liveOverTargetTicks < 7200 / 5 * 0.15, $"over target on {liveOverTargetTicks} ticks");
        // Anti-thrash: total churn is bounded (≤ window limit per 5-min window, ≈ 6 × 24 windows = 144, plus RED bursts).
        Assert.True(totalVirtualizations < 400, $"virtualized {totalVirtualizations} times in 2 h");
        // No tab gets virtualized, restored and re-virtualized inside 2 minutes more than a handful of times.
        Assert.True(quickReVirtualize <= 10, $"{quickReVirtualize} quick re-virtualizations");
        // No single tab is churned excessively.
        Assert.True(tabs.Max(t => t.Virtualizations) <= 30, $"max per-tab churn {tabs.Max(t => t.Virtualizations)}");
    }
}
