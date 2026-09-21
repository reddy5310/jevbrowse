using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>When an agent session is over its workspace is retired: nothing in it can be selected again, whichever ephemeral container it had.</summary>
public class RetiredAgentWorkspaceTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public RetiredAgentWorkspaceTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(IdentityContainer.Disposable)]
    [InlineData(IdentityContainer.Private)]
    public async Task An_ended_agent_workspace_and_its_pages_can_no_longer_be_selected(IdentityContainer container)
    {
        var mine = _k.Open(new Uri("https://example.org/mine"));
        await _k.ActivateAsync(mine.Id);
        var ws = _k.CreateWorkspace("Agent · s1", container);
        var page = _k.OpenIn(ws.Id, new Uri("https://example.org/agent"));
        await _k.ActivateAsync(page.Id); await _k.VirtualizeAsync(page.Id, Cause.User);   // the agent's session ended: its page is virtual
        await _k.ActivateAsync(mine.Id);

        await _k.EndPrivateSessionAsync(ws.Id, _k.ActiveWorkspace);

        Assert.DoesNotContain(_k.Workspaces, w => w.Id == ws.Id);
        Assert.DoesNotContain(_k.Tabs, t => t.Id == page.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => _k.ActivateAsync(page.Id));   // stale references fail cleanly (the window catches it)
        Assert.Equal(mine.Id, _k.Active?.Id);                                      // the person's page was never disturbed
        await _k.EndPrivateSessionAsync(ws.Id, _k.ActiveWorkspace);                // idempotent
    }

    [Fact]
    public async Task An_ordinary_workspace_can_still_never_be_ended_this_way()
    {
        var work = _k.CreateWorkspace("Work", IdentityContainer.Work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _k.EndPrivateSessionAsync(work.Id, ContextId.Default));
    }
}
