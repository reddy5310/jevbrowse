using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// An agent works in a workspace of its own and its pages run live, but the person's window is not the agent's to move: it must
/// never change the tab they are looking at, the workspace they are in, or what is on screen. (It used to. Every navigate switched
/// the whole window to the agent's page, and no test noticed.)
/// </summary>
public class AgentDoesNotMoveTheWindowTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private readonly DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;

    public AgentDoesNotMoveTheWindowTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.GetTempPath(), (_, _) => Task.FromResult(false), null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose() => _db.Dispose();

    private static AgentManifest Manifest() => new()
    {
        Agent = "Claude Code", AllowDomains = ["github.com", "localhost"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot],
        MaxLivePages = 2, SessionMinutes = 60,
    };

    private async Task<VirtualTab> PersonIsReading()
    {
        var mine = _k.Open(new Uri("https://example.com/"));
        await _k.ActivateAsync(mine.Id);
        return mine;
    }

    [Fact]
    public async Task An_agent_navigating_leaves_the_active_tab_the_active_workspace_and_the_screen_alone()
    {
        var mine = await PersonIsReading();
        var myWorkspace = _k.ActiveWorkspace;
        var s = await _gw.OpenAsync(Manifest(), default);

        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default)).Ok);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse/issues"), default)).Ok);

        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.Equal(myWorkspace, _k.ActiveWorkspace);
        Assert.True(_leases[mine.Id].IsVisible, "the person's page must still be the one on screen");
    }

    [Fact]
    public async Task The_agents_pages_are_live_in_its_own_workspace_but_not_shown()
    {
        await PersonIsReading();
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);

        var page = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        Assert.Equal(s.WorkspaceId, page.WorkspaceId);
        Assert.True(page.State.HasLiveRenderer(), "the agent needs a live page to read from");
        Assert.False(_leases[page.Id].IsVisible, "and it must not be drawn in the person's window");
    }

    [Fact]
    public async Task Reading_works_on_a_page_that_is_not_shown()
    {
        await PersonIsReading();
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);
        var read = await _gw.ExecuteAsync(s, new(AgentAction.Read), default);
        Assert.True(read.Ok, read.Message);
    }

    [Fact]
    public async Task Watching_an_agent_is_the_persons_own_choice_and_moves_the_window_only_then()
    {
        var mine = await PersonIsReading();
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default);
        Assert.Equal(mine.Id, _k.Active?.Id);

        var agentPage = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        await _k.ActivateAsync(agentPage.Id);   // what the panel's "Show its page" button does

        Assert.Equal(agentPage.Id, _k.Active?.Id);
        Assert.Equal(s.WorkspaceId, _k.ActiveWorkspace);
    }

    [Fact]
    public async Task Opening_a_tab_for_someone_else_never_switches_workspace()
    {
        var mine = await PersonIsReading();
        var other = _k.CreateWorkspace("Elsewhere");
        var t = _k.OpenIn(other.Id, new Uri("https://github.com/"));
        Assert.Equal(other.Id, t.WorkspaceId);
        Assert.Equal(mine.WorkspaceId, _k.ActiveWorkspace);
        Assert.Equal(mine.Id, _k.Active?.Id);
    }

    [Fact]
    public async Task Bringing_the_tab_being_shown_to_life_in_the_background_changes_nothing()
    {
        var mine = await PersonIsReading();
        await _k.EnsureLiveInBackgroundAsync(mine.Id);
        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.True(_leases[mine.Id].IsVisible);
    }

    [Fact]
    public async Task The_live_page_limit_still_holds_for_background_pages()
    {
        await PersonIsReading();
        var s = await _gw.OpenAsync(Manifest(), default);
        for (var i = 0; i < 4; i++) await _gw.ExecuteAsync(s, new(AgentAction.Navigate, $"https://github.com/x/{i}"), default);
        Assert.True(_gw.LiveAgentPages(s) <= 2);
    }
}
