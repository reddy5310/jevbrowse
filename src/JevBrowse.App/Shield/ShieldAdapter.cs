using System.Collections.Concurrent;
using JevBrowse.Domain;
using JevBrowse.Shield;
using JevBrowse.Storage;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.Shield;

public sealed class TabShieldStats
{
    public int Total, Blocked, ThirdParty, CosmeticSelectors, Collapsed, SemanticCollapsed;
    public int AdsPruned, AdsSkipped, WallSeen;
    public string? LastCollapseResult, LastSemantic;
    public List<string> SiteModules { get; } = [];
    public readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    public readonly ConcurrentQueue<(string Host, string Rule)> Recent = new();
    /// <summary>Distinct third-party hosts contacted (for "show every third party contacted by this page", §26).</summary>
    public readonly ConcurrentDictionary<string, int> ThirdPartyHosts = new();
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
    private volatile CosmeticEngine _cosmetic = CosmeticEngine.Compile([]);
    private readonly ConcurrentDictionary<string, string> _cssCache = new(StringComparer.OrdinalIgnoreCase);

    public int CosmeticGeneric => _cosmetic.GenericCount;
    public int CosmeticDomain => _cosmetic.DomainRuleCount;
    public void SetCosmetic(CosmeticEngine engine) { _cosmetic = engine; _cssCache.Clear(); }

    // Injected at document start; the page can only receive a stylesheet, never a callback. Hidden elements are
    // recoverable: the per-site switch removes the sheet on reload, and the panel lists the selector count.
    private const string CosmeticScript = """
        (() => {
          const css = __CSS__;
          const add = () => { if (document.getElementById('jev-shield-cosmetic')) return;
            const s = document.createElement('style'); s.id = 'jev-shield-cosmetic'; s.textContent = css;
            (document.head || document.documentElement).appendChild(s); };
          if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add);
        })();
        """;

    private string CssFor(string host) => _cssCache.GetOrAdd(host, h => _cosmetic.StylesheetFor(h));

    // Collapse pass (uBlock-style "collapse blocked elements"), run after load: hides frames/images whose requests
    // Shield blocked, and empty placeholder boxes whose only text is an "advertisement" label. Deterministic; the
    // host list comes from this tab's own block log, never from the page.
    private const string CollapseScript = """
        ((hosts, blockedCount) => {
          let n = 0;
          const hidden = [];
          const hide = (e, why) => { if (e && e.dataset.jevCollapsed === undefined) { e.dataset.jevCollapsed = why; e.style.setProperty('display', 'none', 'important'); n++; } };
          const hostOf = s => { try { return new URL(s, location.href).host; } catch { return ''; } };
          const adToken = /(^|[-_ :])(ad|ads|adv|advert|advertisement|advertising|gpt|dfp|sponsor|sponsored|banner|taboola|outbrain|mgid|adsense|adslot|ad-slot|adunit|ad-unit)([-_ :]|$)/i;
          const adNamed = e => { for (let x = e, i = 0; x && i < 4; x = x.parentElement, i++) { if (adToken.test(x.id || '') || adToken.test(x.className && x.className.baseVal !== undefined ? '' : (x.className || ''))) return true; } return false; };
          const ownText = e => { let t = ''; e.childNodes.forEach(c => { if (c.nodeType === 3) t += c.textContent; }); return t.trim(); };
          const labelOnly = e => /^(advertisement|advertisements|sponsored|ad)$/i.test((e.textContent || '').trim());

          // 1. Media/frames whose request Shield blocked: collapse the frame and its ad-named wrapper.
          document.querySelectorAll('iframe[src], img[src], video[src], embed[src], object[data]').forEach(e => {
            const h = hostOf(e.src || e.data || '');
            if (h && hosts.some(b => h === b || h.endsWith('.' + b))) { let w = e; for (let x = e.parentElement, i = 0; x && i < 3; x = x.parentElement, i++) if (adNamed(x)) w = x; hide(w, 'blocked-media'); }
          });

          // 2. Reserved ad boxes left empty: only when ads were actually blocked on this page (evidence), the box is
          //    ad-named or labelled "advertisement", is tall enough to matter, and holds no real text or media.
          if (blockedCount > 0) {
            document.querySelectorAll('div, section, aside, ins, li').forEach(e => {
              if (e.dataset.jevCollapsed !== undefined) return;
              const r = e.getBoundingClientRect();
              if (r.height < 90 || r.width < 120) return;
              const text = (e.textContent || '').trim();
              const hasMedia = [...e.querySelectorAll('img, video, canvas, svg, picture')].some(m => { const b = m.getBoundingClientRect(); return b.width > 40 && b.height > 40; });
              const frames = [...e.querySelectorAll('iframe')];
              const liveFrame = frames.some(f => { const h = hostOf(f.src || ''); return h && !hosts.some(b => h === b || h.endsWith('.' + b)) && f.getBoundingClientRect().height > 40 && !adToken.test(f.src || '') && !adToken.test(f.id || '') && !adToken.test(f.className || ''); });
              const emptyish = (text.length === 0 || labelOnly(e)) && !hasMedia && !liveFrame && e.querySelectorAll('a, button, input, form').length === 0;
              if (emptyish && (adNamed(e) || labelOnly(e))) hide(e, 'empty-ad-slot');
            });
          }
          return n;
        })(__HOSTS__, __BLOCKED__)
        """;

