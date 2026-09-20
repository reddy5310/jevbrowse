using System.Net.Http.Json;
using System.Text.Json;
using JevBrowse.AgentGateway;
using JevBrowse.Domain;
using JevBrowse.Kernel.Tests;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;
using Xunit;

namespace JevBrowse.AgentGateway.Tests;

/// <summary>
/// Screenshot is its own operation: separately granted, separately budgeted, checked against what is on screen, returned in memory and
/// nowhere else, and never allowed to keep anything (or return anything) from a session that has ended. It must not be achieved by
/// enabling the ordinary thumbnail path, which Trust OS refuses for private and disposable containers.
/// </summary>
public class AgentScreenshotTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 10 };
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
    private readonly TabKernel _k;
    private readonly AgentGateway _gw;
    private readonly string _shotDir = Path.Combine(Path.GetTempPath(), "jev-shots-" + Guid.NewGuid().ToString("N"), "screenshots");

    public AgentScreenshotTests()
    {
        _k = new TabKernel(_leases, new TabRepository(_db), new CheckpointRepository(_db), Path.GetTempPath(), () => _now, new WorkspaceRepository(_db));
        _k.Load();
        _gw = new AgentGateway(_k, _leases, _shotDir, (_, _) => Task.FromResult(false), null, () => _now) { LoadTimeout = TimeSpan.FromMilliseconds(5) };
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_shotDir)!, true); } catch (Exception) { }
    }

    private static AgentManifest Manifest(int shots = 10, params AgentAction[] actions) => new()
    {
        Agent = "Claude Code", AllowDomains = ["github.com"], MaxLivePages = 2, SessionMinutes = 60, MaxScreenshots = shots,
        Actions = actions.Length == 0 ? [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot] : [.. actions],
    };

    private async Task<(AgentSession Session, VirtualTab Mine, VirtualTab Page, FakeLease Lease)> Ready(AgentManifest? m = null)
    {
        var mine = _k.Open(new Uri("https://example.com/"));
        await _k.ActivateAsync(mine.Id);
        var s = await _gw.OpenAsync(m ?? Manifest(), default);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Navigate, "https://github.com/reddy5310/jevbrowse"), default)).Ok);
        var page = _k.Tabs.Single(t => s.Pages.Contains(t.Id));
        return (s, mine, page, _leases[page.Id]);
    }

    [Fact]
    public async Task A_screenshot_is_returned_in_memory_and_nothing_is_written_anywhere()
    {
        var (s, _, _, lease) = await Ready();
        lease.ScreenshotBytes = FakeLease.TinyPng(64, 32);

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(lease.ScreenshotBytes, r.Screenshot);
        Assert.Null(r.ScreenshotPath);
        Assert.False(Directory.Exists(_shotDir), "no folder, so nothing to clean up after Stop, expiry or a crash");
        Assert.Contains(s.Audit, e => e.Action == "screenshot" && e.Allowed && e.Reason.Contains("64x32") && e.Reason.Contains("memory only"));
    }

    [Fact]
    public async Task It_works_on_a_page_that_is_not_shown_without_showing_it_and_without_moving_the_window()
    {
        var (s, mine, page, lease) = await Ready();
        Assert.False(lease.IsVisible);

        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default)).Ok);

        Assert.Equal(1, lease.Screenshots);
        Assert.False(lease.VisibleAtLastCapture);
        Assert.Equal(0, lease.ShowCalls);                       // never asked to be shown
        Assert.False(lease.IsVisible);
        Assert.Equal(mine.Id, _k.Active?.Id);
        Assert.True(_leases[mine.Id].IsVisible);
    }

    [Fact]
    public async Task It_does_not_turn_on_the_ordinary_thumbnail_path_to_do_it()
    {
        var (s, _, page, lease) = await Ready();
        Assert.False(lease.AllowThumbnails);                    // a disposable session gets no thumbnails
        await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(lease.AllowThumbnails);
        Assert.Null(_k.GetCheckpoint(page.Id)?.ThumbnailPath);
    }

    [Fact]
    public async Task Screenshot_has_to_be_granted_separately_from_reading()
    {
        var (s, _, _, lease) = await Ready(Manifest(actions: [AgentAction.Navigate, AgentAction.Read]));
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(r.Ok);
        Assert.StartsWith("action_not_granted", r.Message);
        Assert.Equal(0, lease.Screenshots);
    }

    [Fact]
    public async Task A_page_that_asks_for_a_password_is_never_photographed()
    {
        var (s, _, _, lease) = await Ready();
        lease.RaiseSignals(PageSignals.PasswordField);
        var r = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(r.Ok);
        Assert.StartsWith("hard:", r.Message);
        Assert.Null(r.Screenshot);
        Assert.Equal(0, lease.Screenshots);                     // refused before the engine was even asked
    }

    [Fact]
    public async Task Pictures_have_their_own_budget_and_the_ceiling_can_only_lower_it()
    {
        var (s, _, _, _) = await Ready(Manifest(shots: 2));
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default)).Ok);
        Assert.True((await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default)).Ok);
        var third = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(third.Ok);
        Assert.Equal("screenshot_quota_exhausted", third.Message);

        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "c", AllowDomains = ["github.com"], Actions = [AgentAction.Screenshot], MaxScreenshots = 5 } };
        Assert.Equal(5, ceiling.Clamp(new AgentManifest { Agent = "a", AllowDomains = ["github.com"], Actions = [AgentAction.Screenshot], MaxScreenshots = 50 }).Effective.MaxScreenshots);
        Assert.Equal(3, ceiling.Clamp(new AgentManifest { Agent = "a", AllowDomains = ["github.com"], Actions = [AgentAction.Screenshot], MaxScreenshots = 3 }).Effective.MaxScreenshots);
    }

    [Fact]
    public async Task An_oversized_picture_and_something_that_is_not_a_picture_are_refused()
    {
        var (s, _, _, lease) = await Ready();
        lease.ScreenshotBytes = FakeLease.TinyPng(64, 32, pad: AgentGateway.MaxScreenshotBytes);
        var big = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(big.Ok); Assert.Equal("screenshot_too_large", big.Message); Assert.Null(big.Screenshot);

        lease.ScreenshotBytes = new byte[200];
        var junk = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        Assert.False(junk.Ok); Assert.Equal("screenshot_not_an_image", junk.Message);
    }

    [Fact]
    public async Task Stopping_the_session_while_the_engine_is_still_drawing_returns_nothing_and_keeps_nothing()
    {
        var (s, _, _, lease) = await Ready();
        var finish = new TaskCompletionSource();
        lease.ScreenshotGate = () => finish.Task;

        var pending = _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        await _gw.StopAsync(s, default);

        var r = await pending;
        Assert.False(r.Ok);
        Assert.Null(r.Screenshot);
        Assert.True(s.CleanedUp);
        Assert.Contains(s.Audit, e => e.Action == "screenshot" && !e.Allowed && e.Reason.StartsWith("cancelled"));

        finish.SetResult();                                     // the engine finishes late: nothing is delivered, kept or thrown
        await Task.Delay(50);
        Assert.False(Directory.Exists(_shotDir));
        Assert.Equal(1, s.Audit.Count(e => e.Action == "screenshot"));
    }

    [Fact]
    public async Task A_picture_that_finishes_just_as_the_session_ends_is_thrown_away_not_delivered()
    {
        var (s, _, _, lease) = await Ready();
        lease.ScreenshotGate = async () => await _gw.CloseAsync(s, default);   // the session ends inside the capture; the bytes still come back

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);

        Assert.False(r.Ok);
        Assert.Null(r.Screenshot);
        Assert.Contains(s.Audit, e => e.Action == "screenshot" && !e.Allowed);
        Assert.DoesNotContain(s.Audit, e => e.Action == "screenshot" && e.Allowed);
    }

    [Fact]
    public async Task A_page_that_turns_into_a_login_form_while_being_photographed_has_its_picture_discarded()
    {
        var (s, _, _, lease) = await Ready();
        lease.ScreenshotGate = () => { lease.RaiseSignals(PageSignals.PasswordField); return Task.CompletedTask; };

        var r = await _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);

        Assert.False(r.Ok);
        Assert.Null(r.Screenshot);
        Assert.StartsWith("screenshot_discarded", r.Message);
    }

    [Fact]
    public async Task Expiry_releases_the_page_and_a_late_picture_is_not_delivered()
    {
        var (s, _, _, lease) = await Ready();
        var gate = new TaskCompletionSource();
        lease.ScreenshotGate = () => gate.Task;
        var pending = _gw.ExecuteAsync(s, new(AgentAction.Screenshot), default);
        await Task.Delay(30);

        Assert.Equal(0, await _gw.SweepExpiredAsync([s], default));   // not expired yet: a running capture is not disturbed
        Assert.False(pending.IsCompleted);
        _now = s.ExpiresAt.AddMinutes(1);
        Assert.Equal(1, await _gw.SweepExpiredAsync([s], default));    // expiry ends the session and releases its pages
        gate.SetResult();                                              // the engine finishes late

        var r = await pending;
        Assert.False(r.Ok);
        Assert.Null(r.Screenshot);
        Assert.True(s.CleanedUp);
    }

    [Fact]
    public async Task Over_the_local_endpoint_the_picture_arrives_as_base64_in_the_response_and_no_file_exists()
    {
        var ceiling = new AgentCeiling { Limits = new AgentManifest { Agent = "c", AllowDomains = ["github.com"], Actions = [AgentAction.Navigate, AgentAction.Read, AgentAction.Screenshot], MaxLivePages = 3, SessionMinutes = 60, MaxActions = 100, MaxScreenshots = 5 } };
        using var host = new LocalAgentHost(_gw, ceiling);
        host.Start();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", host.Token);
        var open = await http.PostAsJsonAsync("sessions", Manifest());
        open.EnsureSuccessStatusCode();
        var id = (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.PostAsJsonAsync($"sessions/{id}/actions", new { action = "Navigate", url = "https://github.com/" })).StatusCode);
        var mine = _k.Tabs.Single(t => t.Url.Host == "github.com");
        _leases[mine.Id].ScreenshotBytes = FakeLease.TinyPng(120, 80);

        var shot = await http.PostAsJsonAsync($"sessions/{id}/actions", new { action = "Screenshot" });

        Assert.Equal(System.Net.HttpStatusCode.OK, shot.StatusCode);
        var body = await shot.Content.ReadFromJsonAsync<JsonElement>();
        // The response is a serialised AgentResponse; property casing is the host's business, so look properties up without regard to it.
        JsonElement Prop(string name) => body.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        var png = Convert.FromBase64String(Prop("screenshot").GetString()!);
        Assert.Equal(FakeLease.TinyPng(120, 80), png);
        Assert.True(body.EnumerateObject().All(p => !string.Equals(p.Name, "screenshotPath", StringComparison.OrdinalIgnoreCase) || p.Value.ValueKind == JsonValueKind.Null));
        Assert.False(Directory.Exists(_shotDir));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.DeleteAsync($"sessions/{id}")).StatusCode);
    }

    [Fact]
    public void Leftovers_from_older_builds_are_removed_at_start_up_but_only_from_a_folder_named_screenshots()
    {
        Directory.CreateDirectory(Path.Combine(_shotDir, "abc123"));
        File.WriteAllBytes(Path.Combine(_shotDir, "abc123", "old.png"), [1, 2, 3]);
        var other = Path.Combine(Path.GetDirectoryName(_shotDir)!, "notes");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.txt"), "mine");

        Assert.Equal(1, AgentGateway.RemoveLegacyScreenshotFiles(_shotDir));
        Assert.False(Directory.Exists(_shotDir));
        Assert.Equal(0, AgentGateway.RemoveLegacyScreenshotFiles(other));   // not named "screenshots": untouched
        Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
    }

    [Fact]
    public void A_refusal_is_explained_in_words_a_person_can_act_on()
    {
        Assert.Contains("password", AgentActivity.RefusalWords("hard:secret_on_screen"));
        Assert.Contains("pictures", AgentActivity.RefusalWords("screenshot_quota_exhausted"));
    }
}
