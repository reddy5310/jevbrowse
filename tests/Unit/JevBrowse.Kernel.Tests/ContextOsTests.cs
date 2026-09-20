using JevBrowse.Domain;
using JevBrowse.ResourceOS;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class ContextOsTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 8 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;

    public ContextOsTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
    }

    public void Dispose() => _db.Dispose();

    private static SystemPressure Pressure(double avail) => new((long)(16L * (1 << 30) * avail), 16L * (1 << 30), 0, false, false, DateTimeOffset.UnixEpoch);

    private async Task<List<VirtualTab>> Populate(ContextId ws, int n, string tag)
    {
        await _k.SwitchWorkspaceAsync(ws);
        var list = new List<VirtualTab>();
        for (int i = 0; i < n; i++) { var t = _k.Open(new Uri($"https://{tag}{i}.test")); await _k.ActivateAsync(t.Id); _now += TimeSpan.FromSeconds(30); list.Add(t); }
        return list;
    }

    [Fact]
    public async Task Default_workspace_exists_and_new_tabs_join_active_workspace()
    {
        Assert.Single(_k.Workspaces);
        var dev = _k.CreateWorkspace("Dev");
        await _k.SwitchWorkspaceAsync(dev.Id);
        var t = _k.Open(new Uri("https://localhost:3000"));
        Assert.Equal(dev.Id, t.WorkspaceId);
        Assert.Single(_k.TabsIn(dev.Id));
        Assert.Empty(_k.TabsIn(ContextId.Default));
    }

    [Fact]
    public async Task Two_contexts_x_40_resources_inactive_context_drains_to_virtual()
    {
        // Architecture Table A.12 workload: 2 contexts × 40 resources, workspace switch reclaim.
        var research = _k.CreateWorkspace("Research");
        var dev = _k.CreateWorkspace("Dev");
        await Populate(research.Id, 40, "r");
        Assert.True(_k.LiveCount <= 8);
        await Populate(dev.Id, 40, "d");
        Assert.Equal(80, _k.Tabs.Count);

        // Scheduler runs with Dev active: Research tabs carry priority 0.3 and drain at the shorter idle threshold.
        var s = new DefaultScheduler(SchedulerPolicy.For(MemoryMode.Balanced) with { MaxLive = 8 });
        _now += TimeSpan.FromMinutes(7); // > 20 min × 0.3 = 6 min for background; < 20 min for foreground
        var plan = s.Evaluate(Pressure(0.5), _k.Snapshot(), _now);
        await _k.ApplyPlanAsync(plan);

        Assert.All(_k.TabsIn(research.Id), t => Assert.Equal(ResourceState.Virtual, t.State));
        Assert.Contains(_k.TabsIn(dev.Id), t => t.State.HasLiveRenderer());
        Assert.Equal(dev.Id, _k.ActiveWorkspace);
    }

    [Fact]
    public async Task Switching_back_activates_last_active_tab_of_that_workspace()
    {
        var a = _k.CreateWorkspace("A"); var b = _k.CreateWorkspace("B");
        var ta = await Populate(a.Id, 3, "a");
        await _k.ActivateAsync(ta[1].Id); _now += TimeSpan.FromSeconds(5);
        await Populate(b.Id, 2, "b");
        await _k.SwitchWorkspaceAsync(a.Id);
        Assert.Equal(ta[1].Id, _k.Active!.Id);
        Assert.Equal(ResourceState.Hot, ta[1].State);
    }

    [Fact]
    public async Task Move_tab_between_workspaces_persists()
    {
        var dev = _k.CreateWorkspace("Dev");
        var t = _k.Open(new Uri("https://github.com"));
        await _k.MoveToWorkspaceAsync(t.Id, dev.Id);
        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        k2.Load();
        Assert.Equal(dev.Id, k2.Tabs.Single().WorkspaceId);
        Assert.Equal(2, k2.Workspaces.Count);
    }

    [Fact]
    public async Task Time_travel_checkpoint_restores_lazily()
    {
        var trip = _k.CreateWorkspace("Trip");
        var tabs = await Populate(trip.Id, 5, "t");
        var cp = _k.RecordContextCheckpoint();
        Assert.Equal(5, cp.Resources.Count);
        Assert.Equal(5, cp.LiveCount);
        Assert.Equal(tabs[4].Id, cp.ActiveResource);

        foreach (var t in tabs.Take(3)) await _k.CloseAsync(t.Id);          // user closed 3
        await _k.VirtualizeAsync(tabs[3].Id, Cause.User);
        await _k.VirtualizeAsync(tabs[4].Id, Cause.User);
        Assert.Equal(0, _k.LiveCount);

        var timeline = _k.Timeline();
        Assert.Contains(timeline, c => c.Id == cp.Id);
        var recreated = await _k.RestoreContextAsync(timeline.Single(c => c.Id == cp.Id));
        Assert.Equal(3, recreated);
        Assert.Equal(5, _k.TabsIn(trip.Id).Count());
        Assert.Equal(1, _k.LiveCount);                                       // only the active one loads
        Assert.Equal(tabs[4].Id, _k.Active!.Id);
        Assert.Equal(3, _k.TabsIn(trip.Id).Count(t => t.State == ResourceState.Virtual && t.Id != tabs[4].Id && t.Id != tabs[3].Id));
    }

    [Fact]
    public void Checkpoint_retention_is_bounded()
    {
        var repo = new WorkspaceRepository(_db);
        for (int i = 0; i < 30; i++)
            repo.SaveCheckpoint(new ContextCheckpoint(Guid.NewGuid(), ContextId.Default, "Default", _now.AddHours(-i), null, [], 0));
        var pruned = repo.PruneCheckpoints(keepPerWorkspace: 5, maxAge: TimeSpan.FromHours(10), _now);
        Assert.Equal(19, pruned); // 30 total; 11 are ≤10 h old (i=0..10); of the 19 older, none are within the newest 5
        Assert.Equal(11, repo.ListCheckpoints().Count);
    }
}
