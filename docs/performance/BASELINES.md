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
| restore p50 | 350 ms |
| restore p95 | 1,236 ms |
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

## Gates for future PRs

A PR that moves restore p95 or the 5-live private figure by more than 10% needs an ADR (Architecture §22).
