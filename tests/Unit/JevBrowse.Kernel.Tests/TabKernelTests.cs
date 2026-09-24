using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class TabKernelTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private readonly TabKernel _k;

    public TabKernelTests()
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
    public async Task Fifty_visible_tabs_never_exceed_five_live_renderers()
    {
        for (int i = 0; i < 50; i++)
        {
            await OpenAndActivate($"site{i}.test");
            Assert.True(_k.LiveCount <= 5, $"live={_k.LiveCount} after tab {i}");
        }
        Assert.Equal(50, _k.Tabs.Count);
        Assert.Equal(5, _k.LiveCount);
        Assert.Equal(45, _k.Tabs.Count(t => t.State == ResourceState.Virtual));
        Assert.Equal(45, _leases.Disposes);
    }

    [Fact]
    public async Task Eviction_is_least_recently_active()
    {
        var tabs = new List<VirtualTab>();
        for (int i = 0; i < 5; i++) tabs.Add(await OpenAndActivate($"s{i}.test"));
        await _k.ActivateAsync(tabs[0].Id); _now += TimeSpan.FromSeconds(1); // s0 becomes most recent
        await OpenAndActivate("s5.test");
        Assert.Equal(ResourceState.Virtual, tabs[1].State); // s1 was oldest
        Assert.True(tabs[0].State.HasLiveRenderer());
    }

    [Fact]
    public async Task Protected_tabs_are_never_auto_evicted()
    {
        var tabs = new List<VirtualTab>();
        for (int i = 0; i < 5; i++) tabs.Add(await OpenAndActivate($"s{i}.test"));
        foreach (var t in tabs) _k.SetProtection(t.Id, ProtectionFlags.Audible);
        await OpenAndActivate("s5.test");
        Assert.All(tabs, t => Assert.True(t.State.HasLiveRenderer()));
        Assert.Equal(6, _k.LiveCount); // budget exceeded rather than isolation/protection weakened
    }

    [Fact]
    public async Task Activate_hides_previous_and_shows_new()
    {
        var a = await OpenAndActivate("a.test");
        var b = await OpenAndActivate("b.test");
        Assert.False(_leases[a.Id].IsVisible);
        Assert.True(_leases[b.Id].IsVisible);
        Assert.Equal(ResourceState.Warm, a.State);
        Assert.Equal(ResourceState.Hot, b.State);
    }

    [Fact]
    public async Task Reactivating_virtual_tab_acquires_new_lease()
    {
        var a = await OpenAndActivate("a.test");
        await _k.VirtualizeAsync(a.Id, Cause.User);
        Assert.Equal(0, _k.LiveCount);
        await _k.ActivateAsync(a.Id);
        Assert.Equal(ResourceState.Hot, a.State);
        Assert.Equal(2, _leases.Acquires);
    }

    [Fact]
    public async Task Scheduler_virtualize_respects_veto_but_user_overrides()
    {
        var a = await OpenAndActivate("a.test");
        _k.SetProtection(a.Id, ProtectionFlags.DirtyForm);
        var r = await _k.VirtualizeAsync(a.Id, Cause.Scheduler);
        Assert.False(r.Allowed);
        Assert.Equal(ResourceState.Hot, a.State);
        Assert.Equal(1, _k.LiveCount);

        r = await _k.VirtualizeAsync(a.Id, Cause.User);
        Assert.True(r.Allowed);
        Assert.Equal(0, _k.LiveCount);
    }

    [Fact]
    public async Task Navigation_updates_tab_and_persists()
    {
        var a = await OpenAndActivate("a.test");
        _leases[a.Id].RaiseNavigation(new Uri("https://a.test/page2"), "Page 2");
        Assert.Equal("Page 2", a.Title);
        var row = new TabRepository(_db).LoadAll().Single();
        Assert.Equal("Page 2", row.Title);
        Assert.Equal("https://a.test/page2", row.Url.ToString());
    }

    [Fact]
    public async Task Tabs_survive_restart_as_virtual()
    {
        for (int i = 0; i < 8; i++) await OpenAndActivate($"s{i}.test");
        _k.SetProtection(_k.Tabs[2].Id, ProtectionFlags.KeepActive);

        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath());
        k2.Load();
        Assert.Equal(8, k2.Tabs.Count);
        Assert.All(k2.Tabs, t => Assert.Equal(ResourceState.Virtual, t.State));
        Assert.Equal(0, k2.LiveCount);
        Assert.Equal(ProtectionFlags.KeepActive, k2.Tabs[2].Protection);
        Assert.Equal(_k.Tabs.Select(t => t.Id), k2.Tabs.Select(t => t.Id)); // order preserved
    }

    [Fact]
    public async Task Close_releases_renderer_and_deletes_row()
    {
        var a = await OpenAndActivate("a.test");
        var b = await OpenAndActivate("b.test");
        await _k.CloseAsync(a.Id);
        Assert.Single(_k.Tabs);
        Assert.Equal(1, _k.LiveCount);
        Assert.Single(new TabRepository(_db).LoadAll());
        Assert.Equal(b.Id, _k.Tabs[0].Id);
    }

    [Fact]
    public void Open_and_OpenIn_use_the_given_presetId_exactly_when_one_is_given()
    {
        var id = ResourceId.New();
        var t = _k.Open(new Uri("https://example.org/a"), presetId: id);
        Assert.Equal(id, t.Id);
        Assert.Single(_k.Tabs, x => x.Id == id);

        var ws = _k.CreateWorkspace("Other");
        var id2 = ResourceId.New();
        var t2 = _k.OpenIn(ws.Id, new Uri("https://example.org/b"), presetId: id2);
        Assert.Equal(id2, t2.Id);
        Assert.Single(_k.Tabs, x => x.Id == id2);
    }

    [Fact]
    public void Open_and_OpenIn_still_generate_a_fresh_id_when_none_is_given()
    {
        var a = _k.Open(new Uri("https://example.org/a"));
        var b = _k.Open(new Uri("https://example.org/b"));
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(default, a.Id);
    }
}
