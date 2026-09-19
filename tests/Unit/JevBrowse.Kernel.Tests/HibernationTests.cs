using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class HibernationTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly TabKernel _k;

    public HibernationTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now);
        _k.Load();
    }

    public void Dispose() => _db.Dispose();

    private async Task<VirtualTab> OpenAndActivate(string host)
    {
        var t = _k.Open(new Uri($"https://{host}"));
        _now += TimeSpan.FromSeconds(1);
        await _k.ActivateAsync(t.Id);
        return t;
    }

    [Fact]
    public async Task Virtualize_writes_checkpoint_and_restore_applies_it()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].RaiseNavigation(new Uri("https://a.test/article"), "Article");
        _leases[a.Id].ScrollY = 1234;

        await _k.VirtualizeAsync(a.Id, Cause.User);
        var cp = _k.GetCheckpoint(a.Id);
        Assert.NotNull(cp);
        Assert.Equal(1234, cp.ScrollY);
        Assert.Equal("https://a.test/article", cp.Url.ToString());

        await _k.ActivateAsync(a.Id);
        Assert.Equal("https://a.test/article", _leases[a.Id].Url.ToString());
        Assert.Equal(1234, _leases[a.Id].Applied?.ScrollY);
    }

    [Fact]
    public async Task Capture_failure_still_virtualizes_and_never_loses_the_tab()
    {
        var a = await OpenAndActivate("a.test");
        _leases.FailNextCapture = true;
        var r = await _k.VirtualizeAsync(a.Id, Cause.User);
        Assert.True(r.Allowed);
        Assert.Equal(ResourceState.Virtual, a.State);
        Assert.Null(_k.GetCheckpoint(a.Id));
        Assert.Single(new TabRepository(_db).LoadAll()); // durable row intact
    }

    [Fact]
    public async Task Checkpoint_survives_restart()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].ScrollY = 99;
        await _k.VirtualizeAsync(a.Id, Cause.User);

        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath());
        k2.Load();
        Assert.Equal(99, k2.GetCheckpoint(a.Id)?.ScrollY);
    }

    [Fact]
    public async Task Detected_protection_vetoes_scheduler_and_clears_when_virtual()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].RaiseDetected(ProtectionFlags.Audible);
        Assert.Equal(ProtectionFlags.Audible, a.Protection);
        Assert.False((await _k.VirtualizeAsync(a.Id, Cause.Scheduler)).Allowed);

        _leases[a.Id].RaiseDetected(ProtectionFlags.None);
        Assert.True((await _k.VirtualizeAsync(a.Id, Cause.Scheduler)).Allowed);
        Assert.Equal(ProtectionFlags.None, a.DetectedProtection);
    }

    [Fact]
    public async Task Detected_flags_are_not_persisted_but_user_flags_are()
    {
        var a = await OpenAndActivate("a.test");
        _k.SetProtection(a.Id, ProtectionFlags.UserPinned);
        _leases[a.Id].RaiseDetected(ProtectionFlags.DirtyForm);
        var row = new TabRepository(_db).LoadAll().Single();
        Assert.Equal(ProtectionFlags.UserPinned, row.Protection);
    }

    [Fact]
    public async Task Restore_timing_is_recorded_only_for_fresh_leases()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].RaiseLoaded();
        Assert.Single(_k.RestoreTimingsMs);
        await _k.ActivateAsync(a.Id); // already live: no new timer
        _leases[a.Id].RaiseLoaded();
        Assert.Single(_k.RestoreTimingsMs);
    }

    [Fact]
    public async Task Close_removes_checkpoint()
    {
        var a = await OpenAndActivate("a.test");
        await _k.VirtualizeAsync(a.Id, Cause.User);
        Assert.NotNull(_k.GetCheckpoint(a.Id));
        await _k.CloseAsync(a.Id);
        Assert.Null(new CheckpointRepository(_db).Get(a.Id));
    }
}
