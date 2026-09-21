using JevBrowse.Brain;
using JevBrowse.Brain.Tests;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Memory;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Memory.Tests;

public class BrowserMemoryTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(30);
    public void Dispose() => _db.Dispose();

    private static string Article(string topic, int words = 120) =>
        string.Join(' ', Enumerable.Range(0, words).Select(i => i % 7 == 0 ? topic : "filler")) + " " + topic + " renderer process model explained in depth.";

    [Fact]
    public void ForgetSite_removes_every_page_of_that_host_and_only_that_host()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        var tab = ResourceId.New();
        m.Index(tab, new Uri("https://news.example.org/a"), "A", ContextId.Default, Article("alpha"));
        m.Index(tab, new Uri("https://news.example.org/b"), "B", ContextId.Default, Article("alpha"));
        m.Index(ResourceId.New(), new Uri("https://other.example.org/c"), "C", ContextId.Default, Article("alpha"));

        Assert.Equal(2, m.ForgetSite("News.Example.org."));

        Assert.Equal(1, m.Stats().Docs);
        Assert.Single(m.Search("alpha"));
    }

    [Fact]
    public void Index_and_search_roundtrip_with_snippet()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        var id = ResourceId.New();
        m.Index(id, new Uri("https://learn.microsoft.com/webview2/process-model"), "WebView2 process model", ContextId.Default, Article("WebView2"));
        m.Index(ResourceId.New(), new Uri("https://example.org/cats"), "Cats", ContextId.Default, Article("cats"));
        var hits = m.Search("webview2 renderer");
        Assert.Single(hits);
        Assert.Equal(id, hits[0].Id);
        Assert.Contains("WebView2", hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((2, hits[0].Url.ToString().Length > 0 ? m.Stats().Bytes : 0), (m.Stats().Docs, m.Stats().Bytes));
    }

    [Fact]
    public void Text_relevance_outranks_recency_and_scores_are_positive()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        var strongOld = ResourceId.New(); var weakNew = ResourceId.New();
        _now = DateTimeOffset.UnixEpoch.AddDays(1);
        m.Index(strongOld, new Uri("https://a.test/kernel"), "Kernel scheduler internals", ContextId.Default,
            string.Join(' ', Enumerable.Repeat("The kernel scheduler decides which kernel thread runs; the scheduler quantum and scheduler queues matter.", 12)));
        _now = DateTimeOffset.UnixEpoch.AddDays(30);   // much newer
        m.Index(weakNew, new Uri("https://b.test/notes"), "Weekly notes", ContextId.Default,
            string.Join(' ', Enumerable.Repeat("Assorted filler about lunch plans and travel", 30)) + " one mention of the kernel scheduler at the end.");

        var hits = m.Search("kernel scheduler");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Score > 0));
        Assert.Equal(strongOld, hits[0].Id);          // relevance decides; the recency boost is only a tiebreaker-sized nudge
    }

    [Fact]
    public void Prefix_and_diacritics_and_short_text_rules()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        m.Index(ResourceId.New(), new Uri("https://a.test"), "Scheduling", ContextId.Default, Article("schéduling"));
        Assert.Single(m.Search("sched"));            // prefix on last token
        Assert.Single(m.Search("scheduling"));       // diacritics removed
        m.Index(ResourceId.New(), new Uri("https://b.test"), "Tiny", ContextId.Default, "too short to matter");
        Assert.Equal(1, m.Stats().Docs);             // <200 chars is not indexed
    }

    [Fact]
    public void Recency_and_workspace_boost_reorder_equal_matches()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        var ws = ContextId.New();
        var old = ResourceId.New(); var fresh = ResourceId.New(); var mine = ResourceId.New();
        _now = DateTimeOffset.UnixEpoch.AddDays(1); m.Index(old, new Uri("https://old.test"), "Kernel", ContextId.Default, Article("kernel"));
        _now = DateTimeOffset.UnixEpoch.AddDays(30); m.Index(fresh, new Uri("https://fresh.test"), "Kernel", ContextId.Default, Article("kernel"));
        m.Index(mine, new Uri("https://mine.test"), "Kernel", ws, Article("kernel"));
        var hits = m.Search("kernel", workspace: ws);
        Assert.Equal(mine, hits[0].Id);
        Assert.Equal(fresh, hits[1].Id);
        Assert.Equal(old, hits[2].Id);
    }

    [Fact]
    public void Disk_budget_drops_oldest()
    {
        var m = new BrowserMemory(_db, budgetBytes: 3000, clock: () => _now);
        for (int i = 0; i < 10; i++) { _now = _now.AddMinutes(1); m.Index(ResourceId.New(), new Uri($"https://p{i}.test"), $"Page {i}", ContextId.Default, Article($"topic{i}", 150)); }
        var (docs, bytes) = m.Stats();
        Assert.True(bytes <= 3000, $"bytes {bytes}");
        Assert.True(docs < 10);
        Assert.Empty(m.Search("topic0"));           // oldest gone
        Assert.Single(m.Search("topic9"));          // newest kept
    }

    [Fact]
    public async Task Rerank_uses_brain_and_falls_back_when_denied()
    {
        var m = new BrowserMemory(_db, clock: () => _now);
        var ids = Enumerable.Range(0, 4).Select(_ => ResourceId.New()).ToList();
        for (int i = 0; i < 4; i++) m.Index(ids[i], new Uri($"https://r{i}.test"), $"Doc {i}", ContextId.Default, Article("rerank"));
        var local = m.Search("rerank");

        var jev = new FakeProvider(Provider.Jev) { Reply = "4, 3, 2, 1" };
        var on = new BrainRouter(new DefaultTrustPolicy(), new BrainPolicy { AiEnabled = true, CloudEnabled = true }, [jev]);
        var reranked = await m.SearchWithRerankAsync("rerank", on, null, default);
        Assert.Equal(local[3].Id, reranked[0].Id);
        Assert.DoesNotContain("https://", jev.Received.Single()); // only titles/snippets went out, no URLs

        var off = new BrainRouter(new DefaultTrustPolicy(), new BrainPolicy(), [jev]);
        var fallback = await m.SearchWithRerankAsync("rerank", off, null, default);
        Assert.Equal(local.Select(h => h.Id), fallback.Select(h => h.Id));
    }
}

