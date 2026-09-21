using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>Back and Forward survive a tab going to sleep: the walk is right in every order, and nothing revealing is kept where policy says no.</summary>
public class SleepHistoryTests
{
    private static HistoryEntry E(string n) => new("https://example.org/" + n, n);
    private static NavHistory Saved(int index, params string[] names) => new(names.Select(E).ToList(), index);

    [Fact]
    public void Seeding_splits_the_saved_history_at_the_page_the_tab_woke_on()
    {
        var h = new SleepHistory(); h.Seed(Saved(2, "A", "B", "C", "D"));
        Assert.Equal(["A", "B"], h.Before.Select(x => x.Title));
        Assert.Equal(["D"], h.After.Select(x => x.Title));
    }

    [Fact]
    public void Back_and_Forward_walk_the_whole_history_in_order_by_replacing_the_current_page()
    {
        var h = new SleepHistory(); h.Seed(Saved(2, "A", "B", "C", "D"));
        var cur = E("C");
        cur = h.TakeBack(cur)!; Assert.Equal("B", cur.Title);
        cur = h.TakeBack(cur)!; Assert.Equal("A", cur.Title);
        Assert.Null(h.TakeBack(cur));                                // the start
        cur = h.TakeForward(cur)!; Assert.Equal("B", cur.Title);
        cur = h.TakeForward(cur)!; Assert.Equal("C", cur.Title);
        cur = h.TakeForward(cur)!; Assert.Equal("D", cur.Title);
        Assert.Null(h.TakeForward(cur));                             // the end
    }

    [Fact]
    public void A_new_navigation_ends_Forward_but_keeps_Back()
    {
        var h = new SleepHistory(); h.Seed(Saved(2, "A", "B", "C", "D"));
        h.ClearForward();
        Assert.Empty(h.After);
        Assert.Equal(["A", "B"], h.Before.Select(x => x.Title));
    }

    [Fact]
    public void Live_pages_visited_after_waking_come_after_the_restored_ones_in_the_saved_order()
    {
        // Woke on C with A,B behind; then followed a link to E and went back to C. Live = [C, E], current = C (index 0).
        var h = new SleepHistory(); h.Seed(Saved(2, "A", "B", "C"));
        h.ClearForward();
        var flat = h.Flatten([E("C"), E("E")], 0);
        Assert.Equal(["A", "B", "C", "E"], flat.Entries.Select(x => x.Title));
        Assert.Equal(2, flat.Index);
    }

    [Fact]
    public void After_a_virtual_Back_the_forward_pages_stay_in_true_order_around_the_live_ones()
    {
        // A B C(woke) then E visited: live [C, E]. Back to C (live), Back again (virtual): live becomes [B, E], After = [C].
        var h = new SleepHistory(); h.Seed(Saved(2, "A", "B", "C")); h.ClearForward();
        var cur = h.TakeBack(E("C"))!;
        var flat = h.Flatten([cur, E("E")], 0);
        Assert.Equal(["A", "B", "C", "E"], flat.Entries.Select(x => x.Title));
        Assert.Equal(1, flat.Index);
        Assert.Equal("C", h.TakeForward(cur)!.Title);
    }

    [Fact]
    public void Only_web_addresses_are_kept_and_the_current_index_follows_the_filtering()
    {
        var flat = new SleepHistory().Flatten([new("jev://welcome/", "w"), E("A"), E("B")], 2);
        Assert.Equal(["A", "B"], flat.Entries.Select(x => x.Title));
        Assert.Equal(1, flat.Index);
    }

    [Fact]
    public void A_long_history_keeps_the_most_recent_entries()
    {
        var live = Enumerable.Range(0, 80).Select(i => E("p" + i)).ToList();
        var flat = new SleepHistory().Flatten(live, 79);
        Assert.Equal(SleepHistory.MaxEntries, flat.Entries.Count);
        Assert.Equal("p79", flat.Entries[flat.Index].Title);
    }
}

public class BackForwardAcrossSleepKernelTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 5 };
    private readonly TabKernel _k;
    public BackForwardAcrossSleepKernelTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
    }
    public void Dispose() => _db.Dispose();

    private static NavHistory Hist(string current) => new([new("https://example.org/a", "a"), new(current, "cur"), new("https://example.org/z", "z")], 1);

    private async Task<VirtualTab> SleepAndWake(string url)
    {
        var t = _k.Open(new Uri(url));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].HistoryToReport = Hist(url);
        var other = _k.Open(new Uri("https://example.org/other"));
        await _k.ActivateAsync(other.Id);
        Assert.True((await _k.VirtualizeAsync(t.Id, Cause.User)).Allowed);
        return t;
    }

    [Fact]
    public async Task A_sleeping_tab_wakes_with_its_back_and_forward_history()
    {
        var t = await SleepAndWake("https://example.org/page");
        await _k.ActivateAsync(t.Id);
        var seeded = _leases[t.Id].Seeded;
        Assert.NotNull(seeded);
        Assert.Equal(3, seeded!.Entries.Count);
        Assert.Equal(1, seeded.Index);
    }

    [Fact]
    public async Task The_history_is_kept_on_disk_for_an_ordinary_page_so_it_survives_a_restart()
    {
        var t = await SleepAndWake("https://example.org/page");
        Assert.NotNull(_k.GetCheckpoint(t.Id)?.History);
    }

    [Fact]
    public async Task A_Sensitive_page_keeps_its_history_in_memory_only()
    {
        var t = await SleepAndWake("https://netbanking.hdfcbank.com/home");        // classified Sensitive by its address
        Assert.Null(_k.GetCheckpoint(t.Id)?.History);                              // nothing about where else the person went is written down
        await _k.ActivateAsync(t.Id);
        Assert.NotNull(_leases[t.Id].Seeded);                                      // but waking in the same run still keeps Back and Forward
    }

    [Fact]
    public async Task A_Private_tab_never_writes_history_to_disk_but_keeps_it_while_the_session_lasts()
    {
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        await _k.SwitchWorkspaceAsync(priv.Id);
        var t = _k.Open(new Uri("https://example.org/private-page"));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].HistoryToReport = Hist("https://example.org/private-page");
        var other = _k.Open(new Uri("https://example.org/other"));
        await _k.ActivateAsync(other.Id);
        await _k.VirtualizeAsync(t.Id, Cause.User);
        Assert.Null(_k.GetCheckpoint(t.Id));                                       // no checkpoint row at all
        await _k.ActivateAsync(t.Id);
        Assert.NotNull(_leases[t.Id].Seeded);
    }

    [Fact]
    public async Task A_history_that_does_not_describe_the_page_being_loaded_is_not_applied()
    {
        var t = _k.Open(new Uri("https://example.org/page"));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].HistoryToReport = Hist("https://example.org/somewhere-else");   // stale: the current entry is not the tab's address
        var other = _k.Open(new Uri("https://example.org/other"));
        await _k.ActivateAsync(other.Id);
        await _k.VirtualizeAsync(t.Id, Cause.User);
        await _k.ActivateAsync(t.Id);
        Assert.Null(_leases[t.Id].Seeded);
    }
}
