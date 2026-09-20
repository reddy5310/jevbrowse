using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// Regression tests for the independent review's release blockers: privacy boundaries, identity transitions,
/// lifecycle serialization, and restore correctness. Each asserts the SYSTEM property, not a component detail.
/// </summary>
public class PrivacyAndLifecycleTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private readonly string _thumbs = Path.Combine(Path.GetTempPath(), "jev-priv-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(2);
    private readonly TabKernel _k;

    public PrivacyAndLifecycleTests()
    {
        Directory.CreateDirectory(_thumbs);
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), _thumbs, () => _now, new WorkspaceRepository(_db));
        _k.Load();
    }

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_thumbs, true); } catch (IOException) { } }

    private string[] ThumbFiles() => Directory.GetFiles(_thumbs);

    private async Task<VirtualTab> Live(string url, ContextId? ws = null)
    {
        if (ws is { } w) await _k.SwitchWorkspaceAsync(w);
        var t = _k.Open(new Uri(url));
        _now += TimeSpan.FromSeconds(1);
        await _k.ActivateAsync(t.Id);
        var l = _leases[t.Id];
        l.ThumbnailToWrite = t.Id + ".png";
        l.ThumbnailDir = _thumbs;
        return t;
    }

    // ---- Review #1: private/sensitive tabs must not write pixels or timelines ----

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Cat", IdentityContainer.Personal, 1)]      // control: public is allowed
    [InlineData("https://netbanking.hdfcbank.com/portal", IdentityContainer.Personal, 0)] // sensitive
    [InlineData("https://en.wikipedia.org/wiki/Cat", IdentityContainer.Private, 0)]       // ephemeral container
    [InlineData("https://en.wikipedia.org/wiki/Cat", IdentityContainer.Disposable, 0)]
    public async Task Switching_away_captures_a_thumbnail_only_when_policy_allows(string url, IdentityContainer c, int expectedFiles)
    {
        var ws = c == IdentityContainer.Personal ? _k.Workspaces[0] : _k.CreateWorkspace(c.ToString(), c);
        var a = await Live(url, ws.Id);
        await Live("https://example.org/next");           // activating another tab hides `a` (deactivation capture)
        Assert.Equal(expectedFiles, ThumbFiles().Length);
    }

    [Fact]
    public async Task Private_workspace_leaves_no_context_checkpoint_and_no_workspace_row()
    {
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        await Live("https://secret-plans.example.org/a", priv.Id);
        await Live("https://secret-plans.example.org/b");
        await _k.SwitchWorkspaceAsync(_k.Workspaces[0].Id);   // records the outgoing workspace's checkpoint

        Assert.DoesNotContain(_k.Timeline(), c => c.WorkspaceName == "Private" || c.Resources.Any(r => r.Url.Host == "secret-plans.example.org"));
        Assert.DoesNotContain(new WorkspaceRepository(_db).LoadAll(), w => w.Name == "Private");
        Assert.Empty(new TabRepository(_db).LoadAll());
        Assert.Empty(ThumbFiles());
        // and the in-memory record for the private workspace does not leak into durable storage when asked directly
        await _k.SwitchWorkspaceAsync(priv.Id);
        _k.RecordContextCheckpoint();
        Assert.DoesNotContain(_k.Timeline(), c => c.WorkspaceName == "Private");
    }

    [Fact]
    public async Task Stricter_class_purges_thumbnail_checkpoint_and_tells_listeners_to_forget()
    {
        var a = await Live("https://intranet.corp.test/report");
        await Live("https://example.org/other");               // hides a → thumbnail allowed and written
        Assert.Single(ThumbFiles());
        await _k.VirtualizeAsync(a.Id, Cause.User);             // checkpoint saved (with the thumbnail path)
        Assert.NotNull(_k.GetCheckpoint(a.Id));
        await _k.ActivateAsync(a.Id);                            // renderer again
        var forgotten = false;
        _k.Changed += e => { if (e.Kind == "policy-tightened" && e.Id == a.Id) forgotten = true; };

        _leases[a.Id].RaiseSignals(PageSignals.PasswordField);  // the page turns out to have a password field

        Assert.Null(_k.GetCheckpoint(a.Id));
        Assert.DoesNotContain(ThumbFiles(), f => Path.GetFileName(f).StartsWith(a.Id.ToString()));  // a's pixels are gone
        Assert.True(forgotten);
    }

    // ---- Review #2: identity is immutable; crossing it creates a new tab ----

    [Fact]
    public async Task Moving_across_identities_opens_a_new_tab_and_removes_every_trace_of_the_old_one()
    {
        var work = _k.CreateWorkspace("Work", IdentityContainer.Work);
        var a = await Live("https://github.com/company/project");
        await _k.VirtualizeAsync(a.Id, Cause.User);
        await _k.ActivateAsync(a.Id);
        Assert.Equal(IdentityContainer.Personal, _leases.Containers[a.Id]);

        var moved = await _k.MoveToWorkspaceAsync(a.Id, work.Id);

        Assert.NotEqual(a.Id, moved.Id);                              // a different resource...
        Assert.DoesNotContain(_k.Tabs, t => t.Id == a.Id);            // ...the original is gone
        Assert.Null(_k.GetCheckpoint(a.Id));
        Assert.DoesNotContain(new TabRepository(_db).LoadAll(), r => r.Id == a.Id);
        Assert.Equal(ResourceState.Virtual, moved.State);
        Assert.Equal(work.Id, moved.WorkspaceId);

        await _k.ActivateAsync(moved.Id);                             // and it renders in the Work profile
        Assert.Equal(IdentityContainer.Work, _leases.Containers[moved.Id]);
        Assert.Equal(work.Id, _leases.IsolationKeys[moved.Id]);
    }

    [Fact]
    public async Task Moving_into_a_private_workspace_deletes_the_durable_copy()
    {
        var priv = _k.CreateWorkspace("Private", IdentityContainer.Private);
        var a = await Live("https://en.wikipedia.org/wiki/Cat");
        Assert.Single(new TabRepository(_db).LoadAll());

        var moved = await _k.MoveToWorkspaceAsync(a.Id, priv.Id);

        Assert.Empty(new TabRepository(_db).LoadAll());              // neither the old row nor a new one exists
        Assert.Equal(IdentityContainer.Private, _k.ContainerOf(moved));
        Assert.Equal(DataClass.Ephemeral, _k.ClassOf(moved));
    }

    [Fact]
    public async Task Same_container_move_keeps_identity_and_ephemeral_workspaces_never_share_one()
    {
        var w1 = _k.CreateWorkspace("W1", IdentityContainer.Work);
        var w2 = _k.CreateWorkspace("W2", IdentityContainer.Work);
        var t = await Live("https://a.example.org", w1.Id);
        Assert.Equal(t.Id, (await _k.MoveToWorkspaceAsync(t.Id, w2.Id)).Id);       // Work→Work: same profile, same tab

        var p1 = _k.CreateWorkspace("P1", IdentityContainer.Private);
        var p2 = _k.CreateWorkspace("P2", IdentityContainer.Private);
        var pt = await Live("https://b.example.org", p1.Id);
        Assert.NotEqual(pt.Id, (await _k.MoveToWorkspaceAsync(pt.Id, p2.Id)).Id);  // each private workspace is its own profile
    }

    [Fact]
    public void Workspace_container_is_persisted_at_creation_and_ephemeral_workspaces_are_not_persisted()
    {
        _k.CreateWorkspace("Work", IdentityContainer.Work);
        _k.CreateWorkspace("Private", IdentityContainer.Private);
        _k.CreateWorkspace("Agent 1", IdentityContainer.Disposable);

        var k2 = new TabKernel(new FakeLeaseManager(), new TabRepository(_db), new CheckpointRepository(_db), _thumbs, null, new WorkspaceRepository(_db));
        k2.Load();
        Assert.Contains(k2.Workspaces, w => w.Name == "Work" && w.Container == IdentityContainer.Work);
        Assert.DoesNotContain(k2.Workspaces, w => w.Name is "Private" or "Agent 1");
    }

    // ---- Review #7: lifecycle operations are serialized ----

    [Fact]
    public async Task Concurrent_activations_of_one_tab_acquire_exactly_one_renderer()
    {
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(40);
        var t = _k.Open(new Uri("https://a.example.org"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _k.ActivateAsync(t.Id)));
        Assert.Equal(1, _leases.Acquires);
        Assert.Equal(1, _leases.MaxConcurrentAcquires);
        Assert.Equal(1, _k.LiveCount);
    }

    [Fact]
    public async Task Interleaved_activate_virtualize_close_never_leave_a_tab_and_its_renderer_disagreeing()
    {
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(15);
        _leases.MaxLive = 3;
        var tabs = Enumerable.Range(0, 8).Select(i => _k.Open(new Uri($"https://s{i}.example.org"))).ToList();
        var rng = new Random(11);
        var ops = new List<Task>();
        for (int i = 0; i < 60; i++)
        {
            var t = tabs[rng.Next(tabs.Count)];
            ops.Add(rng.Next(4) switch
            {
                0 => Safe(() => _k.VirtualizeAsync(t.Id, Cause.User)),
                1 => Safe(() => _k.VirtualizeAsync(t.Id, Cause.Scheduler)),
                _ => Safe(() => _k.ActivateAsync(t.Id)),
            });
        }
        await Task.WhenAll(ops);
        foreach (var t in _k.Tabs)
            Assert.Equal(t.State.HasLiveRenderer(), _leases.TryGet(t.Id, out _));
        Assert.Equal(_k.Tabs.Count(t => t.State.HasLiveRenderer()), _leases.LiveResources.Count);
        Assert.True(_leases.MaxConcurrentAcquires <= 1);

        static async Task Safe(Func<Task> f) { try { await f(); } catch (KeyNotFoundException) { } }
    }

    // ---- Review #8: restore correctness and hibernation safety ----

    [Fact]
    public async Task An_older_checkpoint_never_overrides_newer_navigation_after_restart()
    {
        var a = await Live("https://docs.example.org/one");
        _leases[a.Id].ScrollY = 500;
        await _k.VirtualizeAsync(a.Id, Cause.User);              // checkpoint of /one
        await _k.ActivateAsync(a.Id);
        _leases[a.Id].RaiseNavigation(new Uri("https://docs.example.org/two"), "Two");   // user navigates on

        var leases2 = new FakeLeaseManager();
        var k2 = new TabKernel(leases2, new TabRepository(_db), new CheckpointRepository(_db), _thumbs, null, new WorkspaceRepository(_db));
        k2.Load();
        await k2.ActivateAsync(k2.Tabs.Single().Id);

        Assert.Equal("https://docs.example.org/two", leases2[k2.Tabs.Single().Id].Url.ToString());
        Assert.Null(leases2[k2.Tabs.Single().Id].Applied);       // scroll of a different page is not applied
    }

    [Fact]
    public async Task Failed_commit_rolls_the_model_back_and_keeps_the_renderer()
    {
        var a = await Live("https://docs.example.org/one");
        _db.Exec("DROP TABLE checkpoints");                       // make the durable write fail
        var r = await _k.VirtualizeAsync(a.Id, Cause.User);

        Assert.False(r.Allowed);
        Assert.StartsWith("commit_failed", r.Reason);
        Assert.Equal(ResourceState.Hot, a.State);                 // the model does not claim Virtual
        Assert.True(_leases.TryGet(a.Id, out _));                 // and the renderer is still there
    }

    [Fact]
    public async Task Shutdown_checkpoint_saves_live_pages_without_disposing_them_and_skips_secret_ones()
    {
        var pub = await Live("https://docs.example.org/long");
        _leases[pub.Id].ScrollY = 777;
        var secret = await Live("https://docs.example.org/login");
        _leases[secret.Id].RaiseSignals(PageSignals.PasswordField);

        var n = await _k.CheckpointAllAsync();

        Assert.Equal(1, n);
        Assert.Equal(777, _k.GetCheckpoint(pub.Id)!.ScrollY);
        Assert.Null(_k.GetCheckpoint(secret.Id));
        Assert.Equal(2, _k.LiveCount);                            // nothing was disposed
    }

    // ---- Review #10: admission decisions are explainable ----

    [Fact]
    public async Task Foreground_admission_records_why_a_tab_was_evicted()
    {
        _leases.MaxLive = 2;
        var a = await Live("https://a.example.org");
        await Live("https://b.example.org");
        await Live("https://c.example.org");                      // over budget → a is evicted
        var d = _k.LastDecision(a.Id);
        Assert.NotNull(d);
        Assert.Equal("foreground_admission", d!.Reasons["trigger"]);
        Assert.Equal("no", d.Reasons["jev_consulted"]);
    }
}
