using System.Diagnostics;
using System.Text.Json;
using JevBrowse.App.Renderer;
using JevBrowse.App.Trust;
using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;

namespace JevBrowse.App;

public sealed partial class MainWindow
{
    /// <summary>Real WebViews and cookie stores; host media messages are injected, not a hardware capture test.</summary>
    private async Task RunPrivateSessionCheckAsync()
    {
        _tick?.Stop();
        var kernel = _kernel!;
        var leases = _leases!;
        var checks = new Dictionary<string, bool>();
        var timer = Stopwatch.StartNew();
        var originalPage = leases.LocalPage;
        leases.LocalPage = uri => uri.Host == "private-probe" ? "<!doctype html><title>Private session probe</title><p>Local probe</p>" : originalPage?.Invoke(uri);
        WebView2Lease Lease(VirtualTab tab) => leases.TryGet(tab.Id, out var lease) ? (WebView2Lease)lease : throw new InvalidOperationException("Probe renderer missing.");
        async Task<VirtualTab> OpenAsync()
        {
            var tab = kernel.Open(new Uri("jev://private-probe"));
            await kernel.ActivateAsync(tab.Id);
            return tab;
        }
        void SetCookie(VirtualTab tab, string value)
        {
            var manager = Lease(tab).View.CoreWebView2.CookieManager;
            manager.AddOrUpdateCookie(manager.CreateCookie("session", value, "private-session.test", "/"));
        }
        async Task<string?> Cookie(VirtualTab tab) => (await Lease(tab).View.CoreWebView2.CookieManager.GetCookiesAsync("https://private-session.test/"))
            .FirstOrDefault(c => c.Name == "session")?.Value;
        string? error = null;
        try
        {
            await ChangeProductModeAsync(ProductMode.Simple);
            var normal = await OpenAsync();
            var normalWorkspace = kernel.ActiveWorkspace;
            SetCookie(normal, "normal");
            await ChangeProductModeAsync(ProductMode.Private);
            var session = kernel.ActiveWorkspace;
            var first = await OpenAsync();
            checks["privateStartsWithoutNormalCookie"] = await Cookie(first) is null;
            SetCookie(first, "private");
            var second = await OpenAsync();
            checks["privateTabsShareSessionCookie"] = await Cookie(second) == "private";

            await ChangeProductModeAsync(ProductMode.Simple);
            checks["leaveReturnsToNormalWorkspaceAndTab"] = kernel.ActiveWorkspace == normalWorkspace && kernel.Active?.Id == normal.Id;
            checks["normalCookieUnchanged"] = await Cookie(normal) == "normal";
            await ChangeProductModeAsync(ProductMode.Private);
            checks["returnKeepsSessionAndCookie"] = kernel.ActiveWorkspace == session && await Cookie(second) == "private";

            var oldLease = Lease(first);
            oldLease.OnMediaMessage("jev:media:probe:mic,cam,peer");
            kernel.SetProtection(second.Id, ProtectionFlags.KeepActive | ProtectionFlags.DirtyForm);
            kernel.Open(new Uri("jev://private-probe/virtual"));
            checks["probeMediaProtectedBeforeEnd"] = first.Protection.HasLiveMedia();

            var answer = new TaskCompletionSource<PermissionAdapter.Choice>();
            var permissions = new PermissionAdapter(new SitePermissionsRepository(_db!), (_, _) => answer.Task);
            permissions.Block(IdentityContainer.Private, session, new Uri("https://private-session.test"), PermissionKind.Geolocation);
            var pending = permissions.DecideAsync(IdentityContainer.Private, session, new Uri("https://private-session.test"), PermissionKind.Camera);
            checks["permissionAnswerWasPending"] = !pending.IsCompleted;
            permissions.EndSession(IdentityContainer.Private, session);
            var closedAndDeleted = await EndPrivateSessionCoreAsync(session);
            answer.SetResult(PermissionAdapter.Choice.AllowAlways);
            checks["latePermissionAnswerDeniedAndForgotten"] = await pending == PermissionVerdict.Deny && permissions.SessionGrantCount(IdentityContainer.Private, session) == 0;
            checks["privateRenderersClosed"] = !leases.TryGet(first.Id, out _) && !leases.TryGet(second.Id, out _);
            checks["privateProfileDeleted"] = closedAndDeleted;
            checks["privateTabsAndWorkspaceRemoved"] = !kernel.TabsIn(session).Any() && kernel.Workspaces.All(w => w.Id != session);
            oldLease.OnMediaMessage("jev:media:late:mic,cam");
            oldLease.SweepMedia();
            oldLease.CleanupHostState();
            checks["lateMediaCannotReviveHostTracking"] = oldLease.HostStateReleased;
            checks["normalRendererSurvivedEnd"] = leases.TryGet(normal.Id, out _) && await Cookie(normal) == "normal";

            await ChangeProductModeAsync(ProductMode.Private);
            var freshSession = kernel.ActiveWorkspace;
            var fresh = await OpenAsync();
            checks["freshSessionHasNewIdentityAndNoCookie"] = freshSession != session && await Cookie(fresh) is null;
            checks["freshSessionCleanupComplete"] = await EndPrivateSessionCoreAsync(freshSession);
            await kernel.CloseAsync(normal.Id);
            checks["allProbeRenderersClosed"] = leases.LiveResources.Count == 0;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { leases.LocalPage = originalPage; }
        var result = new
        {
            pass = error is null && checks.Count > 0 && checks.Values.All(value => value),
            checks, elapsedMs = timer.ElapsedMilliseconds, error,
            scope = "Real WebViews and cookie profiles; product-mode handlers and explicit session cleanup. Media host heartbeats and delayed permission answers are injected. This does not test device capture or click the confirmation dialog. Use an isolated data directory."
        };
        await File.WriteAllTextAsync(Path.Combine(DataDir, "benchmarks", "private-session-check.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
