using JevBrowse.Shield;
using JevBrowse.Storage;
using JevBrowse.TrustOS;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.Trust;

/// <summary>
/// WebView2 PermissionRequested → Trust OS PermissionPolicy. Denies and stored grants are answered without UI;
/// "Ask" is routed to a single prompt owned by the window (Table A.4: no ad-hoc prompts scattered through the app).
/// </summary>
public sealed class PermissionAdapter
{
    public enum Choice { AllowOnce, AllowForHour, AllowAlways, Block }

    private readonly SitePermissionsRepository _repo;
    private readonly PermissionPolicy _policy;
    private readonly Func<string, PermissionKind, Task<Choice>> _prompt;

    public PermissionAdapter(SitePermissionsRepository repo, Func<string, PermissionKind, Task<Choice>> prompt)
    {
        _repo = repo;
        _prompt = prompt;
        _policy = new PermissionPolicy((site, kind) =>
        {
            var r = repo.Get(site, (int)kind);
            return r is null ? null : new PermissionGrant(site, kind, r.Allowed, r.ExpiresAt);
        });
    }

    public void Attach(CoreWebView2 core)
    {
        core.PermissionRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var site = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? NetworkRequest.SiteOf(u.Host) : e.Uri;
                var kind = Map(e.PermissionKind);
                var verdict = _policy.Decide(site, kind);
                if (verdict == PermissionVerdict.Ask)
                {
                    var choice = await _prompt(site, kind);
                    var now = DateTimeOffset.UtcNow;
                    switch (choice)
                    {
                        case Choice.AllowForHour: _repo.Set(site, (int)kind, true, now.AddHours(1), now); break;
                        case Choice.AllowAlways: _repo.Set(site, (int)kind, true, null, now); break;
                        case Choice.Block: _repo.Set(site, (int)kind, false, null, now); break;
                    }
                    verdict = choice == Choice.Block ? PermissionVerdict.Deny : PermissionVerdict.Allow;
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
