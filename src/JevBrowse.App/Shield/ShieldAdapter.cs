using System.Collections.Concurrent;
using JevBrowse.Domain;
using JevBrowse.Shield;
using JevBrowse.Storage;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.Shield;

public sealed class TabShieldStats
{
    public int Total, Blocked;
    public readonly ConcurrentQueue<(string Host, string Rule)> Recent = new();
    public void Record(string host, string rule) { Recent.Enqueue((host, rule)); while (Recent.Count > 50) Recent.TryDequeue(out _); }
}

/// <summary>
/// The narrow adapter between WebView2's WebResourceRequested surface and the engine (§9, ADR 0001).
/// Runs synchronously on the request path: no I/O, no awaits, no AI. Per-site enable state is cached in memory.
/// </summary>
public sealed class ShieldAdapter
{
    private readonly SiteSettingsRepository _sites;
    private readonly HashSet<string> _disabledSites;
    private volatile FilterEngine _engine = FilterEngine.Compile([]);

    public ShieldAdapter(SiteSettingsRepository sites)
    {
        _sites = sites;
        _disabledSites = new HashSet<string>(sites.DisabledSites(), StringComparer.OrdinalIgnoreCase);
    }

    public ConcurrentDictionary<ResourceId, TabShieldStats> Stats { get; } = new();
    public int RuleCount => _engine.RuleCount;
    public void SetEngine(FilterEngine engine) => _engine = engine;

    public bool IsEnabledFor(string site) => !_disabledSites.Contains(site);
    public void SetEnabledFor(string site, bool enabled)
    {
        lock (_disabledSites) { if (enabled) _disabledSites.Remove(site); else _disabledSites.Add(site); }
        _sites.SetShieldEnabled(site, enabled);
    }

    public void Attach(CoreWebView2 core, ResourceId id)
    {
        var stats = Stats.GetOrAdd(id, _ => new TabShieldStats());
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            // V1 never blocks top-level navigations: a wrong rule must not make a site unreachable (§9 conservative).
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url)) return;
            Uri.TryCreate(core.Source, UriKind.Absolute, out var top);
            stats.Total++;
            if (top is not null && !IsEnabledFor(NetworkRequest.SiteOf(top.Host))) return;

            var decision = _engine.Evaluate(new NetworkRequest(url, top, Map(e.ResourceContext)));
            if (decision.Verdict != Verdict.Block) return;
            stats.Blocked++;
            stats.Record(url.Host, decision.Rule ?? "");
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by JevBrowse Shield", "Content-Type: text/plain");
        };
    }

    public void Detach(ResourceId id) => Stats.TryRemove(id, out _);

    private static RequestType Map(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Document => RequestType.Document,
        CoreWebView2WebResourceContext.Stylesheet => RequestType.Stylesheet,
        CoreWebView2WebResourceContext.Image => RequestType.Image,
        CoreWebView2WebResourceContext.Media => RequestType.Media,
        CoreWebView2WebResourceContext.Font => RequestType.Font,
        CoreWebView2WebResourceContext.Script => RequestType.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Fetch => RequestType.XmlHttpRequest,
        CoreWebView2WebResourceContext.Websocket => RequestType.WebSocket,
        CoreWebView2WebResourceContext.Ping or CoreWebView2WebResourceContext.CspViolationReport => RequestType.Ping,
        _ => RequestType.Other,
    };
}
