using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// Read, Click and Type each wait for something (the engine, a person). What they were authorised against can change during that wait: the session can be stopped or
/// expire, the page can become a different document, or the page can turn into a login form. Nothing may be delivered or done on the strength of checks made before the wait.
/// </summary>
public class AgentAsyncWaitTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;
    private TaskCompletionSource<bool> _confirmation = new();

    public AgentAsyncWaitTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.Combine(Path.GetTempPath(), "jev-aw-" + Guid.NewGuid().ToString("N")), (_, _) => _confirmation.Task, null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose() => _db.Dispose();

    private async Task<(AgentSession S, FakeLease Lease)> Ready()
    {
        var mine = _k.Open(new Uri("https://example.com/"));
        await _k.ActivateAsync(mine.Id);
        var s = await _gw.OpenAsync(new AgentManifest
        {
            Agent = "Claude Code", AllowDomains = ["github.com"], MaxLivePages = 2, SessionMinutes = 60,
            Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Click, AgentAction.TypeNonSecret],
        }, default);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default)).Ok);
        return (s, _leases[_k.Tabs.Single(t => s.Pages.Contains(t.Id)).Id]);
    }

    // ---- Read ----

    [Fact]
    public async Task A_page_map_finished_after_the_session_was_stopped_is_not_delivered()
    {
        var (s, lease) = await Ready();
        var release = new TaskCompletionSource();
        lease.MapGate = () => release.Task;

        var read = _gw.ExecuteAsync(s, new(AgentAction.Read), default);
        await _gw.StopAsync(s, default);
        release.SetResult();
        var r = await read;

        Assert.False(r.Ok);
        Assert.Null(r.Page);
    }

    [Fact]
    public async Task A_page_map_finished_after_the_deadline_is_not_delivered_and_the_session_is_released()
    {
        var (s, lease) = await Ready();
        lease.MapGate = () => { _now = s.ExpiresAt.AddSeconds(1); return Task.CompletedTask; };   // the clock passes the deadline while the engine works

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);

        Assert.False(r.Ok);
        Assert.Contains("read_discarded:session_expired", r.Message);
        Assert.True(s.Closed);
    }

    [Fact]
    public async Task A_page_map_of_a_document_that_was_replaced_meanwhile_is_not_delivered()
    {
        var (s, lease) = await Ready();
        lease.MapGate = () => { lease.RaiseReload(); return Task.CompletedTask; };   // a redirect or reload commits a new document during the read

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);

        Assert.False(r.Ok);
        Assert.Contains("read_discarded:page_changed", r.Message);
    }

    [Fact]
    public async Task A_page_map_is_not_delivered_if_the_page_turned_into_a_login_form_while_it_was_read()
    {
        var (s, lease) = await Ready();
        lease.MapGate = () => { lease.RaiseSignals(PageSignals.PasswordField); return Task.CompletedTask; };

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);

        Assert.False(r.Ok);
        Assert.Null(r.Page);
    }

    // ---- Click ----

    private static ElementInfo Delete() => new("button", "button", "Delete repository", "", "", "", "");

    [Fact]
    public async Task A_click_confirmed_after_the_session_was_stopped_never_happens()
    {
        var (s, lease) = await Ready();
        lease.Elements["#go"] = Delete();

        var click = _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "#go"), default);
        await _gw.StopAsync(s, default);          // the person stops the agent while the confirmation is still on screen …
        _confirmation.SetResult(true);            // … and the dialog is then answered "yes"
        var r = await click;

        Assert.False(r.Ok);
        Assert.Empty(lease.Clicked);
    }

    [Fact]
    public async Task A_click_confirmed_after_the_session_expired_never_happens()
    {
        var (s, lease) = await Ready();
        lease.Elements["#go"] = Delete();

        var click = _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "#go"), default);
        _now = s.ExpiresAt.AddMinutes(1);
        _confirmation.SetResult(true);
        var r = await click;

        Assert.False(r.Ok);
        Assert.Empty(lease.Clicked);
        Assert.Contains("click_discarded:session_expired", r.Message);
    }

    [Fact]
    public async Task A_click_confirmed_about_one_page_is_not_performed_on_another()
    {
        var (s, lease) = await Ready();
        lease.Elements["#go"] = Delete();

        var click = _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "#go"), default);
        lease.RaiseReload();                      // the page navigates while the person is deciding
        _confirmation.SetResult(true);
        var r = await click;

        Assert.False(r.Ok);
        Assert.Empty(lease.Clicked);
        Assert.Contains("click_discarded:page_changed", r.Message);
    }

    [Fact]
    public async Task A_confirmed_click_on_an_unchanged_page_still_happens()
    {
        var (s, lease) = await Ready();
        lease.Elements["#go"] = Delete();

        var click = _gw.ExecuteAsync(s, new(AgentAction.Click, Selector: "#go"), default);
        _confirmation.SetResult(true);
        var r = await click;

        Assert.True(r.Ok, r.Message);
        Assert.Equal(["#go"], lease.Clicked);
    }
}
