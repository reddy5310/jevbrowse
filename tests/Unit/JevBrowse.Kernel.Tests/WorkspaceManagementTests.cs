using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class WorkspaceManagementTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public WorkspaceManagementTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    private TabKernel Reloaded()
    {
        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        k2.Load();
        return k2;
    }

    [Fact]
    public void Renaming_a_workspace_is_saved_and_touches_nothing_else()
    {
        var w = _k.CreateWorkspace("Reading");
        var t = _k.OpenIn(w.Id, new Uri("https://example.org/a"));

        _k.RenameWorkspace(w.Id, "  Research  ");

        Assert.Equal("Research", Reloaded().Workspaces.Single(x => x.Id == w.Id).Name);      // survives a restart
        Assert.Equal(w.Id, Reloaded().Tabs.Single(x => x.Id == t.Id).WorkspaceId);           // the tab is still in it
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_workspace_cannot_be_given_an_empty_name(string name)
    {
        var w = _k.CreateWorkspace("Reading");
        Assert.Throws<ArgumentException>(() => _k.RenameWorkspace(w.Id, name));
        Assert.Equal("Reading", _k.Workspaces.Single(x => x.Id == w.Id).Name);
    }

    [Fact]
    public void Temporary_sessions_are_not_renamed()
    {
        var p = _k.CreateWorkspace("Private", IdentityContainer.Private);
        Assert.Throws<InvalidOperationException>(() => _k.RenameWorkspace(p.Id, "Mine"));
    }

    [Fact]
    public async Task Deleting_a_workspace_closes_its_tabs_removes_it_and_leaves_everything_else_alone()
    {
        var keep = _k.Open(new Uri("https://example.org/keep"));
        var w = _k.CreateWorkspace("Old project");
        var a = _k.OpenIn(w.Id, new Uri("https://example.org/a"));
        var b = _k.OpenIn(w.Id, new Uri("https://example.org/b"));
        await _k.ActivateAsync(a.Id);                                  // the person is inside it

        await _k.DeleteWorkspaceAsync(w.Id, ContextId.Default);

        Assert.DoesNotContain(_k.Workspaces, x => x.Id == w.Id);
        Assert.DoesNotContain(_k.Tabs, t => t.Id == a.Id || t.Id == b.Id);
        Assert.Contains(_k.Tabs, t => t.Id == keep.Id);
        Assert.Equal(ContextId.Default, _k.ActiveWorkspace);            // landed somewhere sensible, not in the deleted one
        var again = Reloaded();                                         // and it is gone after a restart too
        Assert.DoesNotContain(again.Workspaces, x => x.Id == w.Id);
        Assert.DoesNotContain(again.Tabs, t => t.Id == a.Id);
        Assert.Contains(again.Tabs, t => t.Id == keep.Id);
    }

    [Fact]
    public async Task The_default_workspace_and_temporary_sessions_cannot_be_deleted_this_way()
    {
        var work = _k.CreateWorkspace("Work");
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _k.DeleteWorkspaceAsync(ContextId.Default, work.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _k.DeleteWorkspaceAsync(priv.Id, ContextId.Default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _k.DeleteWorkspaceAsync(work.Id, work.Id));
        Assert.Contains(_k.Workspaces, x => x.Id == work.Id);
    }
}
