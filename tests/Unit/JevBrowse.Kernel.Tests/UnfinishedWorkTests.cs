using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>Anything a page reports as unfinished work must stop the scheduler from putting the tab to sleep, and must be named to the person.</summary>
public class UnfinishedWorkTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public UnfinishedWorkTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    [Fact]
    public void Every_protection_reason_is_named_to_the_person()
    {
        foreach (var f in Enum.GetValues<ProtectionFlags>().Where(f => f != ProtectionFlags.None))
            Assert.NotEmpty(ProtectionPhrases.StayAwake(f));   // a new flag without words would keep a tab awake for no stated reason
    }

    [Theory]
    [InlineData(ProtectionFlags.DirtyForm)]
    [InlineData(ProtectionFlags.UploadActive)]
    [InlineData(ProtectionFlags.VideoPlaying)]
    [InlineData(ProtectionFlags.DownloadActive)]
    [InlineData(ProtectionFlags.Audible)]
    public async Task A_tab_with_unfinished_work_is_not_put_to_sleep_automatically_but_the_person_still_can(ProtectionFlags reported)
    {
        var t = _k.Open(new Uri("https://example.org/work"));
        await _k.ActivateAsync(t.Id);
        var other = _k.Open(new Uri("https://example.org/other"));
        await _k.ActivateAsync(other.Id);                                  // t is now in the background
        _leases[t.Id].RaiseDetected(reported);                           // what the page script reported

        var automatic = await _k.VirtualizeAsync(t.Id, Cause.Scheduler);
        Assert.False(automatic.Allowed);
        Assert.True(_leases.TryGet(t.Id, out _));

        var explicitly = await _k.VirtualizeAsync(t.Id, Cause.User);      // an explicit request overrides, after the app has warned
        Assert.True(explicitly.Allowed);
    }
}
