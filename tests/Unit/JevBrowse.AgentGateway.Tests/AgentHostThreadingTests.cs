using System.Collections.Concurrent;
using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// WebView2 objects belong to the thread that created them (the window's). The agent host's expiry timer fires on a thread-pool thread, and shutting the
/// endpoint down must not block the window's thread while the cleanup it waits for needs that same thread. A single-threaded message pump stands in for the UI thread.
/// </summary>
public sealed class AgentHostThreadingTests : IDisposable
{
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _q = [];
        public readonly Thread Thread;
        public Pump()
        {
            Thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (cb, state) in _q.GetConsumingEnumerable()) cb(state);
            }) { IsBackground = true, Name = "fake-ui" };
            Thread.Start();
        }
        public override void Post(SendOrPostCallback d, object? state) => _q.Add((d, state));
        public Task<T> Run<T>(Func<Task<T>> f)
        {
            var tcs = new TaskCompletionSource<T>();
            Post(async _ => { try { tcs.SetResult(await f()); } catch (Exception ex) { tcs.SetException(ex); } }, null);
            return tcs.Task;
        }
        public void Dispose() => _q.CompleteAdding();
    }

    private readonly Pump _ui = new();
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;

    public AgentHostThreadingTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.Combine(Path.GetTempPath(), "jev-th-" + Guid.NewGuid().ToString("N")), (_, _) => Task.FromResult(false), null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose() { _ui.Dispose(); _db.Dispose(); }

    private static AgentCeiling Ceiling() => new() { Limits = new AgentManifest { Agent = "ceiling", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read], SessionMinutes = 60, MaxLivePages = 2 } };

    /// <summary>The host is created ON the pump thread, the way the window creates it.</summary>
    private Task<(LocalAgentHost Host, AgentSession Session)> StartHostWithLiveSession(TimeSpan? sweep = null) => _ui.Run(async () =>
    {
        var mine = _k.Open(new Uri("https://example.com/"));
        await _k.ActivateAsync(mine.Id);
        var host = new LocalAgentHost(_gw, Ceiling()) { SweepInterval = sweep ?? TimeSpan.FromMinutes(5) };
        var (s, _) = await host.GrantAsync(new AgentManifest { Agent = "Claude Code", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read] });
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/x"), default)).Ok);
        return (host, s);
    });

    private static async Task<bool> WaitUntil(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(20);
        return done();
    }

    [Fact]
    public async Task The_expiry_timer_releases_pages_on_the_owning_thread_not_a_pool_thread()
    {
        var (host, s) = await StartHostWithLiveSession(TimeSpan.FromMilliseconds(40));
        _leases.ReleaseThreads.Clear();
        _ui.Post(_ => host.Start(), null);          // Start on the owner thread, as the window does; the timer then fires on the pool
        _now = s.ExpiresAt.AddMinutes(1);           // the session has run out

        Assert.True(await WaitUntil(() => s.CleanedUp), "the timer never released the expired session's pages");
        Assert.NotEmpty(_leases.ReleaseThreads);
        Assert.All(_leases.ReleaseThreads, id => Assert.Equal(_ui.Thread.ManagedThreadId, id));
        _ui.Post(_ => host.Dispose(), null);
    }

    [Fact]
    public async Task Turning_the_endpoint_off_from_the_owning_thread_does_not_deadlock_it()
    {
        var (host, s) = await StartHostWithLiveSession();
        _leases.ReleaseGate = async () => await Task.Delay(150);   // a release that really takes time and resumes on the captured context
        _ui.Post(_ => host.Start(), null);

        var stopped = _ui.Run(() => { host.Stop(); return Task.FromResult(true); });   // the old Stop() blocked this thread on cleanup that needed this thread
        Assert.True(await Task.WhenAny(stopped, Task.Delay(3000)) == stopped, "the owning thread was blocked");
        var stillResponsive = _ui.Run(() => Task.FromResult(true));
        Assert.True(await Task.WhenAny(stillResponsive, Task.Delay(3000)) == stillResponsive, "the owning thread never came back");

        Assert.True(await WaitUntil(() => s.CleanedUp), "revocation still had to complete after Stop returned");
        _ui.Post(_ => host.Dispose(), null);
    }

    [Fact]
    public async Task StopEndpointAsync_can_be_awaited_from_the_owning_thread()
    {
        var (host, s) = await StartHostWithLiveSession();
        _leases.ReleaseGate = async () => await Task.Delay(100);
        var done = _ui.Run(async () => { await host.StopEndpointAsync(); return s.CleanedUp; });

        Assert.True(await Task.WhenAny(done, Task.Delay(5000)) == done, "StopEndpointAsync did not complete");
        Assert.True(await done);
    }

    [Fact]
    public async Task A_failed_release_is_retried_by_the_next_sweep()
    {
        var (host, s) = await StartHostWithLiveSession(TimeSpan.FromMilliseconds(40));
        _leases.FailNextRelease = true;
        var faults = new ConcurrentQueue<Exception>();
        host.BackgroundFault += faults.Enqueue;
        _ui.Post(_ => host.Start(), null);
        _now = s.ExpiresAt.AddMinutes(1);

        Assert.True(await WaitUntil(() => s.CleanedUp), "a failed release must be retried by the next sweep");
        _ui.Post(_ => host.Dispose(), null);
    }
}
