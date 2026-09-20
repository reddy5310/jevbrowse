using System.Net.Http.Json;
using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>Regression tests for the independent review's agent findings: authority, identity, quota, scope.</summary>
public class AgentAuthorityTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private bool _confirmAnswer;
    private int _confirmAsked;
    private readonly AgentGateway _gw;

    public AgentAuthorityTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.GetTempPath(), (_, _) => { _confirmAsked++; return Task.FromResult(_confirmAnswer); }, null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose() => _db.Dispose();

    private static AgentCeiling Ceiling(int pages = 3) => new()
    {
        Limits = new AgentManifest
        {
            Agent = "ceiling", AllowDomains = ["localhost", "github.com", "learn.microsoft.com"],
            Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Click, AgentAction.TypeNonSecret, AgentAction.Screenshot],
            MaxLivePages = pages, SessionMinutes = 60, MaxActions = 200, DestructiveActions = "confirm",
        },
    };

    private static AgentManifest Manifest() => new()
    {
        Agent = "Claude Code", Workspace = "Dev", AllowDomains = ["github.com", "localhost"],
        Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Click, AgentAction.TypeNonSecret], MaxLivePages = 3, SessionMinutes = 60,
    };

    [Fact]
    public async Task A_session_can_never_name_its_way_into_an_existing_identity_and_sessions_do_not_share_profiles()
    {
        var work = _k.CreateWorkspace("Work", IdentityContainer.Work);
        var m1 = new AgentManifest { Agent = "a", Workspace = "Work", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate], Container = IdentityContainer.Work };
        var s1 = await _gw.OpenAsync(m1, default);
        var s2 = await _gw.OpenAsync(m1, default);

        Assert.NotEqual(work.Id, s1.WorkspaceId);                                    // the name "Work" is a label, not a lookup
        Assert.NotEqual(s1.WorkspaceId, s2.WorkspaceId);                             // each session has its own workspace
        Assert.Equal(IdentityContainer.Disposable, _k.Workspaces.Single(w => w.Id == s1.WorkspaceId).Container);   // Work is not grantable

        await _gw.ExecuteAsync(s1, new(AgentAction.Navigate, "https://github.com/a"), default);
        await _gw.ExecuteAsync(s2, new(AgentAction.Navigate, "https://github.com/b"), default);
        var keys = _leases.IsolationKeys.Values.ToList();
        Assert.Equal(2, keys.Distinct().Count());                                    // distinct isolation keys = distinct profiles
        Assert.DoesNotContain(work.Id, keys);
    }

    [Fact]
    public void Ceiling_lets_an_agent_narrow_but_never_widen_a_grant()
    {
        var c = Ceiling(pages: 2);
        var wide = new AgentManifest
        {
            Agent = "greedy", AllowDomains = ["github.com", "evil.test", "gist.github.com", "mail.google.com"],
            Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Click],
            DenyDataClasses = [], DestructiveActions = "allow", MaxLivePages = 50, SessionMinutes = 9999, MaxActions = 100000,
            Container = IdentityContainer.Personal,
        };
        var r = c.Clamp(wide);

        Assert.Equal(["github.com", "gist.github.com"], r.Effective.AllowDomains);
        Assert.DoesNotContain("evil.test", r.Effective.AllowDomains);
        Assert.Equal(2, r.Effective.MaxLivePages);
        Assert.Equal(60, r.Effective.SessionMinutes);
        Assert.Equal(200, r.Effective.MaxActions);
        Assert.Equal("confirm", r.Effective.DestructiveActions);                     // "allow" is downgraded
        Assert.Contains(DataClass.Secret, r.Effective.DenyDataClasses);
        Assert.Contains(DataClass.Sensitive, r.Effective.DenyDataClasses);           // the ceiling's denies survive an empty request
        Assert.Equal(IdentityContainer.Disposable, r.Effective.Container);
        Assert.NotEmpty(r.Adjustments);

        var narrow = c.Clamp(new AgentManifest { Agent = "polite", AllowDomains = ["github.com"], Actions = [AgentAction.Read], MaxLivePages = 1, SessionMinutes = 5, DestructiveActions = "deny" });
        Assert.Equal(1, narrow.Effective.MaxLivePages);
        Assert.Equal(5, narrow.Effective.SessionMinutes);
        Assert.Equal("deny", narrow.Effective.DestructiveActions);                   // narrowing is honoured
        Assert.Equal([AgentAction.Read], narrow.Effective.Actions);
    }

    [Fact]
    public async Task Holding_the_token_is_not_authority_the_host_clamps_asks_and_can_refuse()
    {
        using var noCeiling = new LocalAgentHost(_gw);
        noCeiling.Start();
        using (var h0 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{noCeiling.Port}/") })
        {
            h0.DefaultRequestHeaders.Authorization = new("Bearer", noCeiling.Token);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await h0.PostAsJsonAsync("sessions", Manifest())).StatusCode);   // fail closed
        }

        var asked = new List<(AgentManifest Requested, AgentManifest Effective)>();
        var answer = false;
        using var host = new LocalAgentHost(_gw, Ceiling(), (req, eff, _) => { asked.Add((req, eff)); return Task.FromResult(answer); });
        host.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);

        var wide = new AgentManifest { Agent = "x", AllowDomains = ["github.com", "evil.test"], Actions = [AgentAction.Navigate, AgentAction.Read], MaxLivePages = 40, SessionMinutes = 5000 };
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("sessions", wide)).StatusCode);   // the user said no
        answer = true;
        var ok = await http.PostAsJsonAsync("sessions", wide);
        ok.EnsureSuccessStatusCode();
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "github.com" }, body.GetProperty("granted").GetProperty("AllowDomains").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(3, body.GetProperty("granted").GetProperty("MaxLivePages").GetInt32());
        Assert.Contains("evil.test", asked[^1].Requested.AllowDomains);
        Assert.DoesNotContain("evil.test", asked[^1].Effective.AllowDomains);        // the user saw the reduced grant

        var nothing = await http.PostAsJsonAsync("sessions", new AgentManifest { Agent = "y", AllowDomains = ["evil.test"], Actions = [AgentAction.Read] });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, nothing.StatusCode);       // nothing left after clamping
    }

    [Fact]
    public async Task A_direct_user_grant_is_registered_with_the_host_so_its_routes_work()
    {
        using var host = new LocalAgentHost(_gw, Ceiling());
        host.Start();
        var (s, _) = await host.GrantAsync(new AgentManifest { Agent = "Claude Code", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read] });
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        var r = await http.PostAsJsonAsync($"sessions/{s.Id}/actions", new { action = "Navigate", url = "https://github.com/reddy5310/jevbrowse" });
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);                    // previously 404: the host had never heard of the session
    }

    [Fact]
    public async Task Live_page_quota_is_a_hard_limit_refused_when_every_live_page_is_protected()
    {
        var s = await _gw.OpenAsync(new AgentManifest { Agent = "q", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate], MaxLivePages = 1 }, default);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/one"), default)).Ok);
        var first = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        _leases[first.Id].RaiseDetected(ProtectionFlags.Audible);                   // the only live page cannot be evicted

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/two"), default);
        Assert.False(r.Ok);
        Assert.Equal("live_page_quota_unsatisfiable", r.Message);
        Assert.Equal(1, _gw.LiveAgentPages(s));                                      // never exceeded
    }

    [Fact]
    public async Task Destructive_detection_judges_the_real_target_not_just_the_selector_text()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        _leases[tab.Id].Elements["button.primary"] = new ElementInfo("button", "button", "Delete this repository", "", "", "", "");   // innocent selector, dangerous button
        _leases[tab.Id].Elements["#go"] = new ElementInfo("button", "submit", "Go", "", "", "", "post");                                 // any POST submit
        _leases[tab.Id].Elements["a.nav"] = new ElementInfo("a", "", "Docs", "", "", "https://github.com/docs", "");

        _confirmAnswer = false;
        Assert.Equal("destructive_not_confirmed_by_user", (await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "button.primary"), default)).Message);
        Assert.Equal("destructive_not_confirmed_by_user", (await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "#go"), default)).Message);
        Assert.Equal(2, _confirmAsked);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "a.nav"), default)).Ok);
        Assert.Equal(2, _confirmAsked);
    }

    [Fact]
    public async Task Every_navigation_the_page_attempts_is_checked_against_the_allowlist()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/x"), default);
        var lease = _leases[_k.Tabs.Single(t => s.Pages.Contains(t.Id)).Id];

        Assert.True(lease.TryNavigate(new Uri("https://github.com/y")));
        Assert.True(lease.TryNavigate(new Uri("https://api.github.com/z")));
        Assert.False(lease.TryNavigate(new Uri("https://evil.test/phish")));         // a click or redirect leaving scope is cancelled
        await _gw.CloseAsync(s, default);
        Assert.False(lease.TryNavigate(new Uri("https://github.com/after-close")));  // and nothing is allowed once the session is closed
    }

    [Fact]
    public async Task Expired_sessions_are_closed_without_the_agent_calling_again()
    {
        using var host = new LocalAgentHost(_gw, Ceiling());
        var (s, _) = await host.GrantAsync(new AgentManifest { Agent = "t", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate], SessionMinutes = 1 });
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/a"), default);
        Assert.Equal(1, _gw.LiveAgentPages(s));

        _now += TimeSpan.FromMinutes(5);
        Assert.Equal(1, await host.SweepExpiredAsync());
        Assert.True(s.Closed);
        Assert.Equal(0, _gw.LiveAgentPages(s));                                       // pages virtualized
        Assert.Equal(0, await host.SweepExpiredAsync());                              // idempotent
    }
}
