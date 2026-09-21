using System.Text.Json;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using JevBrowse.Storage;
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
    private readonly EphemeralProfileStore _ephemeral;
    private readonly Dictionary<string, string> _ephemeralPaths = [];
    private readonly Dictionary<ResourceId, string> _identities = [];
    private readonly HashSet<string> _endedIdentities = [];
    private readonly Dictionary<string, HashSet<uint>> _runningBrowsers = [];
    private bool _shutdown;

    public WebView2LeaseManager(Panel host, string profilesDir, string thumbnailDir)
    {
        _host = host;
        _profilesDir = profilesDir;
        _thumbnailDir = thumbnailDir;
        _ephemeral = new EphemeralProfileStore(Path.Combine(profilesDir, "ephemeral"));
        _ephemeral.Sweep();
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
    /// <summary>A page asked for a new window: (the page, where to, the person clicked or typed, it is an agent's page).</summary>
    public Action<ResourceId, Uri?, bool, bool>? OnPopupRequested { get; set; }
    /// <summary>A page started a download: (the page, file name, where from, it is an agent's page) → allow it? Asked before anything is saved.</summary>
    public Func<ResourceId, string, Uri?, bool, Task<bool>>? OnDownloadRequested { get; set; }
    public string? DownloadPathOverride { get; set; }
    /// <summary>A download ended: (the page, file name, saved path, where from, it finished rather than being interrupted, this is an agent's page, the identity container and workspace the page belonged to when the download STARTED; a null container means ownership is unknown).</summary>
    public Action<ResourceId, string, string, Uri?, bool, bool, IdentityContainer?, ContextId>? OnDownloadFinished { get; set; }
    /// <summary>Resolves jev:// URLs to locally generated HTML (welcome/help). No network involved.</summary>
    public Func<Uri, string?>? LocalPage { get; set; }

    /// <summary>
    /// One profile per identity. Persistent containers share one profile each; EPHEMERAL containers get a profile per
    /// workspace, so two Private tabs sessions or two agent sessions never share cookies or storage.
    /// </summary>
    public async Task<CoreWebView2Environment> GetEnvironmentAsync(IdentityContainer container, ContextId isolationKey)
    {
        var key = IdentityKey(container, isolationKey);
        ThrowIfEnded(key);
        if (_envs.TryGetValue(key, out var env)) return env;
        var udf = container.IsEphemeral()
            ? _ephemeralPaths.GetValueOrDefault(key) ?? (_ephemeralPaths[key] = _ephemeral.Create())
            : Path.Combine(_profilesDir, container.ToString().ToLowerInvariant());
        Directory.CreateDirectory(udf);
        env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        ThrowIfEnded(key);
        _runningBrowsers[key] = [];
        env.BrowserProcessExited += (_, args) =>
        {
            if (_runningBrowsers.TryGetValue(key, out var running)) running.Remove(args.BrowserProcessId);
            // A browser process that FAILED leaves an environment object that cannot serve new controls. Forget it, so the next renderer for this identity
            // creates a fresh one on the same profile folder (a normal exit, after the last control closed, keeps working with the same object).
            if (args.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed) _envs.Remove(key);
        };
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
        var key = IdentityKey(container, isolationKey);
        ThrowIfEnded(key);
        var env = await GetEnvironmentAsync(container, isolationKey);
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        _host.Children.Add(view);
        WebView2Lease? lease = null;
        try
        {
            await view.EnsureCoreWebView2Async(env);
            _runningBrowsers[key].Add(view.CoreWebView2.BrowserProcessId);
            ThrowIfEnded(key);
            if (OnCoreCreated is not null) await OnCoreCreated(view.CoreWebView2, id, container, isolationKey, initialUrl);
            lease = await WebView2Lease.CreateAsync(id, view, _thumbnailDir);
            ThrowIfEnded(key);
            // In force before the first Navigate below, so an allowed URL that redirects out of scope is stopped.
            if (_navPolicies.TryGetValue(id, out var policy)) lease.NavigationGuard = policy;
            lease.PopupRequested = (target, userInitiated, agent) => OnPopupRequested?.Invoke(id, target, userInitiated, agent);
            lease.DownloadGate = (name, source, agent) => OnDownloadRequested is { } ask ? ask(id, name, source, agent) : Task.FromResult(true);
            lease.DownloadPathOverride = DownloadPathOverride;
            lease.OwnerContainer = container; lease.OwnerWorkspace = isolationKey;   // fixed now: the page may be gone by the time a download ends
            lease.DownloadFinished = (name, path, source, ok, agent) => OnDownloadFinished?.Invoke(id, name, path, source, ok, agent, lease.OwnerContainer, lease.OwnerWorkspace);
            // Before anything else reacts: an environment whose browser process died cannot create new controls, so forget it NOW (the environment's own
            // exit event can arrive later than the failure that triggers the page's recovery).
            lease.EngineFailed += f => { if (f.WholeEngine) _envs.Remove(key); };
            _live[id] = lease;
            _identities[id] = key;
            if (initialUrl.Scheme == "jev" && LocalPage is not null && LocalPage(initialUrl) is { } html) view.CoreWebView2.NavigateToString(html);
            else view.CoreWebView2.Navigate(initialUrl.ToString());
            return lease;
        }
        catch
        {
            lease?.CleanupHostState();
            _host.Children.Remove(view);
            view.Close();
            _live.Remove(id);
            _identities.Remove(id);
            OnCoreDisposed?.Invoke(id);
            throw;
        }
    }

    public async Task ReleaseAsync(ResourceId id, ReleaseDisposition disposition, CancellationToken ct)
    {
        if (!_live.TryGetValue(id, out var lease)) return;
        if (disposition == ReleaseDisposition.Suspend) { await lease.TrySuspendAsync(); return; }
        lease.CleanupHostState();   // before the view goes: stops the media timer and drops its reference to the lease
        _host.Children.Remove(lease.View);
        lease.View.Close();
        _live.Remove(id);
        _identities.Remove(id);
        OnCoreDisposed?.Invoke(id);
    }

    private static string IdentityKey(IdentityContainer container, ContextId isolation) =>
        container.IsEphemeral() ? $"{container}:{isolation}" : container.ToString();

    private void ThrowIfEnded(string key)
    {
        if (_shutdown || _endedIdentities.Contains(key)) throw new InvalidOperationException("This renderer session has ended.");
    }

    public sealed record SessionCleanup(bool RenderersClosed, bool ProfileDataDeleted);

    public Task<SessionCleanup> EndPrivateSessionAsync(ContextId isolation) => EndEphemeralSessionAsync(IdentityContainer.Private, isolation);

    /// <summary>
    /// Ends an ephemeral identity (a Private session, or a Disposable agent workspace): no new renderer may be created for it, and once its engine
    /// processes have exited its throwaway profile folder is deleted. Not deleted yet means it stays for the start-up sweep.
    /// </summary>
    public async Task<SessionCleanup> EndEphemeralSessionAsync(IdentityContainer container, ContextId isolation)
    {
        if (!container.IsEphemeral()) throw new ArgumentException("only ephemeral identities can be ended", nameof(container));
        var key = IdentityKey(container, isolation);
        _endedIdentities.Add(key);
        if (_identities.Values.Contains(key)) return new(false, false);
        // File deletion alone is insufficient: the engine could still write its final profile updates.
        // BrowserProcessExited confirms that all associated processes and profile resources were released.
        for (var attempt = 0; attempt < 30 && _runningBrowsers.TryGetValue(key, out var running) && running.Count > 0; attempt++)
            await Task.Delay(100);
        if (_runningBrowsers.TryGetValue(key, out var remaining) && remaining.Count > 0) return new(true, false);
        _envs.Remove(key);
        _runningBrowsers.Remove(key);
        if (!_ephemeralPaths.TryGetValue(key, out var path)) return new(true, true);
        var deleted = await _ephemeral.EndAsync(path);
        if (deleted) _ephemeralPaths.Remove(key);
        return new(true, deleted);
    }

    /// <summary>Close every renderer and mark this session's ephemeral profiles for deletion.</summary>
    public void Shutdown()
    {
        _shutdown = true;
        foreach (var l in _live.Values.ToList()) { l.CleanupHostState(); _host.Children.Remove(l.View); l.View.Close(); }
        _live.Clear();
        _identities.Clear();
        _envs.Clear();
        _navPolicies.Clear();
        _ephemeral.Dispose(); // failed deletions remain for the next startup sweep
    }
}

