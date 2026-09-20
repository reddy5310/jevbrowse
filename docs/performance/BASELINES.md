# Performance baselines

Machine: Windows 11 Pro 10.0.26200, 16 GB class, WebView2 runtime 153.0.4234.48, Debug build, warm cache.
All values are **measured** process-group figures (private bytes of every WebView2 process); nothing is estimated.
Reports are written to `<data>/benchmarks/*.json` and are not committed; these tables are the committed summary.

## Memory reclamation (`JevBrowse.App --memory-lab`, 2026-09-20)

| Step | Processes | Private MB |
|---|---|---|
| 0 live | 0 | 0 |
| 1 live | 5 | 145 |
| 5 live | 9 | 765 |
| 4 suspended, 1 live | 9 | 621 (−19%) |
| 4 virtual, 1 live | 5 | 319 (−58%) |
| all virtual | 0 | 0 |

Marginal cost per live page ≈ 150–240 MB; fixed browser/GPU/utility overhead ≈ 140 MB.

## Virtual → live restore (`JevBrowse.App --restore-bench`, 8 pages, 2026-09-20)

| Metric | Value |
|---|---|
| restore p50 | 350 ms at Phase 2 → **≈520 ms today** (see the drift note at the end of this file) |
| restore p95 | 1,236 ms at Phase 2 → **≈1,270 ms today** |
| checkpoints written | 8/8 |
| thumbnails written | 8/8 (~115 KB each) |
| scroll position restored | 7/7 scrollable pages |

## Scheduler (simulation, `ThrashSimulationTests`, deterministic seed 42)

2 h session, 30 tabs, tab switch every 20 s, 10-minute memory sawtooth 40% → 6% available:
- live pool over budget+1 on < 15% of ticks
- < 400 virtualizations total, ≤ 10 re-virtualizations within 2 minutes, ≤ 30 per tab

## Shield (`JevBrowse.App --shield-check`, live EasyList + EasyPrivacy, 2026-09-20)

| Metric | Value |
|---|---|
| rules compiled | 110,839 (28,621 unsupported lines skipped: cosmetic, regex, unknown options) |
| compile time | 226 ms (Debug) |
| lookup p50 / p95 | 62.5 µs / 85.9 µs (Debug, 400-URL mix) |
| cnn.com | 26 blocked / 153 requests |
| forbes.com | 18 / 79 |
| theverge.com | 11 / 171 |
| en.wikipedia.org | 0 / 27 (no false positives) |

Known optimisation headroom: token enumeration is O(L²) per URL run; a suffix/prefix trie would cut p95 further if needed.

### Visible-clutter test (ADR 0014, 2026-09-20; network + cosmetic + collapse, AI off)

| Page | blocked / requests | third-party | cosmetic selectors | collapsed | visible result |
|---|---|---|---|---|---|
| youtube.com/watch | 7–13 / 59–205 | 135 | 13,640 | — | video plays; no ad in captured frame; **video ads not claimed** |
| cricbuzz.com | 10 / 70 | 11 | 13,629 | 0 | 3 ad slots collapsed by lists; one empty carousel card remains (semantic layer) |
| goodreturns.in | 53 / 163 | 56 | 13,634 | 6 | clean: blank band + "ADVERTISEMENT" box removed |
| goodreturns.in/gold-rates | 59 / 140 | 110 | 13,634 | 11 | clean |

Compile: 110,839 network + ~19 k cosmetic rules in ~260 ms (Debug).

## Gates for future PRs

A PR that moves restore p95 or the 5-live private figure by more than 10% needs an ADR (Architecture §22).

### YouTube (ADR 0015, `--youtube-check`, 75 s per video, logged out, 2026-09-20)

| Video | before module | after module |
|---|---|---|
| Despacito (kJQP7kiw5Fk) | 37 s in ad state, video reached 39 s | **0 s in ad state**, 19 ad definitions pruned, video reached 71 s |
| Gangnam Style (9bZkp7q19f0) | 0 s (no ads served) | 0 s, 0 pruned |
| Never Gonna Give You Up | 0 s (no ads served) | 0 s, 0 pruned |

Wall detection: one false positive from a hidden renderer, fixed by requiring visibility. No visible enforcement notice in any run. Ads are served non-deterministically; the claim we make is only what this table shows.

### Privacy gate (real engine, `--privacy-check`, 2026-09-20)

Real WebView2 renderers driven through a public control, a sensitive-URL tab (real bank login page → SECRET) and a Private-container session (two tabs, virtualize, workspace switch, permission block), then disk and database inspected.

| Check | Result |
|---|---|
| Public control produced a thumbnail (so "none" elsewhere is meaningful) | yes |
| Sensitive/secret page thumbnails / checkpoint rows | 0 / 0 |
| Private thumbnails, tab rows, checkpoint rows, workspace rows, timeline entries, permission rows, index documents | 0, 0, 0, 0, 0, 0, 0 |
| Ephemeral profile directories after restart | 0 (swept) |

**PASS.** Before the fixes in ADR 0016 the first three private counts were non-zero by construction (thumbnails captured on deactivation; timeline recorded every workspace).

### Restore latency drift (measured 2026-09-20, attribution run)

The Phase 2 figure (p50 350 ms) predates Shield, cosmetic filtering, site modules and permission handling, all of which now run **before a page's first navigation**. Same benchmark, same machine, 8 pages, 2-3 runs each:

| Build | p50 | p95 |
|---|---|---|
| Phase 2 (original baseline) | 350 ms | 1,236 ms |
| Pre-review commit `bcefadc` | 526–552 ms | 1,250–1,269 ms |
| After review fixes | 512–530 ms | 1,270–1,380 ms |

The independent-review changes did **not** move restore latency: the pre-review and post-review builds agree within run-to-run spread. The ≈170 ms drift since Phase 2 comes from pre-navigation work; hypothesis, unverified: registering the ~1 MB per-host cosmetic stylesheet script and the site modules on every renderer acquisition. Tracked in ROADMAP as a performance item; the 10 % PR gate in this file applies from these numbers onward.

### Agent scope gate (real engine, `--agent-check`, ADR 0017, 2026-09-20)

A grant for `youtu.be` only; `https://youtu.be/<id>` 301-redirects to `www.youtube.com`, which is out of scope.

| Check | Result |
|---|---|
| Navigate accepted for the granted host | yes |
| Renderer landed on | `about:blank` (the out-of-scope redirect was cancelled) |
| Out-of-scope redirect blocked on the FIRST load | yes |
| Stop released the session's pages | yes (0 live after) |
| Request after Stop | `session_closed` |

**PASS.** Before ADR 0017 the policy was attached after the load completed, so this redirect would have landed on
`www.youtube.com` with the agent still inside its session.
