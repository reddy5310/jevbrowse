using System.Collections.Concurrent;
using JevBrowse.Domain;
using JevBrowse.Shield;
using JevBrowse.Storage;
using Microsoft.Web.WebView2.Core;

namespace JevBrowse.App.Shield;

/// <summary>
/// Everything Shield knows about ONE renderer. Page counters describe the current document and reset on every
/// top-level navigation (<see cref="NavId"/> identifies it); SessionBlocked is cumulative. Script registrations are
/// owned here, per renderer, so one tab can never remove or overwrite another tab's scripts.
/// </summary>
public sealed class TabShieldStats
{
    public int Total, Blocked, ThirdParty, CosmeticSelectors, Collapsed, SemanticCollapsed;
    public int SessionBlocked;
    public int AdsPruned, AdsSkipped, WallSeen;
    public string? LastCollapseResult, LastSemantic;
    /// <summary>Incremented on every top-level navigation; delayed work compares it to know it is still on the same document.</summary>
    public int NavId;
    /// <summary>The user pressed "show what Shield hid" for this document: no further hiding until the next navigation.</summary>
    public int RestoredNavId = -1;
    public CoreWebView2? Core;
    public string? CosmeticScriptId, SiteModulesScriptId;

    public void ResetPage()
    {
        Total = Blocked = ThirdParty = CosmeticSelectors = Collapsed = SemanticCollapsed = 0;
        LastCollapseResult = LastSemantic = null;
        while (Recent.TryDequeue(out _)) { }
        ThirdPartyHosts.Clear();
    }
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
    private int _sessionBlockedTotal;
    /// <summary>Blocked requests since the app started, across all tabs including ones since virtualized.</summary>
    public int SessionBlockedTotal => Volatile.Read(ref _sessionBlockedTotal);
    /// <summary>A site's anti-adblock wall was detected on this tab; the window tells the user and offers the per-site switch.</summary>
    public event Action<ResourceId>? WallDetected;
    /// <summary>Optional semantic clutter pass (JevBrain). Null = off. Never on the request path; runs after load.</summary>
    public Func<CoreWebView2, ResourceId, Task>? SemanticPass { get; set; }
    public int RuleCount => _engine.RuleCount;
    public void SetEngine(FilterEngine engine) => _engine = engine;

    public bool IsEnabledFor(string site) { lock (_disabledSites) return !_disabledSites.Contains(site); }

