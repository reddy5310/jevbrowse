using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.Kernel.Tests;

/// <summary>
/// Work done in the background (an agent's page coming live) shares the renderer pool with the person. It may use spare capacity and
/// it may release pages the person is not looking at, but it must never cost them the page in front of them, never push the pool over
/// budget, and never present itself as if it were their own restore.
/// </summary>
public class BackgroundAdmissionTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 2 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly List<KernelEvent> _events = [];

    public BackgroundAdmissionTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _k.Changed += _events.Add;
    }

    public void Dispose() => _db.Dispose();

    private VirtualTab Other(string host) => _k.OpenIn(_k.CreateWorkspace("agent " + host).Id, new Uri($"https://{host}/"));

    private async Task<VirtualTab> PersonReads(string host = "reading.example")
    {
        var t = _k.Open(new Uri($"https://{host}/"));
        await _k.ActivateAsync(t.Id);
        return t;
    }

    [Fact]
    public async Task With_only_the_active_page_live_and_the_pool_full_background_work_is_refused_and_the_page_stays()
    {
        _leases.MaxLive = 1;
        var mine = await PersonReads();
        var agent = Other("agent.example");

        var admitted = await _k.EnsureLiveInBackgroundAsync(agent.Id);

        Assert.False(admitted);
        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.True(mine.State.HasLiveRenderer());
        Assert.True(_leases[mine.Id].IsVisible);
        Assert.False(_leases.TryGet(agent.Id, out _));
        Assert.Single(_leases.LiveResources);
        Assert.Contains(_events, e => e.Kind == "background-refused" && e.Background);
    }

    [Fact]
    public async Task Background_work_may_release_a_page_the_person_is_not_looking_at_but_never_the_one_they_are()
    {
        var reading = await PersonReads("a.example");
        var elsewhere = _k.Open(new Uri("https://b.example/"));
        await _k.ActivateAsync(elsewhere.Id);
        await _k.ActivateAsync(reading.Id);                   // both live; the person is on "reading"
        Assert.Equal(2, _leases.LiveResources.Count);
        var agent = Other("agent.example");

        Assert.True(await _k.EnsureLiveInBackgroundAsync(agent.Id));

        Assert.Equal(reading.Id, _k.Active?.Id);
        Assert.True(_leases[reading.Id].IsVisible);
        Assert.True(_leases.TryGet(agent.Id, out _));
        Assert.False(_leases.TryGet(elsewhere.Id, out _), "the page nobody was looking at is the one released");
        Assert.True(_leases.LiveResources.Count <= 2);
    }

    [Fact]
    public async Task A_protected_page_is_not_released_for_background_work_either()
    {
        var reading = await PersonReads("a.example");
        var pinnedAwake = _k.Open(new Uri("https://b.example/"));
        await _k.ActivateAsync(pinnedAwake.Id);
        _k.SetProtection(pinnedAwake.Id, ProtectionFlags.KeepActive);
        await _k.ActivateAsync(reading.Id);
        var agent = Other("agent.example");

        Assert.False(await _k.EnsureLiveInBackgroundAsync(agent.Id));
        Assert.True(_leases.TryGet(pinnedAwake.Id, out _));
        Assert.True(_leases.TryGet(reading.Id, out _));
        Assert.Equal(2, _leases.LiveResources.Count);
    }

    [Fact]
    public async Task If_the_page_to_release_cannot_be_saved_first_background_work_is_refused_not_forced()
    {
        var reading = await PersonReads("a.example");
        var elsewhere = _k.Open(new Uri("https://b.example/"));
        await _k.ActivateAsync(elsewhere.Id);
        await _k.ActivateAsync(reading.Id);
        var agent = Other("agent.example");
        _leases.FailNextCapture = true;

        Assert.False(await _k.EnsureLiveInBackgroundAsync(agent.Id));
        Assert.True(_leases.TryGet(elsewhere.Id, out _), "losing a page's saved place is not worth an agent's request");
        Assert.Equal(2, _leases.LiveResources.Count);
    }

    [Fact]
    public async Task The_persons_own_action_may_still_exceed_the_pool_when_everything_live_is_protected()
    {
        _leases.MaxLive = 1;
        var first = await PersonReads("a.example");
        _k.SetProtection(first.Id, ProtectionFlags.KeepActive);
        var second = _k.Open(new Uri("https://b.example/"));

        await _k.ActivateAsync(second.Id);   // the person asked; their work is never dropped to honour a budget

        Assert.Equal(second.Id, _k.Active?.Id);
        Assert.Equal(2, _leases.LiveResources.Count);
    }

    [Fact]
    public async Task Background_restores_raise_events_marked_background_and_the_persons_do_not()
    {
        var mine = await PersonReads();
        _leases[mine.Id].RaiseLoaded();
        var agent = Other("agent.example");
        Assert.True(await _k.EnsureLiveInBackgroundAsync(agent.Id));
        _leases[agent.Id].RaiseLoaded();

        var mineEvents = _events.Where(e => e.Id == mine.Id && e.Kind is "restoring" or "restored" or "loaded").ToList();
        var agentEvents = _events.Where(e => e.Id == agent.Id && e.Kind is "restoring" or "restored" or "loaded" or "warmed").ToList();
        Assert.NotEmpty(mineEvents);
        Assert.All(mineEvents, e => Assert.False(e.Background));
        Assert.Contains(agentEvents, e => e.Kind == "restoring");
        Assert.All(agentEvents, e => Assert.True(e.Background, $"{e.Kind} for the agent's page must be marked background"));
    }

    [Fact]
    public void A_background_event_never_produces_a_status_message()
    {
        var id = ResourceId.New();
        Assert.NotNull(KernelStatus.For(new KernelEvent("restored", id, "300 ms")));            // the person's own wake-up is news
        Assert.Null(KernelStatus.For(new KernelEvent("restored", id, "300 ms", Background: true)));   // an agent's is not
    }

    [Fact]
    public async Task A_slow_foreground_restore_finishes_intact_when_background_work_arrives_during_it()
    {
        var gate = new TaskCompletionSource();
        var mine = _k.Open(new Uri("https://slow.example/"));
        var agent = Other("agent.example");
        _leases.AcquireGate = (id, _) => id == mine.Id ? gate.Task : Task.CompletedTask;

        var activation = _k.ActivateAsync(mine.Id);               // slow: held open
        var background = _k.EnsureLiveInBackgroundAsync(agent.Id); // arrives while it is in flight
        await Task.Delay(50);
        Assert.False(activation.IsCompleted);
        Assert.False(background.IsCompleted, "the kernel does one thing at a time; the agent waits its turn");
        gate.SetResult();
        await activation; Assert.True(await background);

        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.True(_leases[mine.Id].IsVisible);
        Assert.False(_leases[agent.Id].IsVisible);
        Assert.Equal([mine.Id, agent.Id], _leases.AcquireLog.Select(a => a.Id).ToArray());   // the person's restore began first
        Assert.Equal(RenderIntent.Foreground, _leases.AcquireLog[0].Intent);
        Assert.Equal(RenderIntent.Background, _leases.AcquireLog[1].Intent);
        // Every restore event about the person's page is theirs, with no background marker mixed in.
        Assert.All(_events.Where(e => e.Id == mine.Id && e.Kind == "restoring"), e => Assert.False(e.Background));
    }

    [Fact]
    public async Task A_slow_background_restore_never_takes_over_or_evicts_when_the_person_then_switches_pages()
    {
        var gate = new TaskCompletionSource();
        var first = await PersonReads("a.example");
        var second = _k.Open(new Uri("https://b.example/"));
        var agent = Other("agent.example");
        _leases.AcquireGate = (id, _) => id == agent.Id ? gate.Task : Task.CompletedTask;

        var background = _k.EnsureLiveInBackgroundAsync(agent.Id);
        var switching = _k.ActivateAsync(second.Id);               // the person switches while the agent's page is still coming up
        await Task.Delay(50);
        gate.SetResult();
        Assert.True(await background);
        await switching;

        Assert.Equal(second.Id, _k.Active?.Id);
        Assert.True(_leases[second.Id].IsVisible);
        // The person's own action outranks the agent's background page: if the pool needed room, the agent's older page is the one released.
        if (_leases.TryGet(agent.Id, out var agentLease)) Assert.False(agentLease.IsVisible);
        Assert.True(_leases.LiveResources.Count <= 2, "the pool held its budget throughout");
    }

    [Fact]
    public async Task Several_background_requests_at_once_never_exceed_the_pool_or_touch_the_active_page()
    {
        _leases.MaxLive = 3;
        var mine = await PersonReads();
        var agents = Enumerable.Range(0, 6).Select(i => Other($"agent{i}.example")).ToList();

        var results = await Task.WhenAll(agents.Select(a => _k.EnsureLiveInBackgroundAsync(a.Id)));

        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.True(_leases[mine.Id].IsVisible);
        Assert.True(_leases.LiveResources.Count <= 3);
        Assert.Contains(true, results);
        Assert.Contains(_leases.LiveResources, id => id == mine.Id);
    }
}
