namespace JevBrowse.Shield;

/// <summary>
/// First-party site modules (ADR 0015): small, versioned scripts injected at document start for specific hosts.
/// They are the browser-level equivalent of uBlock scriptlets, which Manifest V3 extensions can no longer run.
/// Rules: deterministic, no network, no host objects; they may only post the fixed strings listed in
/// docs/threat-model/NATIVE_BRIDGE.md; per-site Shield disable removes them.
/// </summary>
public sealed record SiteScript(string Name, int Version, string[] HostSuffixes, string Script)
{
    public bool AppliesTo(string host) => HostSuffixes.Any(s => host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
}

public static class SiteScripts
{
    public static IReadOnlyList<SiteScript> All => [YouTube];

    public static IEnumerable<SiteScript> For(string host) => All.Where(s => s.AppliesTo(host));

    /// <summary>
    /// YouTube ad defence, three layers: (1) prune ad definitions from player JSON before the player sees them,
    /// (2) if an ad still plays, mute + seek + skip, (3) report the anti-adblock wall so the user is told the truth.
    /// Counters go out as fixed bridge strings; nothing else leaves the page.
    /// </summary>
    public static readonly SiteScript YouTube = new("youtube", 1, ["youtube.com", "youtube-nocookie.com", "m.youtube.com"], """
        (() => {
          if (window.__jevYt) return; window.__jevYt = true;
          const post = m => { try { chrome.webview.postMessage(m); } catch {} };
          const AD_KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams', 'adBreakParams'];
          const prune = (o, depth) => {
            let n = 0;
            try {
              if (!o || typeof o !== 'object' || depth > 3) return 0;
              for (const k of AD_KEYS) if (k in o) { delete o[k]; n++; }
              if (o.playerConfig && o.playerConfig.daiConfig) { delete o.playerConfig.daiConfig; n++; }
              if (o.playerResponse) n += prune(o.playerResponse, depth + 1);
              if (Array.isArray(o.onResponseReceivedEndpoints)) n += prune(o.onResponseReceivedEndpoints, depth + 1);
            } catch {}
            return n;
          };
          const report = n => { if (n > 0) post('jev:yt-ad-pruned'); };

          // Layer 2a: everything parsed from text (ytInitialPlayerResponse, innertube XHR parsed via JSON.parse)
          const _parse = JSON.parse;
          JSON.parse = function (t, r) { const o = _parse.call(this, t, r); report(prune(o, 0)); return o; };
          // Layer 2b: fetch() responses read as JSON
          const _json = Response.prototype.json;
          Response.prototype.json = function () { return _json.call(this).then(o => { report(prune(o, 0)); return o; }); };
          // Layer 2c: the inline global assigned by the initial HTML
          let _ipr;
          try { Object.defineProperty(window, 'ytInitialPlayerResponse', { configurable: true, get: () => _ipr, set: v => { report(prune(v, 0)); _ipr = v; } }); } catch {}

          // Layer 3: player-level skipper, and Layer 4: wall detection. 500 ms tick, cheap selectors only.
          let wallReported = false;
          setInterval(() => {
            try {
              const p = document.querySelector('.html5-video-player');
              const v = document.querySelector('video.html5-main-video');
              if (p && v && p.classList.contains('ad-showing')) {
                v.muted = true;
                if (isFinite(v.duration) && v.duration > 0.5) v.currentTime = v.duration;
                const s = document.querySelector('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, button.ytp-ad-skip-button-container');
                if (s) s.click();
                post('jev:yt-ad-skipped');
              }
              if (!wallReported) {
                // Only a *visible* enforcement element counts; YouTube keeps hidden renderers in the DOM.
                const wall = [...document.querySelectorAll('ytd-enforcement-message-view-model, yt-playability-error-supported-renderers, #error-screen .ytp-error')]
                  .find(e => { const r = e.getBoundingClientRect(); return r.width > 100 && r.height > 60 && getComputedStyle(e).visibility !== 'hidden'; });
                if (wall) { wallReported = true; post('jev:yt-wall'); }
              }
            } catch {}
          }, 500);
        })();
        """);
}
