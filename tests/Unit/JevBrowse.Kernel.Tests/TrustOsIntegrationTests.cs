using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class TrustOsIntegrationTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new();
    private readonly string _thumbs = Path.Combine(Path.GetTempPath(), "jev-thumbs-" + Guid.NewGuid().ToString("N"));
    private readonly TabKernel _k;

    public TrustOsIntegrationTests()
    {
        Directory.CreateDirectory(_thumbs);
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), _thumbs, null, new WorkspaceRepository(_db));
        _k.Load();
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_thumbs, true); } catch (IOException) { } }

    private async Task<VirtualTab> Live(string url, IdentityContainer container = IdentityContainer.Personal)
    {
        var ws = _k.Workspaces.FirstOrDefault(w => w.Container == container) ?? _k.CreateWorkspace(container.ToString());
        ws.Container = container;
        await _k.SwitchWorkspaceAsync(ws.Id);
        var t = _k.Open(new Uri(url));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].ThumbnailToWrite = t.Id + ".png";
        return t;
    }

    [Fact]
    public async Task Public_page_keeps_checkpoint_and_thumbnail()
    {
        var t = await Live("https://en.wikipedia.org/wiki/Cat");
        Assert.Equal(DataClass.Public, _k.ClassOf(t));
        await _k.VirtualizeAsync(t.Id, Cause.User);
        var cp = _k.GetCheckpoint(t.Id);
        Assert.NotNull(cp?.ThumbnailPath);
        Assert.True(File.Exists(cp!.ThumbnailPath));
    }

    [Fact]
    public async Task Sensitive_page_keeps_scroll_but_thumbnail_is_deleted()
    {
        var t = await Live("https://netbanking.hdfcbank.com/portal");
        Assert.Equal(DataClass.Sensitive, _k.ClassOf(t));
        await _k.VirtualizeAsync(t.Id, Cause.User);
        var cp = _k.GetCheckpoint(t.Id);
        Assert.NotNull(cp);
        Assert.Null(cp!.ThumbnailPath);
        Assert.Empty(Directory.GetFiles(_thumbs));
        Assert.Single(new TabRepository(_db).LoadAll()); // URL/title still durable
    }

    [Fact]
    public async Task Password_field_makes_page_secret_and_nothing_but_the_row_survives()
    {
        var t = await Live("https://github.com/login");
        _leases[t.Id].RaiseSignals(PageSignals.PasswordField);
        Assert.Equal(DataClass.Secret, _k.ClassOf(t));
        await _k.VirtualizeAsync(t.Id, Cause.User);
        Assert.Null(_k.GetCheckpoint(t.Id));
        Assert.Empty(Directory.GetFiles(_thumbs));
        Assert.Single(new TabRepository(_db).LoadAll());
    }

    [Fact]
    public async Task Private_container_leaves_zero_durable_trace()
    {
        var t = await Live("https://en.wikipedia.org/wiki/Cat", IdentityContainer.Private);
        Assert.Equal(DataClass.Ephemeral, _k.ClassOf(t));
        Assert.Equal(IdentityContainer.Private, _leases.Containers[t.Id]);
        _leases[t.Id].RaiseNavigation(new Uri("https://en.wikipedia.org/wiki/Dog"), "Dog");
        await _k.VirtualizeAsync(t.Id, Cause.User);
        Assert.Empty(new TabRepository(_db).LoadAll());
        Assert.Null(_k.GetCheckpoint(t.Id));
        Assert.Empty(Directory.GetFiles(_thumbs));
        Assert.Contains(t, _k.Tabs); // still usable in this session
    }

    [Fact]
    public async Task Renderer_container_follows_workspace()
    {
        var a = await Live("https://a.test", IdentityContainer.Work);
        var b = await Live("https://b.test", IdentityContainer.Dev);
        Assert.Equal(IdentityContainer.Work, _leases.Containers[a.Id]);
        Assert.Equal(IdentityContainer.Dev, _leases.Containers[b.Id]);
    }
}
