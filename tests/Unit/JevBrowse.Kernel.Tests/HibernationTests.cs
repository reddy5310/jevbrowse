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
    public async Task Capture_failure_keeps_the_renderer_for_automatic_demotion_but_the_user_may_override()
    {
        var a = await OpenAndActivate("a.test");
        _leases.FailNextCapture = true;
        var auto = await _k.VirtualizeAsync(a.Id, Cause.Scheduler);
        Assert.False(auto.Allowed);
        Assert.Equal("capture_failed: renderer kept", auto.Reason);
        Assert.Equal(ResourceState.Hot, a.State);
        Assert.Equal(1, _k.LiveCount);                      // the unfinished page was NOT thrown away by a scheduler

        _leases.FailNextCapture = true;
        var manual = await _k.VirtualizeAsync(a.Id, Cause.User);   // explicit request: proceeds without a checkpoint
        Assert.True(manual.Allowed);
        Assert.Equal(ResourceState.Virtual, a.State);
        Assert.Null(_k.GetCheckpoint(a.Id));
        Assert.Single(new TabRepository(_db).LoadAll());    // durable row intact
    }

    [Theory]
    [InlineData(CaptureOutcome.TimedOut)]
    [InlineData(CaptureOutcome.Cancelled)]
    public async Task An_automatic_demotion_stops_when_we_cannot_tell_whether_the_page_was_preserved(CaptureOutcome outcome)
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].NextCaptureOutcome = outcome;

        var r = await _k.VirtualizeAsync(a.Id, Cause.Scheduler);

        Assert.False(r.Allowed);
        Assert.Equal($"capture_{outcome.ToString().ToLowerInvariant()}: renderer kept", r.Reason);
        Assert.Equal(1, _k.LiveCount);
        Assert.Equal(outcome, _k.LastCapture(a.Id)!.Outcome);
    }

    [Fact]
    public async Task A_partial_capture_is_kept_but_reported_so_the_user_is_not_promised_their_place()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].ScrollY = 900;
        _leases[a.Id].NextCaptureOutcome = CaptureOutcome.Partial;

        Assert.True((await _k.VirtualizeAsync(a.Id, Cause.Scheduler)).Allowed);   // the address is still worth keeping

        var last = _k.LastCapture(a.Id)!;
        Assert.Equal(CaptureOutcome.Partial, last.Outcome);
        Assert.Equal(0, _k.GetCheckpoint(a.Id)!.ScrollY);                        // and we do not pretend we have it

        // "Partial" alone does not say what was lost. The parts do, and the message names the loss the user will
        // actually notice: the page reopens at the top.
        Assert.True(last.HasAddress);
        Assert.False(last.Preserved.HasFlag(PreservedParts.Position));
        Assert.Equal("previous position unavailable", last.Shortfall);
    }

    [Fact]
    public async Task Losing_only_the_preview_is_not_reported_as_a_failed_restore()
    {
        // The preview is a picture in the restore panel. Losing it changes nothing about the page that comes back,
        // so saying "we could not restore your page" would be false.
        var kept = new CaptureResult(new Checkpoint(new ResourceId(Guid.NewGuid()), new Uri("https://a.test/"), "A", 0, 900, null, null, DateTimeOffset.UnixEpoch),
            CaptureOutcome.Partial, "no preview image", PreservedParts.Address | PreservedParts.Position);
        Assert.Equal("", kept.Shortfall);
        Assert.True(kept.IsUsable);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Policy_refusing_to_persist_is_a_decision_not_a_capture_failure()
    {
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        await _k.SwitchWorkspaceAsync(priv.Id);
        var t = _k.Open(new Uri("https://a.test/x"));
        await _k.ActivateAsync(t.Id);

        Assert.True((await _k.VirtualizeAsync(t.Id, Cause.Scheduler)).Allowed);   // must not be blocked like a failure
        Assert.Null(_k.GetCheckpoint(t.Id));
        Assert.Equal(CaptureOutcome.Captured, _k.LastCapture(t.Id)!.Outcome);
        Assert.Contains("nothing about this page is persisted", _k.LastCapture(t.Id)!.Detail);
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
        _k.SetProtection(a.Id, ProtectionFlags.KeepActive);
        _leases[a.Id].RaiseDetected(ProtectionFlags.DirtyForm);
        var row = new TabRepository(_db).LoadAll().Single();
        Assert.Equal(ProtectionFlags.KeepActive, row.Protection);
    }

    [Fact]
    public async Task Pinning_a_tab_does_not_stop_it_sleeping()
    {
        // These were one control, and the control that read as "keep this handy" also exempted the tab from the
        // scheduler. Pinning is placement now, and placement alone.
        var a = await OpenAndActivate("a.test");
        _k.SetPinned(a.Id, true);

        Assert.True(a.IsPinned);
        Assert.Equal(ProtectionFlags.None, a.Protection);
        Assert.True((await _k.VirtualizeAsync(a.Id, Cause.Scheduler)).Allowed);
        Assert.True(a.IsPinned);                                   // and it is still pinned once asleep
    }

    [Fact]
    public async Task Keeping_a_tab_active_does_not_move_it()
    {
        var a = await OpenAndActivate("a.test");
        var b = await OpenAndActivate("b.test");
        _k.SetProtection(b.Id, ProtectionFlags.KeepActive);

        Assert.False((await _k.VirtualizeAsync(b.Id, Cause.Scheduler)).Allowed);   // it does not sleep
        Assert.False(b.IsPinned);                                                  // and it did not jump the queue
        Assert.Equal([a.Id, b.Id], _k.Tabs.Select(t => t.Id));
    }

    [Fact]
    public async Task Both_choices_survive_a_restart_independently()
    {
        var pinnedOnly = await OpenAndActivate("pin.test");
        var awakeOnly = await OpenAndActivate("awake.test");
        _k.SetPinned(pinnedOnly.Id, true);
        _k.SetProtection(awakeOnly.Id, ProtectionFlags.KeepActive);

        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath());
        k2.Load();

        var pin = k2.Tabs.Single(t => t.Url.Host == "pin.test");
        var awake = k2.Tabs.Single(t => t.Url.Host == "awake.test");
        Assert.True(pin.IsPinned);
        Assert.False(pin.UserProtection.HasFlag(ProtectionFlags.KeepActive));
        Assert.True(awake.UserProtection.HasFlag(ProtectionFlags.KeepActive));
        Assert.False(awake.IsPinned);
        Assert.Equal(pin.Id, k2.Tabs[0].Id);                       // the pinned one comes back at the top
    }

    [Fact]
    public async Task A_pinned_tab_moved_into_a_populated_workspace_lands_at_the_top()
    {
        // Both move paths. Sorting only where the user clicks "pin" is not enough: a tab could sit below ordinary
        // tabs while saying "pinned", then jump to the top after a restart when the database's ORDER BY applied.
        var work = _k.CreateWorkspace("Work", IdentityContainer.Personal);          // same identity: the tab moves
        var dev = _k.CreateWorkspace("Dev", IdentityContainer.Dev);                 // other identity: a new tab
        foreach (var host in new[] { "one.test", "two.test" })
        {
            await _k.MoveToWorkspaceAsync(_k.Open(new Uri($"https://{host}")).Id, work.Id);
            await _k.MoveToWorkspaceAsync(_k.Open(new Uri($"https://{host}")).Id, dev.Id);
        }

        var sameIdentity = _k.Open(new Uri("https://pinned.test"));
        _k.SetPinned(sameIdentity.Id, true);
        await _k.MoveToWorkspaceAsync(sameIdentity.Id, work.Id);
        Assert.Equal(sameIdentity.Id, _k.TabsIn(work.Id).First().Id);

        var crossIdentity = _k.Open(new Uri("https://pinned2.test"));
        _k.SetPinned(crossIdentity.Id, true);
        var moved = await _k.MoveToWorkspaceAsync(crossIdentity.Id, dev.Id);
        Assert.True(moved.IsPinned);
        Assert.Equal(moved.Id, _k.TabsIn(dev.Id).First().Id);

        // And the order the user sees now is the order they get back.
        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath());
        k2.Load();
        Assert.Equal(sameIdentity.Id, k2.TabsIn(work.Id).First().Id);
        Assert.Equal(moved.Id, k2.TabsIn(dev.Id).First().Id);
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
