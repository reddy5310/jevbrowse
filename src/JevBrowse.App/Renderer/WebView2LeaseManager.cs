using System.Text.Json;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace JevBrowse.App.Renderer;

/// <summary>
/// The only place in the app that knows a "renderer" is a WebView2 control. One CoreWebView2Environment per identity
/// container (§10.1): separate user-data folders mean separate cookies, storage and permissions. Ephemeral containers
/// get a fresh folder per session that is deleted on shutdown and swept on the next start.
/// </summary>
public sealed class WebView2LeaseManager : IRendererLeaseManager
{
    private readonly Dictionary<ResourceId, WebView2Lease> _live = [];
    private readonly Dictionary<string, CoreWebView2Environment> _envs = [];
    private readonly Panel _host;
    private readonly string _profilesDir;
    private readonly string _thumbnailDir;
    private readonly string _sessionTag = Environment.ProcessId.ToString();

    public WebView2LeaseManager(Panel host, string profilesDir, string thumbnailDir)
    {
        _host = host;
        _profilesDir = profilesDir;
        _thumbnailDir = thumbnailDir;
        SweepEphemeral();
    }

    public int MaxLive { get; set; } = 5;
    public IReadOnlyCollection<ResourceId> LiveResources => _live.Keys;
    public IEnumerable<int> ProcessIds => _envs.Values.SelectMany(e => e.GetProcessInfos().Select(p => p.ProcessId));

    /// <summary>
    /// Called once per new CoreWebView2 and awaited BEFORE its first navigation, so document-start scripts are
    /// registered in time. Shield and Trust OS attach here.
    /// </summary>
    public Func<CoreWebView2, ResourceId, IdentityContainer, ContextId, Uri, Task>? OnCoreCreated { get; set; }
    public Action<ResourceId>? OnCoreDisposed { get; set; }
    /// <summary>Resolves jev:// URLs to locally generated HTML (welcome/help). No network involved.</summary>
    public Func<Uri, string?>? LocalPage { get; set; }

    /// <summary>
    /// One profile per identity. Persistent containers share one profile each; EPHEMERAL containers get a profile per
    /// workspace, so two Private tabs sessions or two agent sessions never share cookies or storage.
    /// </summary>
    public async Task<CoreWebView2Environment> GetEnvironmentAsync(IdentityContainer container, ContextId isolationKey)
    {
        var key = container.IsEphemeral() ? $"{container}:{isolationKey}" : container.ToString();
        if (_envs.TryGetValue(key, out var env)) return env;
        var udf = container.IsEphemeral()
            ? Path.Combine(_profilesDir, "ephemeral", $"{container.ToString().ToLowerInvariant()}-{_sessionTag}-{isolationKey.ToString()[..8]}")
            : Path.Combine(_profilesDir, container.ToString().ToLowerInvariant());
        Directory.CreateDirectory(udf);
        env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        _envs[key] = env;
        return env;
    }

    private readonly Dictionary<ResourceId, Func<Uri, bool>> _navPolicies = [];

    public void SetNavigationPolicy(ResourceId id, Func<Uri, bool>? guard)
    {
        if (guard is null) _navPolicies.Remove(id); else _navPolicies[id] = guard;
        if (_live.TryGetValue(id, out var live)) live.NavigationGuard = guard;   // also bind an already-live renderer
    }

    public bool TryGet(ResourceId id, out IRendererLease lease)
    {
        var ok = _live.TryGetValue(id, out var l);
        lease = l!;
        return ok;
    }

    public async Task<IRendererLease> AcquireAsync(ResourceId id, Uri initialUrl, RenderIntent intent, IdentityContainer container, ContextId isolationKey, CancellationToken ct)
    {
        var env = await GetEnvironmentAsync(container, isolationKey);
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        _host.Children.Add(view);
        await view.EnsureCoreWebView2Async(env);
        if (OnCoreCreated is not null) await OnCoreCreated(view.CoreWebView2, id, container, isolationKey, initialUrl);
        var lease = await WebView2Lease.CreateAsync(id, view, _thumbnailDir);
        // In force before the first Navigate below, so an allowed URL that redirects out of scope is stopped.
        if (_navPolicies.TryGetValue(id, out var policy)) lease.NavigationGuard = policy;
        _live[id] = lease;
        if (initialUrl.Scheme == "jev" && LocalPage is not null && LocalPage(initialUrl) is { } html) view.CoreWebView2.NavigateToString(html);
        else view.CoreWebView2.Navigate(initialUrl.ToString());
        return lease;
    }

