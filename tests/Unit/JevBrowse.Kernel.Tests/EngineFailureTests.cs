using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>When the engine behind a page dies, its lease must not stay registered: the next activation would reuse a dead control.</summary>
public class EngineFailureTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public EngineFailureTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    private static async Task Settle() => await Task.Delay(50);

    [Theory]
    [InlineData(true)]    // BrowserProcessExited: the control itself must be recreated
    [InlineData(false)]   // RenderProcessExited
    public async Task A_page_whose_engine_died_is_released_and_comes_back_on_a_fresh_renderer(bool wholeEngine)
    {
        var t = _k.Open(new Uri("https://example.org/article"));
        await _k.ActivateAsync(t.Id);
        var dead = _leases[t.Id];
        var events = new List<string>();
        _k.Changed += e => events.Add(e.Kind);

        dead.RaiseEngineFailure(wholeEngine);
        await Settle();

        Assert.False(_leases.TryGet(t.Id, out _), "the dead lease must no longer be registered");
        Assert.Equal(ResourceState.Virtual, t.State);
        Assert.Contains("engine-failed", events);
        Assert.Null(_k.Active);

        var before = _leases.Acquires;
        await _k.ActivateAsync(t.Id);                                     // what the person (or the app) does next
        Assert.Equal(before + 1, _leases.Acquires);
        Assert.True(_leases.TryGet(t.Id, out var fresh));
        Assert.False(ReferenceEquals(dead, fresh), "a NEW lease, not the dead one");
        Assert.Equal(ResourceState.Hot, t.State);
    }

    [Fact]
    public async Task Protection_does_not_keep_a_dead_renderer_registered()
    {
        var t = _k.Open(new Uri("https://example.org/form"));
        await _k.ActivateAsync(t.Id);
        _k.SetProtection(t.Id, ProtectionFlags.KeepActive | ProtectionFlags.DirtyForm);   // would veto every automatic demotion

        _leases[t.Id].RaiseEngineFailure();
        await Settle();

        Assert.False(_leases.TryGet(t.Id, out _));
        Assert.Equal(ResourceState.Virtual, t.State);
    }

    [Fact]
    public async Task A_background_page_that_crashes_does_not_disturb_the_page_in_front()
    {
        var front = _k.Open(new Uri("https://example.org/front"));
        await _k.ActivateAsync(front.Id);
        var back = _k.Open(new Uri("https://example.org/back"));
        await _k.ActivateAsync(back.Id);
        await _k.ActivateAsync(front.Id);

        _leases[back.Id].RaiseEngineFailure();
        await Settle();

        Assert.Equal(front.Id, _k.Active?.Id);
        Assert.True(_leases.TryGet(front.Id, out _));
        Assert.Equal(ResourceState.Virtual, back.State);
    }

    [Fact]
    public async Task A_stale_failure_report_from_an_old_lease_is_ignored()
    {
        var t = _k.Open(new Uri("https://example.org/a"));
        await _k.ActivateAsync(t.Id);
        var old = _leases[t.Id];
        await _k.VirtualizeAsync(t.Id, Cause.User);
        await _k.ActivateAsync(t.Id);                                    // a new lease now serves the tab
        var current = _leases[t.Id];

        old.RaiseEngineFailure();
        await Settle();

        Assert.True(_leases.TryGet(t.Id, out var still) && ReferenceEquals(still, current));
        Assert.Equal(ResourceState.Hot, t.State);
    }
}
