using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// P0 acceptance conditions 1–3 from the second review: navigation policy in force before the first load,
/// revocation that actually releases (even protected pages), and limits that hold under concurrency.
/// </summary>
public class AgentRevocationTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;

    public AgentRevocationTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.GetTempPath(), (_, _) => Task.FromResult(true), null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose() => _db.Dispose();

    private static AgentManifest Manifest(int pages = 3, int minutes = 60, int actions = 200) => new()
    {
        Agent = "a", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read],
        MaxLivePages = pages, SessionMinutes = minutes, MaxActions = actions,
    };

    // ---- 1. Navigation: policy is in force for the FIRST load ----

    [Fact]
    public async Task An_allowed_url_that_redirects_out_of_scope_is_stopped_on_the_very_first_load()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/start"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));

        // What the renderer would have done with the redirect that arrives DURING the first navigation.
        Assert.True(_leases.WouldAllowInitialNavigation(tab.Id, new Uri("https://github.com/start")));
        Assert.False(_leases.WouldAllowInitialNavigation(tab.Id, new Uri("https://evil.test/landing")));
        Assert.False(_leases[tab.Id].TryNavigate(new Uri("https://evil.test/landing")));
    }

    [Fact]
    public async Task The_policy_is_reapplied_when_a_virtualized_agent_page_is_restored()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/a"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        await _k.VirtualizeAsync(tab.Id, Cause.User);          // renderer gone; a fresh one will be built on Read
        Assert.False(_leases.TryGet(tab.Id, out _));

        await _gw.ExecuteAsync(s, new(AgentAction.Read), default);

        Assert.True(_leases.TryGet(tab.Id, out _));
        Assert.False(_leases[tab.Id].TryNavigate(new Uri("https://evil.test/x")));   // the new renderer is guarded too
    }

    // ---- 2. Revocation: reject, cancel, and actually release ----

    [Fact]
    public async Task Stop_releases_even_a_protected_page_and_only_then_reports_stopped()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/a"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        _leases[tab.Id].RaiseDetected(ProtectionFlags.Audible);   // a scheduler-caused release would be vetoed here
        Assert.Equal(1, _gw.LiveAgentPages(s));

        var stopped = await _gw.StopAsync(s, default);

        Assert.True(stopped);
        Assert.True(s.CleanedUp);
        Assert.Equal(0, _gw.LiveAgentPages(s));
        Assert.Equal("session_closed", (await _gw.ExecuteAsync(s, new(AgentAction.Read), default)).Message);
        Assert.False(_leases.WouldAllowInitialNavigation(tab.Id, new Uri("https://github.com/after")));   // scope revoked
    }

    [Fact]
    public async Task An_expired_request_releases_the_pages_instead_of_only_refusing()
    {
        var s = await _gw.OpenAsync(Manifest(minutes: 1), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/a"), default);
        Assert.Equal(1, _gw.LiveAgentPages(s));

        _now += TimeSpan.FromMinutes(5);
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);   // the request that trips the expiry check

        Assert.Equal("session_expired", r.Message);
        Assert.Equal(0, _gw.LiveAgentPages(s));     // previously: marked closed, pages left live forever
        Assert.True(s.CleanedUp);
    }

    [Fact]
    public async Task No_path_leaves_a_session_closed_with_live_pages()
    {
        // Every way a session can end must satisfy the same invariant: Closed ⇒ pages released, nothing stranded
        // for a later sweep. (The old expiry path violated this and the sweeper's !Closed filter hid it.)
        foreach (var end in new[] { "expire", "stop", "delete" })
        {
            var s = await _gw.OpenAsync(Manifest(minutes: 1), default);
            await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/a"), default);
            Assert.Equal(1, _gw.LiveAgentPages(s));

            switch (end)
            {
                case "expire": _now += TimeSpan.FromMinutes(5); await _gw.ExecuteAsync(s, new(AgentAction.Read), default); break;
                case "stop": await _gw.StopAsync(s, default); break;
                default: await _gw.CloseAsync(s, default); break;
            }

            Assert.True(s.Closed, end);
            Assert.True(s.CleanedUp, end);
            Assert.Equal(0, _gw.LiveAgentPages(s));
            Assert.Equal(0, await _gw.SweepExpiredAsync([s], default));   // nothing was left for the sweeper to find
        }
    }

    // ---- 3. Concurrency: limits hold when requests overlap ----

    [Fact]
    public async Task Concurrent_navigations_cannot_exceed_the_live_page_limit()
    {
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(30);   // widen the check→acquire window
        var s = await _gw.OpenAsync(Manifest(pages: 2), default);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            _gw.ExecuteAsync(s, new(AgentAction.Navigate, $"https://github.com/p{i}"), default)));

        Assert.True(_gw.LiveAgentPages(s) <= 2, $"live={_gw.LiveAgentPages(s)}");
        Assert.True(_leases.MaxConcurrentAcquires <= 1);
        Assert.Contains(results, r => r.Ok);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_overspend_the_action_budget()
    {
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(10);
        var s = await _gw.OpenAsync(Manifest(actions: 5), default);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            _gw.ExecuteAsync(s, new(AgentAction.Navigate, $"https://github.com/q{i}"), default)));

        Assert.Equal(5, s.ActionsUsed);   // non-atomic increments previously allowed overspend
        Assert.Equal(15, s.Audit.Count(a => a.Reason == "action_quota_exhausted"));
    }

    [Fact]
    public async Task Work_already_in_flight_cannot_complete_after_revocation()
    {
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(200);
        var s = await _gw.OpenAsync(Manifest(), default);
        var slow = _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/slow"), default);
        await Task.Delay(30);                       // it is inside the acquisition

        await _gw.StopAsync(s, default);
        var r = await slow;

        Assert.False(r.Ok);
        Assert.Equal(0, _gw.LiveAgentPages(s));
        Assert.True(s.CleanedUp);
    }
}