    public async Task ReleaseAsync(ResourceId id, ReleaseDisposition disposition, CancellationToken ct)
    {
        if (!_live.TryGetValue(id, out var lease)) return;
        if (disposition == ReleaseDisposition.Suspend) { await lease.TrySuspendAsync(); return; }
        _live.Remove(id);
        lease.CleanupHostState();   // before the view goes: stops the media timer and drops its reference to the lease
        _host.Children.Remove(lease.View);
        lease.View.Close();
        OnCoreDisposed?.Invoke(id);
    }

    /// <summary>Close every renderer and mark this session's ephemeral profiles for deletion.</summary>
    public void Shutdown()
    {
        foreach (var l in _live.Values.ToList()) { l.CleanupHostState(); _host.Children.Remove(l.View); l.View.Close(); }
        _live.Clear();
        SweepEphemeral(); // best effort now; processes still winding down are caught on next start
    }

    private void SweepEphemeral()
    {
        var dir = Path.Combine(_profilesDir, "ephemeral");
        if (!Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
        {
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class WebView2Lease : IRendererLease
{
    // Narrow, origin-agnostic bridge (Table A.11): the page can only tell us fixed strings. No host objects exposed.
    // Password/payment detection never reads values; it only reports that such an input exists.
    private const string PageScript = """
        (() => {
          if (window.__jevHooked) return; window.__jevHooked = true;
          const post = m => { try { chrome.webview.postMessage(m); } catch {} };
          let dirty = false;
          const mark = e => {
            const t = e.target; if (!t || t.type === 'password') return;
            if (!dirty) { dirty = true; post('jev:dirty-form'); }
          };
          document.addEventListener('input', mark, true);
          document.addEventListener('change', mark, true);
          const scan = () => {
            const pw = !!document.querySelector('input[type="password"]');
            const cc = !!document.querySelector('input[autocomplete^="cc-"]');
            // "Logged in" evidence, structure only: a sign-out link or form. Raises the class (less persistence, no AI).
            const out = !!document.querySelector('a[href*="logout" i], a[href*="signout" i], a[href*="sign_out" i], a[href*="sign-out" i], a[href*="log-out" i], form[action*="logout" i], form[action*="signout" i]');
            if (pw) post('jev:secret-field');
            if (cc) post('jev:payment-field');
            if (out) post('jev:authenticated');
            // The page has rendered real content and shows no sign-in affordance. This does NOT mean public -- an
            // authenticated document looks identical -- so it never raises or lowers the class. It only tells the
            // shell the assessment is settled rather than still loading.
            if (!pw && !cc && !out && document.body && (document.body.innerText || '').trim().length > 200) post('jev:content-rendered');
          };
          // Camera / microphone / screen capture, and calls. IsDocumentPlayingAudio cannot see any of this: a
          // microphone capture makes no sound come OUT of the page, so a live call looks exactly like an idle tab
          // and the scheduler disposes the renderer mid-meeting.
          //
          // Reported as a HEARTBEAT keyed by this document, not as on/off edges. A document that navigates away,
          // crashes or is discarded simply stops sending, and the host expires it -- so a stale "in a call" cannot
          // outlive the page that was in one, and a CANCELLED navigation does not wrongly clear a running call
          // either. Frames each report under their own id and the host adds them up.
          const docId = (Math.random().toString(36).slice(2) + Date.now().toString(36));
          const mic = new Set(), cam = new Set(), screen = new Set();
          let peers = 0, beat = null, lastSent = '';
          const kinds = () => (mic.size ? 'mic,' : '') + (cam.size ? 'cam,' : '') + (screen.size ? 'screen,' : '') + (peers > 0 ? 'peer' : '');
          const send = () => {
            const k = kinds();
            if (k === '' ) {
              if (beat) { clearInterval(beat); beat = null; }
              if (lastSent !== '') { lastSent = ''; post('jev:media-end:' + docId); }
              return;
            }
            lastSent = k;
            post('jev:media:' + docId + ':' + k);
            if (!beat) beat = setInterval(() => post('jev:media:' + docId + ':' + kinds()), 2000);
          };
          const forget = t => { mic.delete(t); cam.delete(t); screen.delete(t); send(); };
          const md = navigator.mediaDevices;
          if (md) {
            // Clones have independent lifetimes: a page may clone a track, stop the original and keep using the
            // clone. Counting only what getUserMedia handed back would then report no capture during live capture.
            const track = (t, set) => {
              if (set.has(t)) return t;
              set.add(t);
              t.addEventListener('ended', () => forget(t));
              const stop = t.stop.bind(t); t.stop = () => { stop(); forget(t); };
              const clone = t.clone.bind(t); t.clone = () => track(clone(), set);
              send();
              return t;
            };
            const watch = (stream, screenShare) => {
              stream.getAudioTracks().forEach(t => track(t, screenShare ? screen : mic));
              stream.getVideoTracks().forEach(t => track(t, screenShare ? screen : cam));
              const add = stream.clone.bind(stream);
              stream.clone = () => watch(add(), screenShare);
              return stream;
            };
            for (const [fn, isScreen] of [['getUserMedia', false], ['getDisplayMedia', true]]) {
              const orig = md[fn] && md[fn].bind(md);
              if (orig) md[fn] = (...a) => orig(...a).then(s => watch(s, isScreen));
            }
          }
          if (typeof RTCPeerConnection === 'function') {
            const Orig = RTCPeerConnection;
            const Patched = function (...a) {
              const pc = new Orig(...a);
              let counted = false;
              const sync = () => {
                // 'disconnected' is the RECOVERY window, not the end of the call -- it is exactly when a participant
                // with no local capture needs the renderer most. Only 'failed' and 'closed' are terminal.
                // 'new' is not counted: a page may construct a connection and never use it.
                const s = pc.connectionState;
                const on = s === 'connecting' || s === 'connected' || s === 'disconnected';
                if (on === counted) return;
                counted = on; peers += on ? 1 : -1; send();
              };
              pc.addEventListener('connectionstatechange', sync);
              const close = pc.close.bind(pc); pc.close = () => { close(); if (counted) { counted = false; peers--; send(); } };
              return pc;
            };
            Patched.prototype = Orig.prototype;
            for (const k of Object.getOwnPropertyNames(Orig)) { try { Patched[k] = Orig[k]; } catch {} }
            window.RTCPeerConnection = Patched;
          }
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scan); else scan();
          new MutationObserver(() => scan()).observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

    // Heavy pages (large stylesheets, video) can take >3 s to rasterize; 8 s bounds virtualize without losing thumbnails.
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(8);

    private readonly string _thumbnailDir;
    private ProtectionFlags _detected;
    private PageSignals _signals;
    private int _activeDownloads;
    private string? _lastThumbnail;
    private Task? _pendingThumbnail;

    private WebView2Lease(ResourceId id, WebView2 view, string thumbnailDir)
    {
        ResourceId = id;
        View = view;
        _thumbnailDir = thumbnailDir;
    }

    public static async Task<WebView2Lease> CreateAsync(ResourceId id, WebView2 view, string thumbnailDir)
    {
        var lease = new WebView2Lease(id, view, thumbnailDir);
        var core = view.CoreWebView2;
        core.SourceChanged += (_, _) => lease.RaiseNavigation();
        core.DocumentTitleChanged += (_, _) => lease.RaiseNavigation();
        core.NavigationStarting += (_, e) =>
        {
            // Agent scope is enforced HERE, on every navigation the page attempts (link clicks, redirects, scripts).
            if (lease.NavigationGuard is { } guard && Uri.TryCreate(e.Uri, UriKind.Absolute, out var dest) && dest.Scheme is "http" or "https" && !guard(dest)) { e.Cancel = true; return; }
            if (!e.IsRedirected) lease.SetSignals(PageSignals.None);
        };
        core.NavigationCompleted += (_, _) => { lease.ClearDetected(ProtectionFlags.DirtyForm); lease.Loaded?.Invoke(); };
        core.IsDocumentPlayingAudioChanged += (_, _) => lease.SetDetected(ProtectionFlags.Audible, core.IsDocumentPlayingAudio);
        core.DownloadStarting += (_, e) =>
        {
            lease._activeDownloads++;
            lease.SetDetected(ProtectionFlags.DownloadActive, true);
            e.DownloadOperation.StateChanged += (d, _) =>
            {
                if (d.State == CoreWebView2DownloadState.InProgress) return;
                if (--lease._activeDownloads <= 0) { lease._activeDownloads = 0; lease.SetDetected(ProtectionFlags.DownloadActive, false); }
            };
        };
        core.WebMessageReceived += (_, e) =>
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch (Exception) { }
            switch (msg)
            {
                case "jev:dirty-form": lease.SetDetected(ProtectionFlags.DirtyForm, true); break;
                case "jev:secret-field": lease.SetSignals(lease._signals | PageSignals.PasswordField); break;
                case "jev:payment-field": lease.SetSignals(lease._signals | PageSignals.PaymentField); break;
                case "jev:authenticated": lease.SetSignals((lease._signals | PageSignals.Authenticated) & ~PageSignals.ContentRendered); break;
                case "jev:content-rendered": if (!lease._signals.HasFlag(PageSignals.Authenticated)) lease.SetSignals(lease._signals | PageSignals.ContentRendered); break;
                default: lease.OnMediaMessage(msg, null); break;
            }
        };
        // A call can live in an iframe (embedded Meet, a widget). CoreWebView2.WebMessageReceived only carries the
        // TOP-LEVEL document's messages, so without this an embedded, microphone-only call reports nothing at all
        // and gets hibernated. Each frame reports under its own document id, and destroying a frame is the evidence
        // that its media is gone — one frame stopping never touches a sibling's protection.
        //
        // Frames nest. CoreWebView2Frame raises its own FrameCreated for children, so this subscribes recursively:
        // a call two iframes deep is still a call.
        void Watch(CoreWebView2Frame frame)
        {
            frame.WebMessageReceived += (_, me) =>
            {
                try { lease.OnMediaMessage(me.TryGetWebMessageAsString(), frame); } catch (Exception) { }
            };
            frame.Destroyed += (_, _) => lease.DropFrameMedia(frame);
            // A frame can also REPLACE its document without being destroyed: the old document is gone, and it never
            // sent a media-end. Without this its entry stays uncertain forever and the tab never sleeps again.
            // ContentLoading commits, so a cancelled navigation inside the frame leaves a running call alone.
            frame.ContentLoading += (_, _) => lease.DropFrameMedia(frame);
            frame.FrameCreated += (_, child) => Watch(child.Frame);
        }
        core.FrameCreated += (_, fe) => Watch(fe.Frame);

        // Confirmed replacement of the top-level document, on the same commit-not-intent basis.
        core.ContentLoading += (_, _) => lease.DropTopLevelMedia();

        // ProcessFailed is NOT a synonym for "the renderer died". It also fires for an unresponsive renderer — which
        // a long script can cause while the process is perfectly alive and still capturing — and for GPU and
        // individual frame-renderer failures. Clearing on all of them would undo the stall fix by another route.
        // Only an actually exited process is evidence that its documents are gone; everything else leaves the
        // entries in place, uncertain, which is the safe direction.
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                                    or CoreWebView2ProcessFailedKind.RenderProcessExited)
                lease.DropAllMedia();
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        return lease;
    }

    public WebView2 View { get; }
    public ResourceId ResourceId { get; }
    public bool IsSuspended => View.CoreWebView2?.IsSuspended ?? false;
    public bool IsVisible => View.Visibility == Visibility.Visible;
    public void Navigate(Uri url) => View.CoreWebView2.Navigate(url.ToString());

    /// <summary>
    /// A collapsed WebView2 cannot be screenshotted (CapturePreviewAsync never completes), so the thumbnail is taken
    /// at the moment the tab leaves the foreground, while it is still rendering.
    /// </summary>
    public bool AllowThumbnails { get; set; }

    public void SetVisible(bool visible)
    {
        // Pixels are only ever captured with the kernel's permission for the CURRENT class and container.
        if (!visible && IsVisible && AllowThumbnails && View.CoreWebView2 is not null)
            _pendingThumbnail = CaptureThumbnailAsync();
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CaptureThumbnailAsync()
    {
        if (!AllowThumbnails) return;
        try
        {
            Directory.CreateDirectory(_thumbnailDir);
            var path = Path.Combine(_thumbnailDir, $"{ResourceId}.png");
            var tmp = path + ".tmp";
            using var mem = new InMemoryRandomAccessStream();
            var capture = View.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, mem).AsTask();
            if (await Task.WhenAny(capture, Task.Delay(CaptureTimeout)) != capture) return;
            await capture;
            mem.Seek(0);
            using var input = mem.AsStreamForRead();
            using (var file = File.Create(tmp)) await input.CopyToAsync(file);
            if (!AllowThumbnails) { try { File.Delete(tmp); } catch (IOException) { } return; } // class tightened while capturing
            File.Move(tmp, path, overwrite: true); // atomic replace: never a half-written thumbnail
            _lastThumbnail = path;
        }
        catch (Exception) { /* thumbnail is disposable (§16); a miss is not an error */ }
    }

    public async Task<bool> TrySuspendAsync()
    {
        if (View.CoreWebView2 is null) return false;
        SetVisible(false); // WebView2 refuses to suspend a visible view (captures only if AllowThumbnails)
        try { return await View.CoreWebView2.TrySuspendAsync(); }
        catch (Exception) { return false; }
    }

    public void Resume() => View.CoreWebView2?.Resume();

    public async Task<CaptureResult> CaptureCheckpointAsync(string thumbnailDir, CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return new(null, CaptureOutcome.Failed, "the renderer is gone");

        double sx = 0, sy = 0; string? favicon = null;
        var gaps = new List<string>();
        var outcome = CaptureOutcome.Captured;
        var kept = PreservedParts.None;
        try
        {
            var script = core.ExecuteScriptAsync("JSON.stringify({x:window.scrollX,y:window.scrollY,f:(document.querySelector('link[rel~=\"icon\"]')||{}).href||null})").AsTask();
            if (await Task.WhenAny(script, Task.Delay(CaptureTimeout, ct)) == script)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await script) ?? "{}");
                sx = doc.RootElement.GetProperty("x").GetDouble();
                sy = doc.RootElement.GetProperty("y").GetDouble();
                favicon = doc.RootElement.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                kept |= PreservedParts.Position;
            }
            else
            {
                // The page did not answer. It may be busy or blocked; either way we did not learn where the user was.
                outcome = CaptureOutcome.TimedOut;
                gaps.Add("the page did not report its position in time");
            }
        }
        catch (OperationCanceledException) { return new(null, CaptureOutcome.Cancelled, "cancelled"); }
        catch (Exception ex) { outcome = CaptureOutcome.Partial; gaps.Add("position unavailable: " + ex.GetType().Name); }

        // Thumbnail: use the one taken on deactivation; if the view is still visible, take a fresh one now.
        if (AllowThumbnails && IsVisible) _pendingThumbnail = CaptureThumbnailAsync();
        if (_pendingThumbnail is { } pending)
        {
            try { if (await Task.WhenAny(pending, Task.Delay(CaptureTimeout, ct)) != pending) gaps.Add("no preview image"); }
            catch (OperationCanceledException) { return new(null, CaptureOutcome.Cancelled, "cancelled"); }
        }
        if (AllowThumbnails && _lastThumbnail is null && !gaps.Contains("no preview image")) gaps.Add("no preview image");

        if (ct.IsCancellationRequested) return new(null, CaptureOutcome.Cancelled, "cancelled");
        var url = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : new Uri("about:blank");
        var thumb = AllowThumbnails ? _lastThumbnail : null;
        var cp = new Checkpoint(ResourceId, url, core.DocumentTitle, sx, sy, favicon, thumb, DateTimeOffset.UtcNow);
        kept |= PreservedParts.Address;                                    // we got this far, so core.Source is real
        if (thumb is not null) kept |= PreservedParts.Preview;
        // A class that forbids thumbnails is not a shortfall: nothing was lost, the policy said not to keep pixels.
        if (!AllowThumbnails) kept |= PreservedParts.Preview;
        if (outcome == CaptureOutcome.Captured && gaps.Count > 0) outcome = CaptureOutcome.Partial;
        return new(cp, outcome, gaps.Count == 0 ? "address, position and preview" : string.Join("; ", gaps), kept);
    }

    // Readability-lite: prefer <article>/<main>/role=main, else the densest text container; strip nav/aside/footer/
    // script/style/forms. Runs in the page, returns text only. Password/secret inputs are never part of innerText.
    private const string ReadableScript = """
        (() => {
          const kill = 'nav, aside, footer, header, script, style, noscript, form, iframe, svg, [role="navigation"], [role="banner"], [role="contentinfo"], [aria-hidden="true"]';
          const clone = (document.querySelector('article, main, [role="main"]') || document.body);
          if (!clone) return '';
          const c = clone.cloneNode(true);
          c.querySelectorAll(kill).forEach(n => n.remove());
          let text = (c.innerText || '').replace(/\s+/g, ' ').trim();
          if (text.length < 400 && document.body) {
            // fall back to the block with the most text
            let best = '', bestLen = 0;
            document.body.querySelectorAll('div, section, td').forEach(el => {
              const t = (el.innerText || '').trim();
              if (t.length > bestLen && el.querySelectorAll('p').length >= 2) { best = t; bestLen = t.length; }
            });
            if (bestLen > text.length) text = best.replace(/\s+/g, ' ');
          }
          return text.slice(0, 200000);
        })()
        """;

    public async Task<string?> ExtractReadableTextAsync(CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return null;
        try
        {
            var task = core.ExecuteScriptAsync(ReadableScript).AsTask();
            if (await Task.WhenAny(task, Task.Delay(CaptureTimeout, ct)) != task) return null;
            return JsonSerializer.Deserialize<string>(await task);
        }
        catch (Exception) { return null; }
    }

    // ---- Agent Gateway surface. Scripts return structure only; values of inputs are never read. ----
    private const string PageMapScript = """
        (() => {
          const txt = e => (e.innerText || e.textContent || '').replace(/\s+/g, ' ').trim();
          const heads = [...document.querySelectorAll('h1,h2,h3')].slice(0, 40).map(txt).filter(Boolean);
          const links = [...document.querySelectorAll('a[href]')].slice(0, 150).map(a => ({ t: txt(a).slice(0, 80), h: a.href })).filter(l => l.t);
          const fields = [...document.querySelectorAll('input,textarea,select')].slice(0, 60).map(f => {
            const lab = f.labels && f.labels[0] ? txt(f.labels[0]) : (f.getAttribute('aria-label') || f.placeholder || null);
            return { n: f.name || f.id || '', t: (f.type || f.tagName).toLowerCase(), l: lab };
          });
          const main = document.querySelector('article, main, [role="main"]') || document.body;
          return JSON.stringify({ title: document.title, heads, links, fields, text: main ? txt(main).slice(0, 4000) : '' });
        })()
        """;

    public async Task<PageMap?> GetPageMapAsync(CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return null;
        try
        {
            var task = core.ExecuteScriptAsync(PageMapScript).AsTask();
            if (await Task.WhenAny(task, Task.Delay(CaptureTimeout, ct)) != task) return null;
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await task) ?? "{}");
            var r = doc.RootElement;
            var url = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : new Uri("about:blank");
            return new PageMap(url, r.GetProperty("title").GetString() ?? "",
                r.GetProperty("heads").EnumerateArray().Select(x => x.GetString() ?? "").ToList(),
                r.GetProperty("links").EnumerateArray().Select(x => new PageLink(x.GetProperty("t").GetString() ?? "", x.GetProperty("h").GetString() ?? "")).ToList(),
                r.GetProperty("fields").EnumerateArray().Select(x => new PageField(x.GetProperty("n").GetString() ?? "", x.GetProperty("t").GetString() ?? "", x.TryGetProperty("l", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null)).ToList(),
                r.GetProperty("text").GetString() ?? "");
        }
        catch (Exception) { return null; }
    }

    public Func<Uri, bool>? NavigationGuard { get; set; }

    public async Task<ElementInfo?> DescribeAsync(string selector, CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return null;
        try
        {
            var task = core.ExecuteScriptAsync($$"""
                (() => { const e = document.querySelector({{JsonSerializer.Serialize(selector)}}); if (!e) return 'null';
                  const txt = s => (s || '').replace(/\s+/g, ' ').trim().slice(0, 120);
                  const f = e.form || e.closest('form');
                  return JSON.stringify({ tag: e.tagName.toLowerCase(), type: (e.type || '').toLowerCase(), text: txt(e.innerText || e.value), label: txt(e.getAttribute('aria-label') || e.title),
                    name: txt((e.name || '') + ' ' + (e.id || '') + ' ' + (typeof e.className === 'string' ? e.className : '')), href: (e.href || e.getAttribute('formaction') || '').slice(0, 200), formMethod: f ? (f.method || 'get').toLowerCase() : '' }); })()
                """).AsTask();
            if (await Task.WhenAny(task, Task.Delay(CaptureTimeout, ct)) != task) return null;
            var json = JsonSerializer.Deserialize<string>(await task);
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            return new ElementInfo(S("tag"), S("type"), S("text"), S("label"), S("name"), S("href"), S("formMethod"));
        }
        catch (Exception) { return null; }
    }

    public Task<ActionResult> ClickAsync(string selector, CancellationToken ct) => RunActionAsync($$"""
        (() => { const e = document.querySelector({{JsonSerializer.Serialize(selector)}}); if (!e) return 'not found'; e.scrollIntoView({block:'center'}); e.click(); return 'ok'; })()
        """, ct);

    public Task<ActionResult> TypeAsync(string selector, string text, CancellationToken ct) => RunActionAsync($$"""
        (() => {
          const e = document.querySelector({{JsonSerializer.Serialize(selector)}}); if (!e) return 'not found';
          const t = (e.type || '').toLowerCase(); const ac = (e.autocomplete || '').toLowerCase();
          if (t === 'password' || ac.startsWith('cc-') || ac.includes('password') || ac === 'one-time-code') return 'refused: secret field';
          e.focus(); e.value = {{JsonSerializer.Serialize(text)}};
          e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true }));
          return 'ok';
        })()
        """, ct);

    private async Task<ActionResult> RunActionAsync(string script, CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return new(false, "no renderer");
        try
        {
            var task = core.ExecuteScriptAsync(script).AsTask();
            if (await Task.WhenAny(task, Task.Delay(CaptureTimeout, ct)) != task) return new(false, "timeout");
            var msg = JsonSerializer.Deserialize<string>(await task) ?? "";
            return new(msg == "ok", msg);
        }
        catch (Exception ex) { return new(false, ex.GetType().Name); }
    }

    public void ApplyCheckpoint(Checkpoint cp)
    {
        if (cp.ScrollX == 0 && cp.ScrollY == 0) return;
        var core = View.CoreWebView2;
        void Once(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Once;
            _ = core.ExecuteScriptAsync($"window.scrollTo({cp.ScrollX.ToString(System.Globalization.CultureInfo.InvariantCulture)},{cp.ScrollY.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }
        core.NavigationCompleted += Once;
    }

    public event Action<NavigationInfo>? NavigationChanged;
    public event Action? Loaded;
    // ---- live capture and calls, per document ----
    //
    // Keyed by the reporting document, never by the tab: a tab can hold a top-level page and several frames, each
    // with its own media, and one of them stopping must not clear another's protection.
    //
    // A MISSING HEARTBEAT DOES NOT MEAN THE MEDIA STOPPED. A live document can stop running JavaScript for a while
    // — one long main-thread task is enough — while the microphone stays open. Treating silence as "finished" would
    // hand the scheduler a live capture to dispose. So expiry only downgrades an entry to UNCERTAIN, which still
    // blocks automatic demotion. Protection is released on evidence, never on absence of it:
    //   • the page says it stopped (jev:media-end), or
    //   • the document is confirmed gone — its frame was destroyed, the top-level document was replaced, or the
    //     renderer process failed.
    // A user's own "Put to sleep" is unaffected: Cause.User overrides every protection.
    //
    // Elapsed time is monotonic (Stopwatch), not wall-clock: a clock adjustment must not expire a live call.

    private sealed class MediaEntry
    {
        public ProtectionFlags Kinds;
        public long SeenMs;
        public bool Uncertain;
        public CoreWebView2Frame? Frame;   // null = the top-level document
    }

    private static readonly System.Diagnostics.Stopwatch MediaClock = System.Diagnostics.Stopwatch.StartNew();
    private const long MediaHeartbeatGraceMs = 6000;   // 3 missed 2 s beats
    private readonly Dictionary<string, MediaEntry> _media = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _mediaSweeper;

    /// <summary>True when something we cannot currently confirm might still be capturing.</summary>
    internal bool MediaStatusUncertain => _media.Values.Any(e => e.Uncertain);

    internal void OnMediaMessage(string? msg, CoreWebView2Frame? frame = null)
    {
        if (msg is null) return;
        if (msg.StartsWith("jev:media-end:", StringComparison.Ordinal))
        {
            if (_media.Remove(msg["jev:media-end:".Length..])) ApplyMedia();   // the page said so: evidence
            return;
        }
        if (!msg.StartsWith("jev:media:", StringComparison.Ordinal)) return;
        var rest = msg["jev:media:".Length..];
        var split = rest.IndexOf(':');
        if (split <= 0) return;
        var docId = rest[..split];
        var kinds = ProtectionFlags.None;
        foreach (var k in rest[(split + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries)) kinds |= k switch
        {
            "mic" => ProtectionFlags.MicrophoneActive,
            "cam" => ProtectionFlags.CameraActive,
            "screen" => ProtectionFlags.ScreenShareActive,
            "peer" => ProtectionFlags.WebRtcActive,
            _ => ProtectionFlags.None,
        };
        if (kinds == ProtectionFlags.None) { if (_media.Remove(docId)) ApplyMedia(); return; }
        _media[docId] = new MediaEntry { Kinds = kinds, SeenMs = MediaClock.ElapsedMilliseconds, Frame = frame };
        ApplyMedia();
        StartSweeper();
    }

    /// <summary>The top-level document has been replaced, so anything the previous one was doing is gone with it.</summary>
    private void DropTopLevelMedia()
    {
        var gone = _media.Where(kv => kv.Value.Frame is null).Select(kv => kv.Key).ToList();
        foreach (var k in gone) _media.Remove(k);
        if (gone.Count > 0) ApplyMedia();
    }

    /// <summary>The renderer process exited: nothing it was doing survived, so nothing it claimed should either.</summary>
    internal void DropAllMedia()
    {
        if (_media.Count == 0) return;
        _media.Clear();
        ApplyMedia();
    }

    /// <summary>
    /// The renderer is going away for good. Uncertain entries deliberately never expire, so without this the
    /// two-second timer — and its reference to this lease — would outlive the tab that created it. Called on every
    /// release and on shutdown, which is what makes "End private session" release host-side tracking too.
    /// </summary>
    public void CleanupHostState()
    {
        _mediaSweeper?.Stop();
        _mediaSweeper = null;
        _media.Clear();
        _detected = ProtectionFlags.None;
        DetectedProtectionChanged = null;
        NavigationChanged = null;
        Loaded = null;
    }

    internal void DropFrameMedia(CoreWebView2Frame frame)
    {
        var gone = _media.Where(kv => ReferenceEquals(kv.Value.Frame, frame)).Select(kv => kv.Key).ToList();
        foreach (var k in gone) _media.Remove(k);
        if (gone.Count > 0) ApplyMedia();
    }

    private void StartSweeper()
    {
        if (_mediaSweeper is not null) return;
        _mediaSweeper = View.DispatcherQueue.CreateTimer();
        _mediaSweeper.Interval = TimeSpan.FromSeconds(2);
        _mediaSweeper.Tick += (_, _) => SweepMedia();
        _mediaSweeper.Start();
    }

    internal void SweepMedia()
    {
        var cutoff = MediaClock.ElapsedMilliseconds - MediaHeartbeatGraceMs;
        foreach (var e in _media.Values) e.Uncertain = e.SeenMs < cutoff;   // downgraded, never dropped
        if (_media.Count == 0) { _mediaSweeper?.Stop(); _mediaSweeper = null; }
    }

    private void ApplyMedia()
    {
        var all = ProtectionFlags.None;
        foreach (var e in _media.Values) all |= e.Kinds;
        var next = (_detected & ~ProtectionFlagsExtensions.LiveMedia) | all;
        if (next == _detected) return;
        _detected = next;
        DetectedProtectionChanged?.Invoke(_detected);
    }

    public event Action<ProtectionFlags>? DetectedProtectionChanged;
    public event Action<PageSignals>? PageSignalsChanged;

    private void RaiseNavigation()
    {
        var core = View.CoreWebView2;
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var u))
            NavigationChanged?.Invoke(new NavigationInfo(u, core.DocumentTitle));
    }

    private void SetDetected(ProtectionFlags flag, bool on)
    {
        var next = on ? _detected | flag : _detected & ~flag;
        if (next == _detected) return;
        _detected = next;
        DetectedProtectionChanged?.Invoke(_detected);
    }

    private void ClearDetected(ProtectionFlags flag) => SetDetected(flag, false);

    private void SetSignals(PageSignals s)
    {
        if (s == _signals) return;
        _signals = s;
        PageSignalsChanged?.Invoke(_signals);
    }
}