    // Residual candidates for the semantic pass: large empty boxes the structural rule left alone. Returns compact
    // structural descriptors (no text beyond a 40-char label) keyed by a per-element token we can act on later.
    private const string CandidatesScript = """
        (() => {
          const out = [];
          const hostOf = s => { try { return new URL(s, location.href).host; } catch { return ''; } };
          let k = 0;
          document.querySelectorAll('div, section, aside, li, article').forEach(e => {
            if (out.length >= 12 || e.dataset.jevCollapsed !== undefined || e.dataset.jevCand !== undefined) return;
            const r = e.getBoundingClientRect();
            if (r.height < 90 || r.width < 150 || r.bottom < 0 || r.top > innerHeight * 2) return;
            const text = (e.textContent || '').trim();
            if (text.length > 40) return;
            const media = [...e.querySelectorAll('img, video, canvas, svg, picture')].some(m => { const b = m.getBoundingClientRect(); return b.width > 40 && b.height > 40; });
            if (media || e.querySelectorAll('a, button, input').length > 0) return;
            if ([...e.children].some(c => { const b = c.getBoundingClientRect(); return b.height > 60 && (c.textContent || '').trim().length > 40; })) return;
            const frames = [...e.querySelectorAll('iframe')].map(f => hostOf(f.src || '')).filter(Boolean);
            const sib = e.parentElement ? [...e.parentElement.children].length : 0;
            const sibText = e.parentElement ? [...e.parentElement.children].filter(c => c !== e && (c.textContent || '').trim().length > 20).length : 0;
            e.dataset.jevCand = String(k);
            out.push({ k, tag: e.tagName.toLowerCase(), id: (e.id || '').slice(0, 40), cls: (typeof e.className === 'string' ? e.className : '').slice(0, 80), w: Math.round(r.width), h: Math.round(r.height), top: Math.round(r.top + scrollY), label: text.slice(0, 40), frames: frames.slice(0, 3), siblings: sib, siblingsWithText: sibText, sticky: getComputedStyle(e).position === 'fixed' || getComputedStyle(e).position === 'sticky' });
            k++;
          });
          return JSON.stringify(out);
        })()
        """;

    public sealed record Candidate(int K, string Tag, string Id, string Cls, int W, int H, int Top, string Label, string[] Frames, int Siblings, int SiblingsWithText, bool Sticky)
    {
        public string Describe() => $"<{Tag} id=\"{Id}\" class=\"{Cls}\"> {W}x{H}px at y={Top}{(Sticky ? " (sticky)" : "")}; label=\"{Label}\"; iframes={(Frames.Length == 0 ? "none" : string.Join(",", Frames))}; {Siblings} siblings of which {SiblingsWithText} have text";
    }

