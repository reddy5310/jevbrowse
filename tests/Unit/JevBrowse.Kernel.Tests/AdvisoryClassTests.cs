using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public class AdvisoryClassTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new();
    private readonly TabKernel _k;

    public AdvisoryClassTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Advisory_raises_but_never_lowers_and_resets_on_navigation()
    {
        var t = _k.Open(new Uri("https://intranet.corp.test/reports"));
        await _k.ActivateAsync(t.Id);
        Assert.Equal(DataClass.Public, _k.ClassOf(t));

        Assert.True(_k.RaiseClass(t.Id, DataClass.Authenticated));
        Assert.Equal(DataClass.Authenticated, _k.ClassOf(t));
        Assert.False(_k.RaiseClass(t.Id, DataClass.Public));          // cannot lower
        Assert.Equal(DataClass.Authenticated, _k.ClassOf(t));

        _leases[t.Id].RaiseSignals(PageSignals.PasswordField);          // deterministic SECRET still wins
        Assert.Equal(DataClass.Secret, _k.ClassOf(t));

        _leases[t.Id].RaiseSignals(PageSignals.None);
        _leases[t.Id].RaiseNavigation(new Uri("https://intranet.corp.test/other"), "Other");
        Assert.Equal(DataClass.Public, _k.ClassOf(t));                 // advisory was for the previous page
    }

    [Fact]
    public async Task Advisory_gates_persistence_like_any_other_class()
    {
        var t = _k.Open(new Uri("https://intranet.corp.test/reports"));
        await _k.ActivateAsync(t.Id);
        _k.RaiseClass(t.Id, DataClass.Sensitive);
        Assert.False(_k.May(t, DataOperation.PersistThumbnail).Allowed);
        Assert.False(_k.May(t, DataOperation.IndexContent).Allowed);
    }
}
