using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// Two review findings: a saved Back/Forward history must never carry an earlier Sensitive address to disk just because the page the tab ended on is ordinary, and a deleted
/// workspace must stay deleted (its Time Travel records go, and a stale one cannot bring it back under a different identity).
/// </summary>
public class ReleaseBlockerRegressionTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 6 };
    private readonly Dictionary<string, DataClass> _overrides = [];
    private readonly TabKernel _k;

    public ReleaseBlockerRegressionTests()
    {
        _k = Make(_leases);
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    private TabKernel Make(FakeLeaseManager leases) =>
        new(leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db),
            classifier: new DataClassifier(h => _overrides.TryGetValue(h, out var c) ? c : null));

    private TabKernel Restarted()
    {
        var k = Make(new FakeLeaseManager());
        k.Load();
        return k;
    }

    private static HistoryEntry E(string url, string title) => new(url, title);

    private async Task<VirtualTab> SleepWithHistory(string currentUrl, params HistoryEntry[] entries)
    {
        var t = _k.Open(new Uri(currentUrl));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].HistoryToReport = new NavHistory(entries, entries.Length - 1);
        var other = _k.Open(new Uri("https://example.org/other"));
        await _k.ActivateAsync(other.Id);
        Assert.True((await _k.VirtualizeAsync(t.Id, Cause.User)).Allowed);
        return t;
    }

    // ---- finding 1 ----

    [Fact]
    public async Task A_Sensitive_page_earlier_in_the_history_keeps_the_whole_history_off_disk_even_though_the_current_page_is_ordinary()
    {
        var t = await SleepWithHistory("https://example.org/article",
            E("https://netbanking.hdfcbank.com/statement", "My account"), E("https://example.org/article", "An article"));

        Assert.Null(_k.GetCheckpoint(t.Id)?.History);                       // nothing of the bank visit is in history_json
        Assert.Null(Restarted().GetCheckpoint(t.Id)?.History);              // …and after a restart
        await _k.ActivateAsync(t.Id);
        Assert.NotNull(_leases[t.Id].Seeded);                               // still useful while the app runs
    }

    [Fact]
    public async Task An_all_ordinary_history_is_still_saved()
    {
        var t = await SleepWithHistory("https://example.org/b", E("https://example.org/a", "a"), E("https://example.org/b", "b"));
        Assert.NotNull(Restarted().GetCheckpoint(t.Id)?.History);
    }

    [Fact]
    public async Task A_site_marked_Sensitive_after_the_visit_removes_the_saved_history_that_mentions_it()
    {
        var t = await SleepWithHistory("https://example.org/b", E("https://clinic.example.net/records", "records"), E("https://example.org/b", "b"));
        Assert.NotNull(_k.GetCheckpoint(t.Id)?.History);                    // ordinary when it was saved

        _overrides["clinic.example.net"] = DataClass.Sensitive;             // the person tightens it later (the badge dialog)
        _k.ReapplyPolicy();

        Assert.Null(_k.GetCheckpoint(t.Id)?.History);
        Assert.Null(Restarted().GetCheckpoint(t.Id)?.History);
    }

    [Fact]
    public async Task A_history_containing_an_unparseable_address_is_treated_as_not_safe_to_save()
    {
        var t = await SleepWithHistory("https://example.org/b", E("not a url", "?"), E("https://example.org/b", "b"));
        Assert.Null(_k.GetCheckpoint(t.Id)?.History);
    }

    // ---- finding 2 ----

    [Fact]
    public async Task A_deleted_Work_workspace_stays_deleted_its_timeline_is_gone_and_an_old_checkpoint_cannot_bring_it_back_as_Personal()
    {
        var work = _k.CreateWorkspace("Work stuff", IdentityContainer.Work);
        var tab = _k.OpenIn(work.Id, new Uri("https://example.org/company"));
        await _k.SwitchWorkspaceAsync(work.Id);
        await _k.ActivateAsync(tab.Id);
        var old = _k.RecordContextCheckpoint();                             // a Time Travel record made while the workspace existed
        Assert.Contains(_k.Timeline(), c => c.WorkspaceId == work.Id);

        await _k.DeleteWorkspaceAsync(work.Id, ContextId.Default);

        Assert.DoesNotContain(_k.Timeline(), c => c.WorkspaceId == work.Id);
        var again = Restarted();                                            // after a restart
        Assert.DoesNotContain(again.Workspaces, w => w.Id == work.Id);
        Assert.DoesNotContain(again.Timeline(), c => c.WorkspaceId == work.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => again.RestoreContextAsync(old));   // a stale record held by a caller is refused
        Assert.DoesNotContain(again.Workspaces, w => w.Id == work.Id);      // …and nothing was recreated, as Personal or anything else
        Assert.DoesNotContain(again.Tabs, t => t.Id == tab.Id);
    }

    [Fact]
    public async Task Restoring_the_timeline_of_a_workspace_that_still_exists_still_works()
    {
        var work = _k.CreateWorkspace("Work stuff", IdentityContainer.Work);
        var a = _k.OpenIn(work.Id, new Uri("https://example.org/a"));
        await _k.SwitchWorkspaceAsync(work.Id);
        await _k.ActivateAsync(a.Id);
        var cp = _k.RecordContextCheckpoint();
        await _k.CloseAsync(a.Id);

        var recreated = await _k.RestoreContextAsync(cp);

        Assert.Equal(1, recreated);
        Assert.Equal(IdentityContainer.Work, _k.Workspaces.Single(w => w.Id == work.Id).Container);   // the identity is the workspace's own
    }
}