    public async Task<IReadOnlyList<Candidate>> ResidualCandidatesAsync(CoreWebView2 core)
    {
        try
        {
            var r = await core.ExecuteScriptAsync(CandidatesScript);
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(r) ?? "[]";
            return System.Text.Json.JsonSerializer.Deserialize<List<Candidate>>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (Exception) { return []; }
    }

    public async Task<int> CollapseCandidatesAsync(CoreWebView2 core, ResourceId id, IEnumerable<int> keys)
    {
        var list = string.Join(",", keys);
        if (list.Length == 0) return 0;
        try
        {
            var r = await core.ExecuteScriptAsync($$"""
                (() => { let n = 0; for (const k of [{{list}}]) { const e = document.querySelector('[data-jev-cand="' + k + '"]'); if (e) { e.dataset.jevCollapsed = 'semantic'; e.style.setProperty('display', 'none', 'important'); n++; } } return n; })()
                """);
            var n = int.TryParse(r, out var v) ? v : 0;
            if (Stats.TryGetValue(id, out var st)) st.SemanticCollapsed += n;
            return n;
        }
        catch (Exception) { return 0; }
    }

    public async Task<int> CollapseAsync(CoreWebView2 core, ResourceId id)
    {
        if (!Stats.TryGetValue(id, out var st)) return 0;
        var hosts = st.Recent.Select(x => x.Host).Distinct().Take(100).ToList();
        try
        {
            var r = await core.ExecuteScriptAsync(CollapseScript.Replace("__HOSTS__", System.Text.Json.JsonSerializer.Serialize(hosts)).Replace("__BLOCKED__", st.Blocked.ToString()));
            st.LastCollapseResult = r;
            return int.TryParse(r, out var n) ? n : 0;
        }
        catch (Exception ex) { st.LastCollapseResult = ex.GetType().Name + ": " + ex.Message; return 0; }
    }

    public ShieldAdapter(SiteSettingsRepository sites)
    {
        _sites = sites;
        _disabledSites = new HashSet<string>(sites.DisabledSites(), StringComparer.OrdinalIgnoreCase);
    }

    public ConcurrentDictionary<ResourceId, TabShieldStats> Stats { get; } = new();
    /// <summary>A site's anti-adblock wall was detected on this tab; the window tells the user and offers the per-site switch.</summary>
    public event Action<ResourceId>? WallDetected;
    /// <summary>Optional semantic clutter pass (JevBrain). Null = off. Never on the request path; runs after load.</summary>
    public Func<CoreWebView2, ResourceId, Task>? SemanticPass { get; set; }
    public int RuleCount => _engine.RuleCount;
    public void SetEngine(FilterEngine engine) => _engine = engine;

    public bool IsEnabledFor(string site) => !_disabledSites.Contains(site);
    public void SetEnabledFor(string site, bool enabled)
    {
        lock (_disabledSites) { if (enabled) _disabledSites.Remove(site); else _disabledSites.Add(site); }
        _sites.SetShieldEnabled(site, enabled);
    }

    // Post-load fallback: if a cross-host navigation raced the document-start registration, add the sheet now.
    private const string EnsureCosmeticScript = """
        ((css) => { if (document.getElementById('jev-shield-cosmetic')) return 'present';
          const s = document.createElement('style'); s.id = 'jev-shield-cosmetic'; s.textContent = css; (document.head || document.documentElement).appendChild(s); return 'added'; })(__CSS__)
        """;

    /// <summary>Awaited before the first navigation (see WebView2LeaseManager.OnCoreCreated).</summary>
    public async Task AttachAsync(CoreWebView2 core, ResourceId id, Uri initialUrl)
    {
        var stats = Stats.GetOrAdd(id, _ => new TabShieldStats());

        // Site modules: registered once, before any navigation, self-gated on host inside the script.
        foreach (var m in SiteScripts.All)
        {
            var gated = $$"""
                (() => { const h = location.host.toLowerCase(); if (!{{System.Text.Json.JsonSerializer.Serialize(m.HostSuffixes)}}.some(s => h === s || h.endsWith('.' + s))) return;
                {{m.Script}}
                })();
                """;
            try { await core.AddScriptToExecuteOnDocumentCreatedAsync(gated); } catch (Exception) { }
        }
        if (initialUrl.Scheme is "http" or "https")
        {
            foreach (var m in SiteScripts.For(initialUrl.Host)) stats.SiteModules.Add($"{m.Name} v{m.Version}");
            await RegisterCosmeticAsync(core, stats, initialUrl.Host);
        }
        Attach(core, id);
    }

    private string? _cosmeticId;
    private async Task RegisterCosmeticAsync(CoreWebView2 core, TabShieldStats stats, string host)
    {
        if (!IsEnabledFor(NetworkRequest.SiteOf(host))) return;
        var css = CssFor(host);
        if (css.Length == 0) return;
        try
        {
            if (_cosmeticId is { } old) core.RemoveScriptToExecuteOnDocumentCreated(old);
            _cosmeticId = await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript.Replace("__CSS__", System.Text.Json.JsonSerializer.Serialize(css)));
            stats.CosmeticSelectors = css.Count(c => c == '\n');
        }
        catch (Exception) { }
    }

