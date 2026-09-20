using JevBrowse.Domain;

namespace JevBrowse.Domain.Tests;

public class VirtualTabTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.UnixEpoch;

    private static VirtualTab Tab(ResourceState s = ResourceState.Hot)
    {
        var t = new VirtualTab(ResourceId.New(), new Uri("https://example.com"));
        t.TryTransition(s, Cause.User, T);
        return t;
    }

    [Theory]
    [InlineData(ResourceState.Hot, true)]
    [InlineData(ResourceState.Warm, true)]
    [InlineData(ResourceState.Cold, true)]
    [InlineData(ResourceState.Suspended, true)]
    [InlineData(ResourceState.Virtual, false)]
    [InlineData(ResourceState.Archived, false)]
    public void Only_live_states_hold_renderers(ResourceState s, bool live) =>
        Assert.Equal(live, s.HasLiveRenderer());

    [Fact]
    public void New_tab_is_virtual_without_renderer() =>
        Assert.False(new VirtualTab(ResourceId.New(), new Uri("https://a.test")).State.HasLiveRenderer());

    [Fact]
    public void Demotion_walks_the_state_machine()
    {
        var t = Tab();
        Assert.True(t.TryTransition(ResourceState.Warm, Cause.Scheduler, T).Allowed);
        Assert.True(t.TryTransition(ResourceState.Cold, Cause.Scheduler, T).Allowed);
        Assert.True(t.TryTransition(ResourceState.Suspended, Cause.Scheduler, T).Allowed);
        Assert.True(t.TryTransition(ResourceState.Virtual, Cause.Scheduler, T).Allowed);
        Assert.True(t.TryTransition(ResourceState.Archived, Cause.Scheduler, T).Allowed);
    }

    [Fact]
    public void Cannot_skip_states_when_demoting()
    {
        var t = Tab();
        var r = t.TryTransition(ResourceState.Virtual, Cause.Scheduler, T);
        Assert.False(r.Allowed);
        Assert.Equal(ResourceState.Hot, t.State);
    }

    [Fact]
    public void Cold_may_go_straight_to_virtual()
    {
        var t = Tab(ResourceState.Cold);
        Assert.True(t.TryTransition(ResourceState.Virtual, Cause.Scheduler, T).Allowed);
    }

    [Theory]
    [InlineData(ProtectionFlags.Audible)]
    [InlineData(ProtectionFlags.WebRtcActive)]
    [InlineData(ProtectionFlags.DownloadActive)]
    [InlineData(ProtectionFlags.DirtyForm)]
    [InlineData(ProtectionFlags.KeepActive)]
    [InlineData(ProtectionFlags.NeverHibernateSite)]
    public void Protection_vetoes_automatic_demotion(ProtectionFlags flag)
    {
        var t = Tab(ResourceState.Cold);
        t.SetProtection(flag);
        var r = t.TryTransition(ResourceState.Virtual, Cause.Scheduler, T);
        Assert.False(r.Allowed);
        Assert.Equal(ResourceState.Cold, t.State);
        Assert.Contains("vetoed", r.Reason);
    }

    [Fact]
    public void User_action_overrides_protection()
    {
        var t = Tab(ResourceState.Cold);
        t.SetProtection(ProtectionFlags.KeepActive);
        Assert.True(t.TryTransition(ResourceState.Virtual, Cause.User, T).Allowed);
    }

    [Fact]
    public void Promotion_is_never_vetoed()
    {
        var t = Tab(ResourceState.Virtual);
        t.SetProtection(ProtectionFlags.Audible);
        Assert.True(t.TryTransition(ResourceState.Hot, Cause.Scheduler, T).Allowed);
    }

    [Fact]
    public void Archived_must_return_to_virtual_first()
    {
        var t = Tab(ResourceState.Archived);
        Assert.False(t.TryTransition(ResourceState.Hot, Cause.User, T).Allowed);
        Assert.False(t.TryTransition(ResourceState.Warm, Cause.User, T).Allowed);
        Assert.True(t.TryTransition(ResourceState.Virtual, Cause.User, T).Allowed);
    }

    [Fact]
    public void Same_state_is_noop() =>
        Assert.Equal("no-op", Tab().TryTransition(ResourceState.Hot, Cause.Scheduler, T).Reason);
}
