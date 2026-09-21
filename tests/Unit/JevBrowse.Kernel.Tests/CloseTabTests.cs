using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class CloseTabTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public CloseTabTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Closing_the_front_tab_selects_another_tab_of_the_same_workspace_never_a_private_or_agent_one()
    {
        var mine1 = _k.Open(new Uri("https://example.org/1"));
        var mine2 = _k.Open(new Uri("https://example.org/2"));
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        var secret = _k.OpenIn(priv.Id, new Uri("https://example.net/private"));   // the LAST tab overall belongs to another workspace
        await _k.ActivateAsync(mine2.Id);

        var next = await _k.CloseAndSelectNextAsync(mine2.Id);

        Assert.Equal(mine1.Id, next?.Id);
        Assert.Equal(mine1.Id, _k.Active?.Id);
        Assert.Equal(mine1.WorkspaceId, _k.ActiveWorkspace);
        Assert.NotEqual(secret.WorkspaceId, _k.ActiveWorkspace);
    }

    [Fact]
    public async Task Closing_the_last_tab_of_a_workspace_does_not_open_a_tab_from_another_workspace()
    {
        var only = _k.Open(new Uri("https://example.org/only"));
        var work = _k.CreateWorkspace("Work", IdentityContainer.Work);
        _k.OpenIn(work.Id, new Uri("https://example.net/work"));
        await _k.ActivateAsync(only.Id);

        var next = await _k.CloseAndSelectNextAsync(only.Id);

        Assert.Null(next);
        Assert.Null(_k.Active);
        Assert.Equal(only.WorkspaceId, _k.ActiveWorkspace);
    }

    [Fact]
    public async Task Closing_a_tab_that_is_not_in_front_leaves_the_front_tab_alone()
    {
        var a = _k.Open(new Uri("https://example.org/a"));
        var b = _k.Open(new Uri("https://example.org/b"));
        await _k.ActivateAsync(a.Id);

        Assert.Null(await _k.CloseAndSelectNextAsync(b.Id));

        Assert.Equal(a.Id, _k.Active?.Id);
    }
}
