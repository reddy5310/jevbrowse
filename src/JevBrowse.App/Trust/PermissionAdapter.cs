using JevBrowse.Domain;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.Trust;

/// <summary>
/// WebView2 PermissionRequested → Trust OS PermissionPolicy.
///
/// Decisions are owned by JevBrowse, not by WebView2's profile: <c>SavesInProfile</c> is always false, otherwise the
/// engine would remember a "for 1 hour" or "once" answer indefinitely and skip our prompt next time. Grants are
/// keyed by container + EXACT origin (see PermissionKey). Ephemeral containers keep grants in memory only.
///
/// What "1 hour" means: it governs future *prompts*. A camera/microphone stream already granted is not revoked
/// mid-call; it ends when the page stops it or the tab is closed.
/// </summary>
public sealed class PermissionAdapter
{
    public enum Choice
    {
        AllowOnce, AllowForHour, AllowAlways,
        /// <summary>Deny and remember: the site is blocked until the user says otherwise.</summary>
        Block,
        /// <summary>Deny this request and remember nothing. Used when we could not ask, and by unattended checks.</summary>
        BlockOnce,
    }

    private readonly SitePermissionsRepository _repo;
    private readonly Func<string, PermissionKind, Task<Choice>> _prompt;
    private readonly Dictionary<(string Key, PermissionKind Kind), PermissionGrant> _memory = [];
    private readonly Func<DateTimeOffset> _clock;

    public PermissionAdapter(SitePermissionsRepository repo, Func<string, PermissionKind, Task<Choice>> prompt, Func<DateTimeOffset>? clock = null)
    {
        _repo = repo;
        _prompt = prompt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private PermissionPolicy PolicyFor(IdentityContainer container) => new((key, kind) =>
    {
        if (!PermissionKey.MayPersist(container)) return _memory.TryGetValue((key, kind), out var g) ? g : null;
        var r = _repo.Get(key, (int)kind);
        return r is null ? null : new PermissionGrant(key, kind, r.Allowed, r.ExpiresAt);
    }, _clock);

    private void Store(IdentityContainer container, string key, PermissionKind kind, bool allowed, DateTimeOffset? expires)
    {
        var now = _clock();
        if (PermissionKey.MayPersist(container)) _repo.Set(key, (int)kind, allowed, expires, now);
        else _memory[(key, kind)] = new PermissionGrant(key, kind, allowed, expires);
    }

    /// <summary>Explicit block from the UI (palette): same key, same storage rules as a prompt answer.</summary>
    public bool Block(IdentityContainer container, ContextId isolation, Uri origin, PermissionKind kind)
    {
        Store(container, PermissionKey.For(container, isolation, origin), kind, false, null);
        return PermissionKey.MayPersist(container);
    }

    public void Attach(CoreWebView2 core, IdentityContainer container, ContextId isolation)
    {
        var policy = PolicyFor(container);
        core.PermissionRequested += async (_, e) =>
        {
            e.SavesInProfile = false;   // never let the engine keep its own copy of our decision
            var deferral = e.GetDeferral();
            try
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var origin)) { e.State = CoreWebView2PermissionState.Deny; return; }
                var key = PermissionKey.For(container, isolation, origin);
                var kind = Map(e.PermissionKind);
                var verdict = policy.Decide(key, kind);
                if (verdict == PermissionVerdict.Ask)
                {
                    var label = $"{origin.Scheme}://{origin.Host}{(origin.IsDefaultPort ? "" : ":" + origin.Port)} ({container})";
                    // Failing to ASK must fail closed. A prompt that throws must deny this one request, not take the app down and
                    // not leave the page's request hanging.
                    Choice choice;
                    try { choice = await _prompt(label, kind); }
                    catch (Exception) { choice = Choice.BlockOnce; }
                    var now = _clock();
                    switch (choice)
                    {
                        case Choice.AllowForHour: Store(container, key, kind, true, now.AddHours(1)); break;
                        case Choice.AllowAlways: Store(container, key, kind, true, null); break;
                        case Choice.Block: Store(container, key, kind, false, null); break;
                        // AllowOnce stores nothing: the next request asks again.
                    }
                    verdict = choice is Choice.Block or Choice.BlockOnce ? PermissionVerdict.Deny : PermissionVerdict.Allow;
                }
                e.State = verdict == PermissionVerdict.Allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            }
            finally { deferral.Complete(); }
        };
    }

    private static PermissionKind Map(CoreWebView2PermissionKind k) => k switch
    {
        CoreWebView2PermissionKind.Geolocation => PermissionKind.Geolocation,
        CoreWebView2PermissionKind.Camera => PermissionKind.Camera,
        CoreWebView2PermissionKind.Microphone => PermissionKind.Microphone,
        CoreWebView2PermissionKind.Notifications => PermissionKind.Notifications,
        CoreWebView2PermissionKind.ClipboardRead => PermissionKind.Clipboard,
        CoreWebView2PermissionKind.OtherSensors => PermissionKind.Sensors,
        _ => PermissionKind.Other,
    };
}
