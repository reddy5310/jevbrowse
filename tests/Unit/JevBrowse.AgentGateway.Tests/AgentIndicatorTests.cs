using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// The "an agent is working" indicator is event-driven: the gateway says when a session opens or ends, so nothing polls and nothing runs
/// while nothing changes. These pin the events the indicator depends on.
/// </summary>
public class AgentIndicatorTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;
    private int _changes;

    public AgentIndicatorTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, Path.GetTempPath(), (_, _) => Task.FromResult(false), null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
        _gw.SessionsChanged += () => Interlocked.Increment(ref _changes);
    }

    public void Dispose() => _db.Dispose();

    private static AgentManifest Manifest() => new() { Agent = "Claude Code", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read], MaxLivePages = 2, SessionMinutes = 5 };

    [Fact]
    public async Task Opening_a_session_says_so()
    {
        await _gw.OpenAsync(Manifest(), default);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public async Task Ordinary_actions_are_not_news_so_the_indicator_is_not_redrawn_for_them()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        var before = _changes;
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/x"), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Read), default);
        Assert.Equal(before, _changes);
    }

    [Fact]
    public async Task Stopping_a_session_says_so_once_its_pages_are_released()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/x"), default);
        var before = _changes;

        Assert.True(await _gw.StopAsync(s, default));

        Assert.True(_changes > before);
        Assert.True(s.CleanedUp);
        Assert.True(s.Closed);
    }

    [Fact]
    public async Task Expiry_says_so_too_even_though_nobody_pressed_anything()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        var before = _changes;
        _now = s.ExpiresAt.AddMinutes(1);

        Assert.Equal(1, await _gw.SweepExpiredAsync([s], default));

        Assert.True(_changes > before);
        Assert.True(s.CleanedUp);
    }

    [Fact]
    public async Task A_session_that_is_already_finished_is_not_reported_finished_again()
    {
        var s = await _gw.OpenAsync(Manifest(), default);
        await _gw.StopAsync(s, default);
        var before = _changes;
        await _gw.CloseAsync(s, default);
        Assert.Equal(before, _changes);
    }
}
