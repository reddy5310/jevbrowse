# ADR 0015 — Site modules: browser-level scriptlets, YouTube as the first

Status: accepted (2026-09-20)

## Why
Video ads on YouTube are stitched into the player response, so the network layer cannot see them; and since Manifest V3 removed dynamic scriptlets from Chrome extensions, only a *browser* can still intervene before the page's own scripts run. That is a structural advantage of owning the renderer, and we should use it deliberately and honestly.

## Decision
`SiteScripts` is a catalogue of small, versioned first-party scripts bound to host suffixes, injected at document start (main world) after cosmetic CSS, and removed by the per-site Shield switch. Constraints (tested): no network calls, no host objects, and only the fixed bridge strings in `NATIVE_BRIDGE.md`; the host counts them and never parses them.

### YouTube module, four layers
1. **Network**: unchanged (`pagead`, doubleclick, tracking beacons blocked by lists).
2. **Data**: `adPlacements`, `adSlots`, `playerAds`, `adBreakHeartbeatParams`, `daiConfig` are deleted from every object that passes through `JSON.parse`, `Response.prototype.json`, and the inline `ytInitialPlayerResponse` setter, before the player reads them.
3. **Player**: every 500 ms, if `.html5-video-player.ad-showing`, mute, seek to the ad's end and click any skip button.
4. **Wall**: if YouTube's enforcement renderer appears, the tab reports it once; the status bar tells the user plainly and the Shield panel offers the per-site switch. No AI on this path; hard rules only.

### Measurement
`JevBrowse.App --youtube-check` plays videos for 75 s each and records: seconds in ad state, seconds playing, furthest playback time, definitions pruned, player skips, wall seen. Numbers go to `docs/performance/BASELINES.md`. Marketing may only say what that JSON says.

## Consequences
- This is an arms race by nature; the module is versioned and updatable independently of the engine.
- A false prune could break playback; the module touches only known ad keys and never the video streams.
- Detection risk: if YouTube walls the browser, the user is told and can disable the module per site in one click. We prefer transparency over evasion tricks (Constitution rule 10).
