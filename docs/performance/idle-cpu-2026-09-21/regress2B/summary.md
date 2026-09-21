# Idle cost: regress2B

Verdict: **all runs valid**. Microsoft Windows 11 Pro 10.0.26200, AMD Ryzen 7 6800H with Radeon Graphics, 16 logical cores; GPU counters available. Build: debug, commit d8f9664ff962b25998c8952e46fdb1f0ab982e48, 4 uncommitted files.

Plan: 3 runs per scenario, warm-up 30 s, sampling 60 s every 2 s. CPU = percent of ONE core, medians over valid runs (min to max in brackets). GPU = summed engine utilisation, reported separately.

| Scenario | valid / attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |
|---|---|---|---|---|---|
| panel-open | 3 / 3 | 8.354 (7.817 to 10.246) | 0.934 (0.835 to 1.12) | 0.005 (0.005 to 0.008) | 0 (0 to 0) |
| panel-closed | 3 / 3 | 9.988 (8.281 to 10.254) | 1.325 (0.833 to 1.515) | 0.009 (0.007 to 0.011) | 0 (0 to 0) |

_GPU is reported separately from CPU on purpose. Low CPU does not mean no GPU activity, and neither means zero idle cost: memory, wake-ups and battery are separate costs this does not measure._