public class MemoryIndexerTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new();
    private readonly TabKernel _k;
    private readonly BrowserMemory _memory;
    private readonly MemoryIndexer _indexer;

    public MemoryIndexerTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), null, new WorkspaceRepository(_db));
        _k.Load();
        _memory = new BrowserMemory(_db);
        _indexer = new MemoryIndexer(_k, _leases, _memory);
    }

    public void Dispose() => _db.Dispose();

    private async Task<VirtualTab> Open(string url, IdentityContainer c = IdentityContainer.Personal)
    {
        var ws = _k.Workspaces.FirstOrDefault(w => w.Container == c) ?? _k.CreateWorkspace(c.ToString(), c);
        await _k.SwitchWorkspaceAsync(ws.Id);
        var t = _k.Open(new Uri(url));
        await _k.ActivateAsync(t.Id);
        _leases[t.Id].ReadableText = string.Join(' ', Enumerable.Repeat("readable article text about browsers", 30));
        return t;
    }

    [Fact]
    public async Task Public_pages_are_indexed_on_load()
    {
        var t = await Open("https://en.wikipedia.org/wiki/Web_browser");
        Assert.True(await _indexer.IndexAsync(t.Id));
        Assert.Single(_memory.Search("browsers"));
        Assert.Equal(1, _indexer.Indexed);
    }

    [Theory]
    [InlineData("https://mail.google.com/mail/", IdentityContainer.Personal)]      // authenticated
    [InlineData("https://netbanking.hdfcbank.com/", IdentityContainer.Personal)]   // sensitive
    [InlineData("https://en.wikipedia.org/wiki/Cat", IdentityContainer.Private)]   // ephemeral
    public async Task Non_public_pages_are_never_indexed(string url, IdentityContainer c)
    {
        var t = await Open(url, c);
        string? reason = null; _indexer.Decided += (_, r) => reason = r;
        Assert.False(await _indexer.IndexAsync(t.Id));
        Assert.Empty(_memory.Search("browsers"));
        Assert.StartsWith("skip:", reason);
    }

    [Fact]
    public async Task Password_field_stops_indexing_even_on_public_site()
    {
        var t = await Open("https://en.wikipedia.org/w/index.php?title=Special:UserLogin");
        _leases[t.Id].RaiseSignals(PageSignals.PasswordField);
        Assert.False(await _indexer.IndexAsync(t.Id));
        Assert.Equal(0, _memory.Stats().Docs);
    }

    [Fact]
    public async Task Closing_a_tab_keeps_what_was_read_until_the_user_clears_it()
    {
        var t = await Open("https://en.wikipedia.org/wiki/Web_browser");
        await _indexer.IndexAsync(t.Id);
        await _k.CloseAsync(t.Id);
        Assert.Equal(1, _memory.Stats().Docs);                    // "where was that article?" must survive closing the tab
        Assert.Single(_memory.Search("browsers"));
        Assert.Equal(1, _memory.Clear());
        Assert.Equal(0, _memory.Stats().Docs);
        Assert.True(_memory.DatabaseFileBytes() > 0);             // the real size is reported, not just the text bytes
    }

    [Fact]
    public async Task Navigating_within_a_tab_adds_pages_instead_of_replacing_them()
    {
        var t = await Open("https://en.wikipedia.org/wiki/Alpha");
        _leases[t.Id].ReadableText = string.Join(' ', Enumerable.Repeat("alpha particles and nuclear decay explained in detail", 20));
        await _indexer.IndexAsync(t.Id);
        _leases[t.Id].RaiseNavigation(new Uri("https://en.wikipedia.org/wiki/Beta"), "Beta");
        _leases[t.Id].ReadableText = string.Join(' ', Enumerable.Repeat("beta testing and release engineering explained in detail", 20));
        await _indexer.IndexAsync(t.Id);

        Assert.Equal(2, _memory.Stats().Docs);
        Assert.Single(_memory.Search("nuclear"));
        Assert.Single(_memory.Search("release"));
    }

    [Fact]
    public async Task A_page_that_changes_while_being_extracted_is_never_committed()
    {
        var t = await Open("https://en.wikipedia.org/wiki/Web_browser");
        var lease = _leases[t.Id];
        lease.ExtractDelay = TimeSpan.FromMilliseconds(20);

        lease.DuringExtract = () => lease.RaiseNavigation(new Uri("https://en.wikipedia.org/wiki/Elsewhere"), "Elsewhere");   // the tab moved on
        Assert.False(await _indexer.IndexAsync(t.Id));

        lease.DuringExtract = () => lease.RaiseSignals(PageSignals.PasswordField);                                             // the page turned out to have a password field
        Assert.False(await _indexer.IndexAsync(t.Id));
        Assert.Equal(0, _memory.Stats().Docs);
    }

    [Fact]
    public async Task Tightening_a_pages_class_removes_it_from_the_index()
    {
        var t = await Open("https://en.wikipedia.org/wiki/Web_browser");
        Assert.True(await _indexer.IndexAsync(t.Id));
        Assert.Equal(1, _memory.Stats().Docs);

        _leases[t.Id].RaiseSignals(PageSignals.Authenticated);    // it was a logged-in page after all
        Assert.Equal(0, _memory.Stats().Docs);
    }

    [Fact]
    public async Task A_page_we_cannot_assess_is_not_indexed_until_it_shows_it_is_public()
    {
        var t = await Open("https://reports.somecompany.example/q3");
        string? reason = null; _indexer.Decided += (_, r) => reason = r;

        Assert.False(await _indexer.IndexAsync(t.Id));             // Not assessed → stays off the index
        Assert.Equal(0, _memory.Stats().Docs);
        Assert.Contains("not assessed", reason);

        // Content rendering with no sign-in affordance changes nothing: it is not evidence the page is public.
        _leases[t.Id].RaiseSignals(PageSignals.ContentRendered);
        Assert.False(await _indexer.IndexAsync(t.Id));
        Assert.Equal(0, _memory.Stats().Docs);

        // A structurally public page is indexed without anyone being asked.
        var wiki = await Open("https://en.wikipedia.org/wiki/Cat");
        Assert.True(await _indexer.IndexAsync(wiki.Id));
        Assert.Equal(1, _memory.Stats().Docs);
    }
}
