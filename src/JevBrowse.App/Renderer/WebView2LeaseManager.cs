using System.Text.Json;
using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace JevBrowse.App.Renderer;

/// <summary>
/// The only place in the app that knows a "renderer" is a WebView2 control. Lives in the App project for now
/// because WinUI controls need the windows TFM; will move to JevBrowse.Renderer.WebView2 when a second host appears.
/// </summary>
public sealed class WebView2LeaseManager : IRendererLeaseManager
{
    private readonly Dictionary<ResourceId, WebView2Lease> _live = [];
    private readonly Panel _host;
    private readonly CoreWebView2Environment _env;

    private readonly string _thumbnailDir;

    public WebView2LeaseManager(Panel host, CoreWebView2Environment env, string thumbnailDir)
    {
        _host = host;
        _env = env;
        _thumbnailDir = thumbnailDir;
    }

    public int MaxLive { get; set; } = 5;
    public IReadOnlyCollection<ResourceId> LiveResources => _live.Keys;
    public IEnumerable<int> ProcessIds => _env.GetProcessInfos().Select(p => p.ProcessId);

    /// <summary>Called once per new CoreWebView2 before its first navigation. Shield and Trust OS attach here.</summary>
    public Action<CoreWebView2, ResourceId>? OnCoreCreated { get; set; }
    public Action<ResourceId>? OnCoreDisposed { get; set; }

    public bool TryGet(ResourceId id, out IRendererLease lease)
    {
        var ok = _live.TryGetValue(id, out var l);
        lease = l!;
        return ok;
    }

    public async Task<IRendererLease> AcquireAsync(ResourceId id, Uri initialUrl, RenderIntent intent, CancellationToken ct)
    {
        var view = new WebView2 { Visibility = Visibility.Collapsed };
        _host.Children.Add(view);
        await view.EnsureCoreWebView2Async(_env);
        OnCoreCreated?.Invoke(view.CoreWebView2, id);
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
}

public sealed class WebView2Lease : IRendererLease
{
    // Narrow, origin-agnostic bridge (Table A.11): the page can only tell us one boolean. No host objects are exposed.
    private const string DirtyFormScript = """
        (() => {
          if (window.__jevDirtyHooked) return; window.__jevDirtyHooked = true;
          let dirty = false;
          const mark = e => {
            const t = e.target; if (!t || t.type === 'password') return;
            if (!dirty) { dirty = true; try { chrome.webview.postMessage('jev:dirty-form'); } catch {} }
          };
          document.addEventListener('input', mark, true);
          document.addEventListener('change', mark, true);
        })();
        """;

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(3);

    private readonly string _thumbnailDir;
    private ProtectionFlags _detected;
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
            if (msg == "jev:dirty-form") lease.SetDetected(ProtectionFlags.DirtyForm, true);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(DirtyFormScript);
        return lease;
    }

    public WebView2 View { get; }
    public ResourceId ResourceId { get; }
    public bool IsSuspended => View.CoreWebView2?.IsSuspended ?? false;
    public bool IsVisible => View.Visibility == Visibility.Visible;
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
    public void Navigate(Uri url) => View.CoreWebView2.Navigate(url.ToString());

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
}