/// <summary>Where the control was while a screenshot had it staged. <c>OverlapsWindow</c> is what must be false for nothing of it to be visible.</summary>
public sealed record StagedCaptureFacts(double X, double Y, double Width, double Height, double WindowWidth, double WindowHeight, bool OverlapsWindow, bool HitTestVisible, bool TabStop);

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
            // Typing anywhere counts, a password field included: the flag says THAT something was typed, never what. (It used to skip password fields,
            // so a page holding only a half-typed password looked finished and could be put to sleep.)
            if (!e.target) return;
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
          let peers = 0, beat = null, lastSent = '', uploads = 0, videoOn = false;
          const kinds = () => (mic.size ? 'mic,' : '') + (cam.size ? 'cam,' : '') + (screen.size ? 'screen,' : '') + (peers > 0 ? 'peer,' : '') + (uploads > 0 ? 'upload,' : '') + (videoOn ? 'video' : '');
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
          // An upload in flight: a request whose body is a file, a blob, form data or raw bytes. Reported with the same heartbeat as a call, so it ends when
          // the request settles, or when the document goes away (the heartbeat stops).
          const isUploadBody = b => b != null && (b instanceof FormData || b instanceof Blob || b instanceof ArrayBuffer || ArrayBuffer.isView(b));
          const uploadStarted = () => { uploads++; send(); };
          const uploadSettled = () => { if (uploads > 0) uploads--; send(); };
          if (typeof fetch === 'function') {
            const of = window.fetch.bind(window);
            window.fetch = (input, init) => {
              const body = init && init.body;   // a Request object's own body is not inspected
              if (!isUploadBody(body)) return of(input, init);
              uploadStarted();
              const p = of(input, init);
              p.then(uploadSettled, uploadSettled);
              return p;
            };
          }
          if (window.XMLHttpRequest) {
            const os = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.send = function (body) {
              if (isUploadBody(body)) { uploadStarted(); this.addEventListener('loadend', uploadSettled, { once: true }); }
              return os.call(this, body);
            };
          }
          // A long or live video playing, muted or not. Short muted loops (hero clips, animated ads) are not reasons to keep a tab awake: it must be visible, a
          // reasonable size, and either live or longer than two minutes.
          const videoPlaying = () => {
            for (const v of document.querySelectorAll('video')) {
              // Buffering (readyState below 3) is a pause the person did not ask for: only an explicit pause or the end releases the tab.
              if (v.paused || v.ended) continue;
              if (v.offsetWidth < 320 || v.offsetHeight < 180 || v.getClientRects().length === 0) continue;
              if (!(v.duration === Infinity || Number.isNaN(v.duration) || v.duration > 120)) continue;
              return true;
            }
            return false;
          };
          const syncVideo = () => { const on = videoPlaying(); if (on !== videoOn) { videoOn = on; send(); } };
          // No timer on pages without video: it starts when a video first plays and stops once nothing qualifying is playing.
          let videoTimer = null;
          const onVideoEvent = () => {
            syncVideo();
            if (videoOn && !videoTimer) videoTimer = setInterval(() => { syncVideo(); if (!videoOn) { clearInterval(videoTimer); videoTimer = null; } }, 4000);
          };
          for (const ev of ['play', 'playing', 'pause', 'ended', 'emptied', 'loadeddata', 'waiting']) document.addEventListener(ev, onVideoEvent, true);
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
        core.SourceChanged += (_, _) => { lease.BumpDocument(); lease.RaiseNavigation(); };
        core.ContentLoading += (_, _) => lease.BumpDocument();   // a commit, including a reload of the same address
        core.DocumentTitleChanged += (_, _) => lease.RaiseNavigation();
        core.NavigationStarting += (_, e) =>
        {
            // Agent scope is enforced HERE, on every navigation the page attempts (link clicks, redirects, scripts).
            if (lease.NavigationGuard is { } guard && Uri.TryCreate(e.Uri, UriKind.Absolute, out var dest) && dest.Scheme is "http" or "https" && !guard(dest)) { e.Cancel = true; return; }
            if (e.Cancel) return;
            lease.BumpDocument();
            // A real navigation from here ends whatever used to be ahead in the saved history (as in any browser); Back/Forward traversals and our own replace do not.
            if (!e.IsRedirected && e.NavigationKind == CoreWebView2NavigationKind.NewDocument)
            {
                // Two loads are not "the person went somewhere new": the one that brought the page back after sleep, and our own replace for Back/Forward. Each is matched by
                // its address and used up, so an event that arrives late can never let a real navigation slip through, or swallow one.
                if (e.Uri == lease._restoreUrl) lease._restoreUrl = null;
                else if (e.Uri == lease._replaceUrl) lease._replaceUrl = null;
                else lease._sleep.ClearForward();
            }
            if (!e.IsRedirected) lease.ResetSignals();
        };
        // Typed input is unfinished work until its DOCUMENT is replaced (ContentLoading, below), NOT until it finishes loading: a person can type into a slow page
        // while it is still loading, and completion must not forget that.
        core.NavigationCompleted += (_, _) => { lease.Loaded?.Invoke(); };
        // The engine's default for a new-window request is to open an UNMANAGED window that skips renderer admission, Shield and the permission adapter.
        // It is never allowed to: the request is always handled here, and the app decides whether it becomes a managed tab or is refused.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var target);
            lease.PopupRequested?.Invoke(target, e.IsUserInitiated, lease.NavigationGuard is not null);
        };
        core.IsDocumentPlayingAudioChanged += (_, _) => lease.SetDetected(ProtectionFlags.Audible, core.IsDocumentPlayingAudio);
        core.DownloadStarting += (_, e) =>
        {
            var gate = lease.DownloadGate;
            var deferral = gate is null ? null : e.GetDeferral();
            void Track(Uri? source)
            {
                lease._activeDownloads++;
                lease.SetDetected(ProtectionFlags.DownloadActive, true);
                e.DownloadOperation.StateChanged += (d, _) =>
                {
                    if (d.State == CoreWebView2DownloadState.InProgress) return;
                    try { var saved = d.ResultFilePath ?? ""; lease.DownloadFinished?.Invoke(Path.GetFileName(saved) is { Length: > 0 } fn ? fn : "a file", saved, source, d.State == CoreWebView2DownloadState.Completed, lease.NavigationGuard is not null); } catch (Exception) { }
                    if (--lease._activeDownloads <= 0) { lease._activeDownloads = 0; lease.SetDetected(ProtectionFlags.DownloadActive, false); }
                };
            }
            if (gate is null) { Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out var src0); Track(src0); return; }
            // The person (or the policy) is asked BEFORE anything is written. Cancel and failure both mean nothing is saved.
            var pending = Decide();   // observed inside: it never throws
            async Task Decide()
            {
                try
                {
                    var name = Path.GetFileName(e.ResultFilePath ?? "") is { Length: > 0 } n ? n : "a file";
                    Uri.TryCreate(e.DownloadOperation.Uri, UriKind.Absolute, out var source);
                    if (!await gate(name, source, lease.NavigationGuard is not null)) { e.Cancel = true; return; }
                    if (lease.DownloadPathOverride is { } path) { e.ResultFilePath = Path.Combine(path, Path.GetFileName(e.ResultFilePath ?? "download.bin")); e.Handled = true; }
                    Track(source);
                }
                catch (Exception) { try { e.Cancel = true; } catch (Exception) { } }
                finally { try { deferral!.Complete(); } catch (Exception) { } }
            }
        };
        void OnCoreMessage(CoreWebView2 _, CoreWebView2WebMessageReceivedEventArgs e)
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
        }
        core.WebMessageReceived += OnCoreMessage;
        lease._detach.Add(() => { try { core.WebMessageReceived -= OnCoreMessage; } catch (Exception) { } });
        // A call can live in an iframe (embedded Meet, a widget). CoreWebView2.WebMessageReceived only carries the
        // TOP-LEVEL document's messages, so without this an embedded, microphone-only call reports nothing at all
        // and gets hibernated. Each frame reports under its own document id, and destroying a frame is the evidence
        // that its media is gone — one frame stopping never touches a sibling's protection.
        //
        // Frames nest. CoreWebView2Frame raises its own FrameCreated for children, so this subscribes recursively:
        // a call two iframes deep is still a call.
        void Watch(CoreWebView2Frame frame)
        {
            // Every subscription this lease makes is paired with the way to undo it, so cleanup can be terminal
            // rather than hopeful. Frames come and go, so their handlers are registered here too.
            void OnFrameMessage(CoreWebView2Frame _, CoreWebView2WebMessageReceivedEventArgs me)
            {
                try
                {
                    var msg = me.TryGetWebMessageAsString();
                    // The page script runs in every frame and reports secret and payment fields the same way. These used to be dropped here
                    // (only media was routed), so a password field in an iframe never reached the guards.
                    if (msg == "jev:secret-field") lease.AddFrameSignal(frame, PageSignals.PasswordField);
                    else if (msg == "jev:payment-field") lease.AddFrameSignal(frame, PageSignals.PaymentField);
                    else if (msg == "jev:dirty-form") lease.SetDetected(ProtectionFlags.DirtyForm, true);   // typing inside an iframe (a payment or comment widget)
                    else lease.OnMediaMessage(msg, frame);
                }
                catch (Exception) { }
            }
            frame.WebMessageReceived += OnFrameMessage;
            lease._detach.Add(() => { try { frame.WebMessageReceived -= OnFrameMessage; } catch (Exception) { } });
            frame.Destroyed += (_, _) => { lease.DropFrameMedia(frame); lease.DropFrameSignals(frame); };
            // A frame can also REPLACE its document without being destroyed: the old document is gone, and it never
            // sent a media-end. Without this its entry stays uncertain forever and the tab never sleeps again.
            // ContentLoading commits, so a cancelled navigation inside the frame leaves a running call alone.
            frame.ContentLoading += (_, _) => { lease.DropFrameMedia(frame); lease.DropFrameSignals(frame); };
            frame.FrameCreated += (_, child) => Watch(child.Frame);
        }
        void OnFrameCreated(CoreWebView2 _, CoreWebView2FrameCreatedEventArgs fe) => Watch(fe.Frame);
        core.FrameCreated += OnFrameCreated;
        lease._detach.Add(() => { try { core.FrameCreated -= OnFrameCreated; } catch (Exception) { } });

        // Confirmed replacement of the top-level document, on the same commit-not-intent basis.
        void OnContentLoading(CoreWebView2 _, CoreWebView2ContentLoadingEventArgs __) { lease.DropTopLevelMedia(); lease.ClearDetected(ProtectionFlags.DirtyForm); }   // the document was replaced: its typing went with it
        core.ContentLoading += OnContentLoading;
        lease._detach.Add(() => { try { core.ContentLoading -= OnContentLoading; } catch (Exception) { } });

        // ProcessFailed is NOT a synonym for "the renderer died". It also fires for an unresponsive renderer — which
        // a long script can cause while the process is perfectly alive and still capturing — and for GPU and
        // individual frame-renderer failures. Clearing on all of them would undo the stall fix by another route.
        // Only an actually exited process is evidence that its documents are gone; everything else leaves the
        // entries in place, uncertain, which is the safe direction.
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
                                    or CoreWebView2ProcessFailedKind.RenderProcessExited)
            {
                lease.DropAllMedia();
                // The registered lease would otherwise be handed back on the next activation while its control is dead (WebView2 requires recreating
                // the control after BrowserProcessExited). Tell the kernel, which releases it and brings the page back on a new one.
                lease.EngineFailed?.Invoke(new EngineFailure(e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited, e.ProcessFailedKind.ToString()));
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript);
        return lease;
    }

    public WebView2 View { get; }
    public ResourceId ResourceId { get; }
    public bool IsSuspended => View.CoreWebView2?.IsSuspended ?? false;
    private bool _kernelVisible;
    /// <summary>What the kernel asked for. Not the control's literal state: a screenshot may stage the control briefly, and that must never read as "shown".</summary>
    public bool IsVisible => _kernelVisible;
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
        _kernelVisible = visible;
        if (_captureStaged) return;   // the capture puts the control back to what the kernel wants when it finishes
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

    /// <summary>
    /// Asks the engine to render the page's own surface into memory (DevTools Page.captureScreenshot). That works for a page that is
    /// not shown and needs no focus, unlike a preview of the control on screen; it does not touch visibility, focus or the thumbnail
    /// path (which Trust OS refuses for private and disposable containers, and which this deliberately does not enable).
    /// </summary>
    private bool _captureStaged;
    private long _documentGeneration;
    public long DocumentGeneration => Interlocked.Read(ref _documentGeneration);
    private void BumpDocument() => Interlocked.Increment(ref _documentGeneration);

    /// <summary>
    /// Told, while a capture has the control staged, where it actually is: its rectangle relative to the window's content, the window's
    /// size, and whether it can take clicks or focus. Lets a check measure "nothing of it can be seen or reached" as geometry instead of
    /// assuming it. Null in normal use.
    /// </summary>
    public static Action<StagedCaptureFacts>? StagedObserver { get; set; }

    /// <summary>
    /// A control that is not shown produces no frames, and a capture waits for a frame: on the real engine every way of asking a
    /// collapsed WebView2 for a picture (as is, lifecycle "active", focus emulation, an emulated viewport) timed out. So a page that
    /// is not shown is drawn for a moment OFF-CANVAS: given a real size and placed far outside the window's client area, where it
    /// is clipped and nothing of it can be seen, cannot be clicked and cannot take focus, then put back exactly as it was. A page the
    /// kernel is already showing is simply captured. Never the thumbnail path, never a file.
    /// </summary>
    public async Task<ScreenshotResult> CaptureScreenshotAsync(CancellationToken ct)
    {
        var core = View.CoreWebView2;
        if (core is null) return new(null, "the renderer is gone");
        if (_kernelVisible) return await CaptureFromEngineAsync(core, 6, ct);
        if (_captureStaged) return new(null, "a picture is already being taken of this page");
        var view = View;
        var saved = (view.Visibility, view.Width, view.Height, view.Margin, view.HorizontalAlignment, view.VerticalAlignment, view.IsHitTestVisible, view.IsTabStop);
        _captureStaged = true;
        try
        {
            view.IsHitTestVisible = false;
            view.IsTabStop = false;
            view.HorizontalAlignment = HorizontalAlignment.Left;
            view.VerticalAlignment = VerticalAlignment.Top;
            view.Width = 1280;
            view.Height = 800;
            view.Margin = new Thickness(-30000, -30000, 0, 0);
            view.Visibility = Visibility.Visible;
            await Task.Delay(200, ct);                        // long enough for the engine to produce a first frame
            if (StagedObserver is { } observe && view.XamlRoot?.Content is UIElement root)
            {
                var bounds = view.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, view.Width, view.Height));
                var window = new Windows.Foundation.Rect(0, 0, root.ActualSize.X, root.ActualSize.Y);
                var overlaps = bounds.X < window.Width && bounds.X + bounds.Width > 0 && bounds.Y < window.Height && bounds.Y + bounds.Height > 0;
                observe(new StagedCaptureFacts(bounds.X, bounds.Y, bounds.Width, bounds.Height, window.Width, window.Height, overlaps, view.IsHitTestVisible, view.IsTabStop));
            }
            return await CaptureFromEngineAsync(core, 8, ct);
        }
        finally
        {
            view.Visibility = Visibility.Collapsed;           // first, so nothing flashes while the rest is put back
            view.Margin = saved.Margin; view.Width = saved.Width; view.Height = saved.Height;
            view.HorizontalAlignment = saved.HorizontalAlignment; view.VerticalAlignment = saved.VerticalAlignment;
            view.IsHitTestVisible = saved.IsHitTestVisible; view.IsTabStop = saved.IsTabStop;
            _captureStaged = false;
            view.Visibility = _kernelVisible ? Visibility.Visible : Visibility.Collapsed;   // what the kernel wants now, which may have changed meanwhile
        }
    }

    private static async Task<ScreenshotResult> CaptureFromEngineAsync(CoreWebView2 core, int seconds, CancellationToken ct)
    {
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"fromSurface\":true}").AsTask().WaitAsync(TimeSpan.FromSeconds(seconds), ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } b64) return new(Convert.FromBase64String(b64), "captured");
            return new(null, "the engine returned no image");
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return new(null, "the page did not produce a picture in time"); }
        catch (Exception ex) { return new(null, "capture failed: " + ex.Message); }
    }

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
        var history = await ReadHistoryAsync(core, ct);
        kept |= PreservedParts.Address;                                    // we got this far, so core.Source is real
        if (thumb is not null) kept |= PreservedParts.Preview;
        // A class that forbids thumbnails is not a shortfall: nothing was lost, the policy said not to keep pixels.
        if (!AllowThumbnails) kept |= PreservedParts.Preview;
        if (outcome == CaptureOutcome.Captured && gaps.Count > 0) outcome = CaptureOutcome.Partial;
        return new(cp, outcome, gaps.Count == 0 ? "address, position and preview" : string.Join("; ", gaps), kept, history);
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

    // ---- Back and Forward across sleep ----
    private readonly JevBrowse.VirtualTabs.SleepHistory _sleep = new();
    private string? _replaceUrl;

    private string? _restoreUrl;

    public void SeedHistory(NavHistory history)
    {
        _sleep.Seed(history);
        _restoreUrl = history.Entries.Count > history.Index ? history.Entries[history.Index].Url : null;   // the wake-up load itself must not end Forward
    }

    /// <summary>The renderer's own history plus what came before and after the page this renderer woke on. Null if it cannot be read (then nothing extra is kept).</summary>
    private async Task<NavHistory?> ReadHistoryAsync(CoreWebView2 core, CancellationToken ct)
    {
        try
        {
            var call = core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}").AsTask();
            if (await Task.WhenAny(call, Task.Delay(CaptureTimeout, ct)) != call) return null;
            using var doc = JsonDocument.Parse(await call);
            var index = doc.RootElement.GetProperty("currentIndex").GetInt32();
            var live = doc.RootElement.GetProperty("entries").EnumerateArray()
                .Select(e => new HistoryEntry(e.GetProperty("url").GetString() ?? "", e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "")).ToList();
            return live.Count == 0 ? null : _sleep.Flatten(live, index);
        }
        catch (Exception) { return null; }
    }

    private HistoryEntry CurrentEntry() => new(View.CoreWebView2.Source ?? "", View.CoreWebView2.DocumentTitle ?? "");

    public bool CanGoBackAcrossSleep => View.CanGoBack || _sleep.CanGoBackFromLiveStart;
    public bool CanGoForwardAcrossSleep => (!View.CanGoBack && _sleep.CanGoForwardFromLiveStart) || View.CanGoForward;

    public void GoBackAcrossSleep()
    {
        if (View.CanGoBack) { View.GoBack(); return; }
        if (_sleep.TakeBack(CurrentEntry()) is { } prev) ReplaceCurrentPage(prev.Url);
    }

    public void GoForwardAcrossSleep()
    {
        if (!View.CanGoBack && _sleep.TakeForward(CurrentEntry()) is { } next) { ReplaceCurrentPage(next.Url); return; }
        if (View.CanGoForward) View.GoForward();
    }

    /// <summary>Loads an address in place of the current page (no new live history entry), so the live history never holds two copies of a page.</summary>
    private void ReplaceCurrentPage(string url)
    {
        _replaceUrl = url;
        _ = View.CoreWebView2.ExecuteScriptAsync("location.replace(" + JsonSerializer.Serialize(url) + ")");
        _ = Task.Delay(15000).ContinueWith(_ => { if (_replaceUrl == url) _replaceUrl = null; }, TaskScheduler.Default);   // never stays set if the navigation did not happen
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
    public event Action<EngineFailure>? EngineFailed;
    /// <summary>(address, the person did it with a click or key, this is an agent's page). Set by the manager.</summary>
    public Action<Uri?, bool, bool>? PopupRequested { get; set; }
    /// <summary>(file name, where from, this is an agent's page) → may it be saved? Set by the manager; null = no question is asked.</summary>
    public Func<string, Uri?, bool, Task<bool>>? DownloadGate { get; set; }
    /// <summary>Measurement only: save into this folder without the engine's own save UI.</summary>
    public string? DownloadPathOverride { get; set; }
    /// <summary>(file name, saved path, where from, finished, this is an agent's page). Set by the manager.</summary>
    public Action<string, string, Uri?, bool, bool>? DownloadFinished { get; set; }
    /// <summary>The identity the page belonged to when it was created. Null = unknown.</summary>
    public IdentityContainer? OwnerContainer { get; set; }
    public ContextId OwnerWorkspace { get; set; }
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
    internal bool HostStateReleased => _disposed && _media.Count == 0 && _mediaSweeper is null && _detach.Count == 0;

    /// <summary>
    /// Terminal. Once cleanup has run the lease accepts nothing further: a message already queued on the dispatcher
    /// must not repopulate tracking or restart the timer after the tab is gone. Cleanup that can be undone by a
    /// late callback is not cleanup.
    /// </summary>
    private bool _disposed;
    private readonly List<Action> _detach = [];

    internal void OnMediaMessage(string? msg, CoreWebView2Frame? frame = null)
    {
        if (_disposed || msg is null) return;
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
            "upload" => ProtectionFlags.UploadActive,
            "video" => ProtectionFlags.VideoPlaying,
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
        if (_disposed) return;          // idempotent: release and shutdown can both reach here
        _disposed = true;               // set FIRST, so anything still in flight is rejected on the way in
        _mediaSweeper?.Stop();
        _mediaSweeper = null;
        _media.Clear();
        _detected = ProtectionFlags.None;
        _signals = PageSignals.None;
        NavigationGuard = null;
        AllowThumbnails = false;
        _lastThumbnail = null;
        foreach (var undo in _detach) undo();
        _detach.Clear();
        DetectedProtectionChanged = null;
        PageSignalsChanged = null;
        NavigationChanged = null;
        Loaded = null;
        EngineFailed = null;
        PopupRequested = null;
        DownloadGate = null;
        DownloadFinished = null;
    }

    internal void DropFrameMedia(CoreWebView2Frame frame)
    {
        var gone = _media.Where(kv => ReferenceEquals(kv.Value.Frame, frame)).Select(kv => kv.Key).ToList();
        foreach (var k in gone) _media.Remove(k);
        if (gone.Count > 0) ApplyMedia();
    }

    private void StartSweeper()
    {
        if (_disposed || _mediaSweeper is not null) return;
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
        var next = (_detected & ~ProtectionFlagsExtensions.PageReported) | all;
        if (next == _detected) return;
        _detected = next;
        DetectedProtectionChanged?.Invoke(_detected);
    }

    public event Action<ProtectionFlags>? DetectedProtectionChanged;
    public event Action<PageSignals>? PageSignalsChanged;

    private void RaiseNavigation()
    {
        if (_disposed) return;
        var core = View.CoreWebView2;
        if (Uri.TryCreate(core.Source, UriKind.Absolute, out var u))
            NavigationChanged?.Invoke(new NavigationInfo(u, core.DocumentTitle));
    }

    private void SetDetected(ProtectionFlags flag, bool on)
    {
        if (_disposed) return;
        var next = on ? _detected | flag : _detected & ~flag;
        if (next == _detected) return;
        _detected = next;
        DetectedProtectionChanged?.Invoke(_detected);
    }

    private void ClearDetected(ProtectionFlags flag) => SetDetected(flag, false);

    // The top document's own signals live in _signals (the handlers above add to it); frames contribute through the tracker, and what
    // is published is the sum, so a password or payment field anywhere on the page, in any frame, is on screen and is reported.
    private readonly PageSignalTracker<CoreWebView2Frame> _tracker = new();
    private PageSignals _published;

    private void SetSignals(PageSignals s)
    {
        if (_disposed) return;
        _signals = s;
        _tracker.SetTop(s);
        Publish();
    }

    private void Publish()
    {
        var effective = _tracker.Effective;
        if (effective == _published) return;
        _published = effective;
        PageSignalsChanged?.Invoke(effective);
    }

    private void AddFrameSignal(CoreWebView2Frame frame, PageSignals flag)
    {
        if (_disposed) return;
        _tracker.AddFrame(frame, flag);
        Publish();
    }

    private void DropFrameSignals(CoreWebView2Frame frame)
    {
        if (_disposed) return;
        _tracker.DropFrame(frame);
        Publish();
    }

    private void ResetSignals()
    {
        if (_disposed) return;
        _signals = PageSignals.None;
        _tracker.Reset();
        Publish();
    }
}