    /// <summary>
    /// Per-site switch. Disabling removes EVERY layer for that site in every open renderer (cosmetic sheet, site
    /// modules) rather than only stopping new ones; the caller reloads the page so the document is clean.
    /// </summary>
    public async Task SetEnabledForAsync(string site, bool enabled)
    {
        lock (_disabledSites) { if (enabled) _disabledSites.Remove(site); else _disabledSites.Add(site); }
        _sites.SetShieldEnabled(site, enabled);
        foreach (var stats in Stats.Values.ToList())
        {
            if (stats.Core is not { } core) continue;
            try
            {
                await RegisterSiteModulesAsync(core, stats);
                if (Uri.TryCreate(core.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") await RegisterCosmeticAsync(core, stats, u.Host);
            }
            catch (Exception) { /* renderer went away */ }
        }
    }

    private List<string> DisabledSnapshot() { lock (_disabledSites) return [.. _disabledSites]; }

    /// <summary>
    /// One document-start script holding every site module, each gated on its host suffixes AND on the current
    /// disabled-sites list (baked in at registration and re-registered whenever that list changes), so a disabled
    /// site's modules never run even though they are registered before any navigation.
    /// </summary>
    private async Task RegisterSiteModulesAsync(CoreWebView2 core, TabShieldStats stats)
    {
        if (stats.SiteModulesScriptId is { } old) { try { core.RemoveScriptToExecuteOnDocumentCreated(old); } catch (Exception) { } stats.SiteModulesScriptId = null; }
        var off = System.Text.Json.JsonSerializer.Serialize(DisabledSnapshot());
        var sb = new System.Text.StringBuilder();
        sb.Append("(() => { const OFF = ").Append(off).Append("; const host = location.host.toLowerCase();\n")
          .Append("  const site = h => { const l = h.split('.'); return l.length <= 2 ? h : l.slice(-2).join('.'); };\n")
          .Append("  if (OFF.some(s => host === s || host.endsWith('.' + s) || site(host) === s)) return;\n");
        foreach (var m in SiteScripts.All)
            sb.Append("  (() => { const h = host; if (!").Append(System.Text.Json.JsonSerializer.Serialize(m.HostSuffixes)).Append(".some(s => h === s || h.endsWith('.' + s))) return;\n")
              .Append(m.Script).Append("\n  })();\n");
        sb.Append("})();");
        stats.SiteModulesScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(sb.ToString());
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
        stats.Core = core;

        // Site modules: registered once per renderer, before any navigation, gated inside the script on host + disabled list.
        try { await RegisterSiteModulesAsync(core, stats); } catch (Exception) { }
        if (initialUrl.Scheme is "http" or "https")
        {
            if (IsEnabledFor(NetworkRequest.SiteOf(initialUrl.Host))) foreach (var m in SiteScripts.For(initialUrl.Host)) stats.SiteModules.Add($"{m.Name} v{m.Version}");
            await RegisterCosmeticAsync(core, stats, initialUrl.Host);
        }
        Attach(core, id);
    }

    /// <summary>
    /// Registers (or, for a disabled site, REMOVES) this renderer's cosmetic stylesheet script. The previous
    /// registration is always removed first; it is per-renderer state, not adapter state.
    /// </summary>
    private async Task RegisterCosmeticAsync(CoreWebView2 core, TabShieldStats stats, string host)
    {
        try
        {
            if (stats.CosmeticScriptId is { } old) { core.RemoveScriptToExecuteOnDocumentCreated(old); stats.CosmeticScriptId = null; }
            stats.CosmeticSelectors = 0;
            if (!IsEnabledFor(NetworkRequest.SiteOf(host))) return;
            var css = CssFor(host);
            if (css.Length == 0) return;
            stats.CosmeticScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(CosmeticScript.Replace("__CSS__", System.Text.Json.JsonSerializer.Serialize(css)));
            stats.CosmeticSelectors = css.Count(c => c == '\n');
        }
        catch (Exception) { }
    }

    /// <summary>
    /// "Show what Shield hid": undo for the current document. Restores every element the collapse layers hid (they are
    /// tagged), removes the cosmetic sheet, and stops further hiding until the next navigation.
    /// </summary>
    public async Task<int> RestoreHiddenAsync(CoreWebView2 core, ResourceId id)
    {
        if (Stats.TryGetValue(id, out var st)) st.RestoredNavId = st.NavId;
        try
        {
            var r = await core.ExecuteScriptAsync("""
                (() => { let n = 0;
                  document.querySelectorAll('[data-jev-collapsed]').forEach(e => { e.style.removeProperty('display'); delete e.dataset.jevCollapsed; n++; });
                  document.querySelectorAll('[data-jev-cand]').forEach(e => { delete e.dataset.jevCand; });
                  const s = document.getElementById('jev-shield-cosmetic'); if (s) { s.remove(); n++; }
                  return n; })()
                """);
            return int.TryParse(r, out var v) ? v : 0;
        }
        catch (Exception) { return 0; }
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

        // Every top-level navigation starts a new document: new NavId, fresh page counters and block evidence, so old
        // hosts and delayed work from the previous page can never be applied to this one.
        string lastHost = "";
        core.NavigationStarting += async (_, e) =>
        {
            stats.NavId++;
            stats.ResetPage();
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host == lastHost) return;
            lastHost = u.Host;
            stats.SiteModules.Clear();
            if (IsEnabledFor(NetworkRequest.SiteOf(u.Host))) foreach (var m in SiteScripts.For(u.Host)) stats.SiteModules.Add($"{m.Name} v{m.Version}");
            await RegisterCosmeticAsync(core, stats, u.Host);   // cross-host: swap the sheet (best effort; the post-load fallback covers the race)
        };
        core.NavigationCompleted += async (_, _) =>
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var u) || !IsEnabledFor(NetworkRequest.SiteOf(u.Host))) return;
            var nav = stats.NavId;
            bool Current() => stats.NavId == nav && stats.RestoredNavId != nav && IsEnabledFor(NetworkRequest.SiteOf(u.Host))
                              && Uri.TryCreate(core.Source, UriKind.Absolute, out var now) && now.Host == u.Host;   // still THIS document, still enabled, not undone

            var css = CssFor(u.Host);
            if (css.Length > 0 && Current()) { try { await core.ExecuteScriptAsync(EnsureCosmeticScript.Replace("__CSS__", System.Text.Json.JsonSerializer.Serialize(css))); } catch (Exception) { } }

            await Task.Delay(1500); // let late ad slots render before collapsing
            if (!Current()) return;
            stats.Collapsed += await CollapseAsync(core, id);
            await Task.Delay(4000);
            if (!Current()) return;
            stats.Collapsed += await CollapseAsync(core, id); // second pass for lazy-loaded slots
            if (SemanticPass is not null && stats.Blocked > 0 && Current()) { try { await SemanticPass(core, id); } catch (Exception) { } }
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
            stats.SessionBlocked++;
            Interlocked.Increment(ref _sessionBlockedTotal);
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
