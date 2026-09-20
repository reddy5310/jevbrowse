using System.Net.Http.Json;
using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.AgentGateway.Tests;

public class AgentGatewayTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly List<AuditEntry> _audit = [];
    private bool _confirmAnswer;
    private int _confirmAsked;
    private readonly AgentGateway _gw;

    public AgentGatewayTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.GetTempPath(), (_, _) => { _confirmAsked++; return Task.FromResult(_confirmAnswer); }, (_, e) => _audit.Add(e), () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
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

    private static AgentManifest Manifest(params AgentAction[] actions) => new()
    {
        Agent = "Claude Code", Workspace = "JevBrowse Development",
        AllowDomains = ["localhost", "github.com", "learn.microsoft.com"],
        Actions = actions.Length == 0 ? [AgentAction.Navigate, AgentAction.Read, AgentAction.Click, AgentAction.TypeNonSecret, AgentAction.Screenshot] : [.. actions],
        MaxLivePages = 2, SessionMinutes = 60,
    };

    [Fact]
    public async Task Session_gets_its_own_disposable_workspace_and_is_audited()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        var ws = _k.Workspaces.Single(w => w.Id == s.WorkspaceId);
        Assert.StartsWith("JevBrowse Development · ", ws.Name);   // the requested name is a label; every session gets its own workspace
        Assert.Equal(IdentityContainer.Disposable, ws.Container);
        Assert.Single(_audit);
        Assert.Equal("open", _audit[0].Action);
    }

    [Fact]
    public async Task Domain_scope_is_enforced_and_denials_are_audited()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default)).Ok);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://api.github.com/x"), default)).Ok); // subdomain
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://mail.google.com/"), default);
        Assert.False(r.Ok);
        Assert.StartsWith("domain_not_allowed", r.Message);
        Assert.Contains(_audit, e => !e.Allowed && e.Reason.StartsWith("domain_not_allowed"));
        Assert.DoesNotContain(_k.Tabs, t => t.Url.Host == "mail.google.com");
    }

    [Fact]
    public async Task Actions_not_in_manifest_are_denied()
    {
        var s = await _gw.OpenAsync(Manifest(AgentAction.Navigate, AgentAction.Read), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/"), default);
        Assert.Equal("action_not_granted:Click", (await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "a"), default)).Message);
        Assert.Equal("action_not_granted:Screenshot", (await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default)).Message);
    }

    [Fact]
    public async Task Live_page_quota_virtualizes_lru_agent_pages()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        for (int i = 0; i < 5; i++)
        {
            await _gw.ExecuteAsync(s, new(AgentAction.Navigate, $"https://github.com/p{i}"), default);
            _now += TimeSpan.FromSeconds(10);
            Assert.True(_gw.LiveAgentPages(s) <= 2, $"live {_gw.LiveAgentPages(s)} after page {i}");
        }
        Assert.Equal(5, s.Pages.Count);
        Assert.Equal(2, _gw.LiveAgentPages(s));
        Assert.Contains(_audit, e => e.Action == "quota");
    }

    [Fact]
    public async Task Read_returns_page_map_without_field_values()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        _leases[tab.Id].Map = new PageMap(tab.Url, "jevbrowse", ["reddy5310/jevbrowse"], [new("Issues", "/reddy5310/jevbrowse/issues")], [new("q", "text", "Search"), new("token", "password", "Token")], "…");
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Page!.Fields.Count);
        Assert.Equal("password", r.Page.Fields[1].Type);
    }

    [Fact]
    public async Task Authenticated_pages_are_denied_by_default_manifest()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/settings/profile"), default);
        Assert.Equal("data_class_denied:Authenticated", (await _gw.ExecuteAsync(s, new(AgentAction.Read), default)).Message);
    }

    [Fact]
    public async Task Data_class_ceiling_blocks_reads_on_sensitive_pages()
    {
        var s = await _gw.OpenAsync(new AgentManifest { Agent = "a", AllowDomains = ["paypal.com"], Actions = [AgentAction.Navigate, AgentAction.Read] }, default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://www.paypal.com/myaccount"), default);
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);
        Assert.False(r.Ok);
        Assert.Equal("data_class_denied:Sensitive", r.Message);
    }

    [Fact]
    public async Task Password_field_page_is_hard_denied_even_if_manifest_allows_secret()
    {
        var s = await _gw.OpenAsync(new AgentManifest { Agent = "a", AllowDomains = ["github.com"], DenyDataClasses = [], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.TypeNonSecret] }, default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/login"), default);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        _leases[tab.Id].RaiseSignals(PageSignals.PasswordField);
        Assert.Equal("hard:secret_page", (await _gw.ExecuteAsync(s, new(AgentAction.Read), default)).Message);
    }

    [Fact]
    public async Task Typing_into_secret_selectors_is_refused_before_reaching_the_page()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/"), default);
        var r = await _gw.ExecuteAsync(s, new(AgentAction.TypeNonSecret, Selector: "#password", Text: "hunter2"), default);
        Assert.Equal("hard:secret_field_selector", r.Message);
        var tab = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        Assert.Empty(_leases[tab.Id].Typed);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.TypeNonSecret, Selector: "#search", Text: "webview2"), default)).Ok);
        Assert.Single(_leases[tab.Id].Typed);
    }

    [Fact]
    public async Task Destructive_clicks_need_human_confirmation()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);
        _confirmAnswer = false;
        var denied = await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "button.delete-repo"), default);
        Assert.Equal("destructive_not_confirmed_by_user", denied.Message);
        _confirmAnswer = true;
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "button.delete-repo"), default)).Ok);
        Assert.Equal(2, _confirmAsked);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "a.nav-link"), default)).Ok);
        Assert.Equal(2, _confirmAsked); // non-destructive: no prompt
    }

    [Fact]
    public async Task Session_expiry_and_action_quota()
    {
        var s = await _gw.OpenAsync(new AgentManifest { Agent = "a", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate], MaxActions = 2, SessionMinutes = 10 }, default);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/1"), default)).Ok);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/2"), default)).Ok);
        Assert.Equal("action_quota_exhausted", (await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/3"), default)).Message);
        var s2 = await _gw.OpenAsync(new AgentManifest { Agent = "b", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate], SessionMinutes = 1 }, default);
        _now += TimeSpan.FromMinutes(2);
        Assert.Equal("session_expired", (await _gw.ExecuteAsync(s2, new(AgentAction.Navigate, "https://github.com/"), default)).Message);
    }

    [Fact]
    public async Task Close_virtualizes_agent_pages()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/"), default);
        Assert.Equal(1, _gw.LiveAgentPages(s));
        await _gw.CloseAsync(s, default);
        Assert.Equal(0, _gw.LiveAgentPages(s));
        Assert.Equal("session_closed", (await _gw.ExecuteAsync(s, new(AgentAction.Read), default)).Message);
    }

    [Fact]
    public async Task Local_host_requires_token_and_forwards_to_gateway()
    {
        using var host = new LocalAgentHost(_gw, Ceiling());
        host.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/") };

        var unauth = await http.PostAsJsonAsync("sessions", Manifest());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauth.StatusCode);

        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        var open = await http.PostAsJsonAsync("sessions", Manifest());
        open.EnsureSuccessStatusCode();
        var id = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var ok = await http.PostAsJsonAsync($"sessions/{id}/actions", new { action = "Navigate", url = "https://github.com/" });
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        var bad = await http.PostAsJsonAsync($"sessions/{id}/actions", new { action = "Navigate", url = "https://evil.test/" });
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, bad.StatusCode);

        var audit = await http.GetFromJsonAsync<JsonElement>($"sessions/{id}/audit");
        Assert.True(audit.GetArrayLength() >= 3);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.DeleteAsync($"sessions/{id}")).StatusCode);
    }
}
