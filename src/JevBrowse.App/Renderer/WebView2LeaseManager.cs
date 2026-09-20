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
        _host.Children.Remove(lease.View);
        lease.View.Close();
        OnCoreDisposed?.Invoke(id);
    }

    /// <summary>Close every renderer and mark this session's ephemeral profiles for deletion.</summary>
    public void Shutdown()
    {
        foreach (var l in _live.Values.ToList()) { _host.Children.Remove(l.View); l.View.Close(); }
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
            // Positive evidence the page is public. Only reported once the document has real content, so an empty
            // shell cannot earn PUBLIC before its app has rendered.
            if (!pw && !cc && !out && document.body && (document.body.innerText || '').trim().length > 200) post('jev:public-evidence');
          };
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
                case "jev:authenticated": lease.SetSignals((lease._signals | PageSignals.Authenticated) & ~PageSignals.PublicEvidence); break;
                case "jev:public-evidence": if (!lease._signals.HasFlag(PageSignals.Authenticated)) lease.SetSignals(lease._signals | PageSignals.PublicEvidence); break;
            }
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
        try
        {
            var script = core.ExecuteScriptAsync("JSON.stringify({x:window.scrollX,y:window.scrollY,f:(document.querySelector('link[rel~=\"icon\"]')||{}).href||null})").AsTask();
            if (await Task.WhenAny(script, Task.Delay(CaptureTimeout, ct)) == script)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(await script) ?? "{}");
                sx = doc.RootElement.GetProperty("x").GetDouble();
                sy = doc.RootElement.GetProperty("y").GetDouble();
                favicon = doc.RootElement.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
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
        var cp = new Checkpoint(ResourceId, url, core.DocumentTitle, sx, sy, favicon, AllowThumbnails ? _lastThumbnail : null, DateTimeOffset.UtcNow);
        if (outcome == CaptureOutcome.Captured && gaps.Count > 0) outcome = CaptureOutcome.Partial;
        return new(cp, outcome, gaps.Count == 0 ? "address, position and preview" : string.Join("; ", gaps));
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
