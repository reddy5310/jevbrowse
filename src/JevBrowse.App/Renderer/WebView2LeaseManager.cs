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
    private readonly Dictionary<IdentityContainer, CoreWebView2Environment> _envs = [];
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

    /// <summary>Called once per new CoreWebView2 before its first navigation. Shield and Trust OS attach here.</summary>
    public Action<CoreWebView2, ResourceId, IdentityContainer>? OnCoreCreated { get; set; }
    public Action<ResourceId>? OnCoreDisposed { get; set; }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync(IdentityContainer container)
    {
        if (_envs.TryGetValue(container, out var env)) return env;
        var udf = container.IsEphemeral()
            ? Path.Combine(_profilesDir, "ephemeral", $"{container.ToString().ToLowerInvariant()}-{_sessionTag}")
            : Path.Combine(_profilesDir, container.ToString().ToLowerInvariant());
        Directory.CreateDirectory(udf);
        env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        _envs[container] = env;
        return env;
    }

    public bool TryGet(ResourceId id, out IRendererLease lease)
    {
        var ok = _live.TryGetValue(id, out var l);
        lease = l!;
        return ok;
    }

    public async Task<IRendererLease> AcquireAsync(ResourceId id, Uri initialUrl, RenderIntent intent, IdentityContainer container, CancellationToken ct)
    {
        var env = await GetEnvironmentAsync(container);
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        _host.Children.Add(view);
        await view.EnsureCoreWebView2Async(env);
        OnCoreCreated?.Invoke(view.CoreWebView2, id, container);
        var lease = await WebView2Lease.CreateAsync(id, view, _thumbnailDir);
        _live[id] = lease;
        view.CoreWebView2.Navigate(initialUrl.ToString());
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
            if (document.querySelector('input[type="password"]')) post('jev:secret-field');
            if (document.querySelector('input[autocomplete^="cc-"]')) post('jev:payment-field');
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', scan); else scan();
          new MutationObserver(() => scan()).observe(document.documentElement, { childList: true, subtree: true });
        })();
        """;

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

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
        core.NavigationStarting += (_, e) => { if (!e.IsRedirected) lease.SetSignals(PageSignals.None); };
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
    public void SetVisible(bool visible)
    {
        if (!visible && IsVisible && View.CoreWebView2 is not null)
            _pendingThumbnail = CaptureThumbnailAsync();
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task CaptureThumbnailAsync()
    {
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
            File.Move(tmp, path, overwrite: true); // atomic replace: never a half-written thumbnail
            _lastThumbnail = path;
        }
        catch (Exception) { /* thumbnail is disposable (§16); a miss is not an error */ }
    }

    public async Task<bool> TrySuspendAsync()
    {
        if (View.CoreWebView2 is null) return false;
        SetVisible(false); // WebView2 refuses to suspend a visible view
        try { return await View.CoreWebView2.TrySuspendAsync(); }
        catch (Exception) { return false; }
    }

    public void Resume() => View.CoreWebView2?.Resume();

    public async Task<Checkpoint> CaptureCheckpointAsync(string thumbnailDir, CancellationToken ct)
    {
        var core = View.CoreWebView2;
        double sx = 0, sy = 0; string? favicon = null;
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
        }
        catch (Exception) { /* page may be mid-navigation; scroll is best-effort */ }

        // Thumbnail: use the one taken on deactivation; if the view is still visible, take a fresh one now.
        if (IsVisible) _pendingThumbnail = CaptureThumbnailAsync();
        if (_pendingThumbnail is { } pending) await Task.WhenAny(pending, Task.Delay(CaptureTimeout, ct));

        var url = Uri.TryCreate(core.Source, UriKind.Absolute, out var u) ? u : new Uri("about:blank");
        return new Checkpoint(ResourceId, url, core.DocumentTitle, sx, sy, favicon, _lastThumbnail, DateTimeOffset.UtcNow);
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
