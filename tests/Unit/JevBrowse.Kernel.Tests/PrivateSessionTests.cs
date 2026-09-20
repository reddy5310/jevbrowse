using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.VirtualTabs;

namespace JevBrowse.Kernel.Tests;

public sealed class PrivateSessionTests : IDisposable
{
    private readonly BrowserDb _db = new(":memory:");
    private readonly FakeLeaseManager _leases = new() { MaxLive = 8 };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jev-session-" + Guid.NewGuid().ToString("N"));
    private readonly TabKernel _kernel;

    public PrivateSessionTests()
    {
        Directory.CreateDirectory(_root);
        _kernel = new(_leases, new TabRepository(_db), new CheckpointRepository(_db), _root, workspaces: new WorkspaceRepository(_db));
        _kernel.Load();
    }

    private async Task<VirtualTab> OpenAsync(string url)
    {
        var tab = _kernel.Open(new Uri(url));
        await _kernel.ActivateAsync(tab.Id);
        return tab;
    }

    [Fact]
    public async Task Switching_away_keeps_private_tabs_and_identity_until_explicit_end()
    {
        var normal = await OpenAsync("https://example.com/normal");
        var session = _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        await _kernel.SwitchWorkspaceAsync(session.Id);
        var secret = await OpenAsync("https://example.com/private");
        var lease = _leases[secret.Id];
        await _kernel.SwitchWorkspaceAsync(ContextId.Default);
        Assert.Same(normal, _kernel.Active);
        Assert.Contains(secret, _kernel.Tabs);
        await _kernel.SwitchWorkspaceAsync(session.Id);
        Assert.Same(lease, _leases[secret.Id]);
        Assert.Equal(session.Id, _leases.IsolationKeys[secret.Id]);
        Assert.DoesNotContain(new TabRepository(_db).LoadAll(), t => t.Id == secret.Id);
        Assert.DoesNotContain(_kernel.Timeline(), t => t.WorkspaceId == session.Id);
    }

    [Fact]
    public async Task End_closes_protected_and_virtual_tabs_and_rejects_late_callbacks_and_restore()
    {
        var normal = await OpenAsync("https://example.com/normal");
        var session = _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        await _kernel.SwitchWorkspaceAsync(session.Id);
        var secret = await OpenAsync("https://example.com/private");
        var lease = _leases[secret.Id];
        lease.RaiseDetected(ProtectionFlags.MicrophoneActive | ProtectionFlags.CameraActive | ProtectionFlags.DownloadActive | ProtectionFlags.DirtyForm);
        _kernel.SetProtection(secret.Id, ProtectionFlags.KeepActive);
        var virtualTab = _kernel.Open(new Uri("https://example.com/virtual"));
        var snapshot = _kernel.RecordContextCheckpoint();
        await _kernel.EndPrivateSessionAsync(session.Id, ContextId.Default);
        Assert.Equal(ContextId.Default, _kernel.ActiveWorkspace);
        Assert.Single(_kernel.Tabs);
        Assert.Same(normal, _kernel.Tabs[0]);
        Assert.Single(_leases.LiveResources);
        Assert.DoesNotContain(session, _kernel.Workspaces);
        var events = new List<KernelEvent>();
        _kernel.Changed += events.Add;
        lease.RaiseNavigation(new Uri("https://example.com/late"), "Late private title");
        lease.RaiseSignals(PageSignals.PasswordField);
        lease.RaiseDetected(ProtectionFlags.CameraActive);
        lease.RaiseLoaded();
        Assert.Empty(events);
        Assert.Equal(PageSignals.None, _kernel.SignalsOf(secret));
        Assert.Null(_kernel.LastCapture(secret.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _kernel.SwitchWorkspaceAsync(session.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _kernel.RestoreContextAsync(snapshot));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _kernel.ActivateAsync(virtualTab.Id));
        await _kernel.EndPrivateSessionAsync(session.Id, ContextId.Default); // idempotent
        var fresh = _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        Assert.NotEqual(session.Id, fresh.Id);
        await _kernel.SwitchWorkspaceAsync(fresh.Id);
        var newTab = await OpenAsync("https://example.com/private");
        Assert.Equal(fresh.Id, _leases.IsolationKeys[newTab.Id]);
    }

    [Fact]
    public async Task End_waits_for_inflight_acquisition_then_closes_it()
    {
        var session = _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        await _kernel.SwitchWorkspaceAsync(session.Id);
        var tab = _kernel.Open(new Uri("https://example.com/private"));
        _leases.AcquireDelay = TimeSpan.FromMilliseconds(80);
        var activating = _kernel.ActivateAsync(tab.Id);
        var ending = _kernel.EndPrivateSessionAsync(session.Id, ContextId.Default);
        await Task.WhenAll(activating, ending);
        Assert.Empty(_leases.LiveResources);
        Assert.Empty(_kernel.Tabs);
    }

    [Fact]
    public async Task Renderer_release_failure_is_retryable_without_reopening_session()
    {
        var session = _kernel.CreateWorkspace("Private", IdentityContainer.Private);
        await _kernel.SwitchWorkspaceAsync(session.Id);
        var first = await OpenAsync("https://example.com/one");
        await OpenAsync("https://example.com/two");
        _leases.FailNextRelease = true;
        await Assert.ThrowsAsync<AggregateException>(() => _kernel.EndPrivateSessionAsync(session.Id, ContextId.Default));
        Assert.Single(_leases.LiveResources);
        Assert.Single(_kernel.Tabs);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _kernel.ActivateAsync(first.Id));
        await _kernel.EndPrivateSessionAsync(session.Id, ContextId.Default);
        Assert.Empty(_leases.LiveResources);
        Assert.Empty(_kernel.Tabs);
    }

    [Fact]
    public async Task Active_profile_is_not_swept_and_locked_data_is_not_reported_deleted()
    {
        using var owner = new EphemeralProfileStore(_root);
        using var otherInstance = new EphemeralProfileStore(_root);
        var path = owner.Create();
        var file = Path.Combine(path, "cookies");
        File.WriteAllText(file, "test session data");
        otherInstance.Sweep();
        Assert.True(File.Exists(file));
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(await owner.EndAsync(path));
        Assert.True(Directory.Exists(path));
        Assert.True(await owner.EndAsync(path));
        Assert.False(Directory.Exists(path));
        Assert.True(await owner.EndAsync(path));
    }

    [Fact]
    public void Startup_retries_abandoned_profiles_and_rejects_paths_outside_root()
    {
        using var profiles = new EphemeralProfileStore(_root);
        var abandoned = Path.Combine(_root, "old-session");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "cookies"), "abandoned");
        profiles.Sweep();
        Assert.False(Directory.Exists(abandoned));
        Assert.Throws<InvalidOperationException>(() => profiles.TryDelete(Path.GetDirectoryName(_root)!));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
