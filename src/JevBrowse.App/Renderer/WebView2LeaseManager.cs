using JevBrowse.Domain;
using JevBrowse.Renderer.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

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

    public WebView2LeaseManager(Panel host, CoreWebView2Environment env)
    {
        _host = host;
        _env = env;
    }

    public int MaxLive { get; set; } = 5;
    public IReadOnlyCollection<ResourceId> LiveResources => _live.Keys;
    public IEnumerable<int> ProcessIds => _env.GetProcessInfos().Select(p => p.ProcessId);

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
        var lease = new WebView2Lease(id, view);
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
    }
}

public sealed class WebView2Lease : IRendererLease
{
    public WebView2Lease(ResourceId id, WebView2 view)
    {
        ResourceId = id;
        View = view;
        var core = view.CoreWebView2;
        core.SourceChanged += (_, _) => Raise();
        core.DocumentTitleChanged += (_, _) => Raise();
        void Raise()
        {
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var u))
                NavigationChanged?.Invoke(new NavigationInfo(u, core.DocumentTitle));
        }
    }

    public WebView2 View { get; }
    public ResourceId ResourceId { get; }
    public bool IsSuspended => View.CoreWebView2?.IsSuspended ?? false;
    public bool IsVisible => View.Visibility == Visibility.Visible;
    public void SetVisible(bool visible) => View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    public void Navigate(Uri url) => View.CoreWebView2.Navigate(url.ToString());

    public async Task<bool> TrySuspendAsync()
    {
        if (View.CoreWebView2 is null) return false;
        SetVisible(false); // WebView2 refuses to suspend a visible view
        try { return await View.CoreWebView2.TrySuspendAsync(); }
        catch (Exception) { return false; }
    }

    public void Resume() => View.CoreWebView2?.Resume();
    public event Action<NavigationInfo>? NavigationChanged;
}
