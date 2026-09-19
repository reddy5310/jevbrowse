using JevBrowse.Domain;
using JevBrowse.ResourceOS;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class ResourceOsIntegrationTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 20 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;

    public ResourceOsIntegrationTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now);
        _k.Load();
    }

    public void Dispose() => _db.Dispose();

    private static SystemPressure Pressure(double avail) => new((long)(16L * (1 << 30) * avail), 16L * (1 << 30), 0, false, false, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Plan_is_applied_and_decisions_are_explainable()
    {
        var tabs = new List<VirtualTab>();
        for (int i = 0; i < 6; i++) { var t = _k.Open(new Uri($"https://s{i}.test")); await _k.ActivateAsync(t.Id); tabs.Add(t); _now += TimeSpan.FromMinutes(1); }
        _now += TimeSpan.FromMinutes(5);

        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 2 });
        var plan = s.Evaluate(Pressure(0.5), _k.Snapshot(), _now);
        Assert.Equal(4, plan.Virtualize.Count);

        var n = await _k.ApplyPlanAsync(plan);
        Assert.Equal(4, n);
        Assert.Equal(2, _k.LiveCount);
        Assert.Equal(ResourceState.Hot, tabs[5].State);        // active never touched
        Assert.Equal(ResourceState.Virtual, tabs[0].State);    // oldest went first
        Assert.Contains("inactive_minutes", _k.LastDecision(tabs[0].Id)!.Explain());
        Assert.Equal(2, _leases.MaxLive);                       // budget propagated to pool
    }

    [Fact]
    public async Task Veto_that_appears_after_planning_is_honoured_at_execution()
    {
        var a = _k.Open(new Uri("https://a.test")); await _k.ActivateAsync(a.Id); _now += TimeSpan.FromMinutes(1);
        var b = _k.Open(new Uri("https://b.test")); await _k.ActivateAsync(b.Id); _now += TimeSpan.FromMinutes(5);

        var plan = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 1 }).Evaluate(Pressure(0.5), _k.Snapshot(), _now);
        Assert.Single(plan.Virtualize);

        _leases[a.Id].RaiseDetected(ProtectionFlags.Audible); // user hit play between plan and apply
        Assert.Equal(0, await _k.ApplyPlanAsync(plan));
        Assert.True(a.State.HasLiveRenderer());
        Assert.Contains("executed", _k.LastDecision(a.Id)!.Reasons.Keys);
    }
}