    public void Attach(CoreWebView2 core, ResourceId id)
    {
        var stats = Stats.GetOrAdd(id, _ => new TabShieldStats());

        // Site modules report through fixed strings only (NATIVE_BRIDGE.md); we count, never parse.
        core.WebMessageReceived += (_, e) =>
        {
            string? msg = null;
            try { msg = e.TryGetWebMessageAsString(); } catch (Exception) { }
            switch (msg)
            {
                case "jev:yt-ad-pruned": stats.AdsPruned++; break;
                case "jev:yt-ad-skipped": stats.AdsSkipped++; break;
                case "jev:yt-wall": stats.WallSeen++; WallDetected?.Invoke(id); break;
            }
        };

        // Cross-host navigation: swap the cosmetic sheet for the new host (best effort; the post-load fallback covers the race).
        string lastHost = "";
        core.NavigationStarting += async (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host == lastHost) return;
            lastHost = u.Host;
            stats.SiteModules.Clear();
            foreach (var m in SiteScripts.For(u.Host)) stats.SiteModules.Add($"{m.Name} v{m.Version}");
            await RegisterCosmeticAsync(core, stats, u.Host);
        };
        core.NavigationCompleted += async (_, _) =>
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || !IsEnabledFor(NetworkRequest.SiteOf(u.Host))) return;
            var css = CssFor(u.Host);
            if (css.Length == 0) return;
            try { await core.ExecuteScriptAsync(EnsureCosmeticScript.Replace("__CSS__", System.Text.Json.JsonSerializer.Serialize(css))); } catch (Exception) { }
        };
        core.NavigationCompleted += async (_, _) =>
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || !IsEnabledFor(NetworkRequest.SiteOf(u.Host))) return;
            await Task.Delay(1500); // let late ad slots render before collapsing
            stats.Collapsed += await CollapseAsync(core, id);
            await Task.Delay(4000);
            stats.Collapsed += await CollapseAsync(core, id); // second pass for lazy-loaded slots
            if (SemanticPass is not null && stats.Blocked > 0) { try { await SemanticPass(core, id); } catch (Exception) { } }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            // V1 never blocks top-level navigations: a wrong rule must not make a site unreachable (§9 conservative).
            if (e.ResourceContext == CoreWebView2WebResourceContext.Document) return;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url)) return;
            Uri.TryCreate(core.Source, UriKind.Absolute, out var top);
            stats.Total++;
            var req = new NetworkRequest(url, top, Map(e.ResourceContext));
            if (req.IsThirdParty) { stats.ThirdParty++; stats.ThirdPartyHosts.AddOrUpdate(url.Host, 1, (_, n) => n + 1); }
            if (top is not null && !IsEnabledFor(NetworkRequest.SiteOf(top.Host))) return;

            var decision = _engine.Evaluate(req);
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
