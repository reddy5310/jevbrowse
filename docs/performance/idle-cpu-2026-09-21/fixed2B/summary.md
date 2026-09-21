# Idle cost: fixed2B

Verdict: **all runs valid**. Microsoft Windows 11 Pro 10.0.26200, AMD Ryzen 7 6800H with Radeon Graphics, 16 logical cores; GPU counters available. Build: debug, commit d8f9664ff962b25998c8952e46fdb1f0ab982e48, 4 uncommitted files.

Plan: 3 runs per scenario, warm-up 30 s, sampling 60 s every 2 s. CPU = percent of ONE core, medians over valid runs (min to max in brackets). GPU = summed engine utilisation, reported separately.

| Scenario | valid / attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |
|---|---|---|---|---|---|
| static | 3 / 3 | 1.126 (0.989 to 1.158) | 0.939 (0.862 to 1.174) | 0.005 (0.005 to 0.005) | 0 (0 to 0) |
| panel-open | 3 / 3 | 1.099 (1.078 to 1.156) | 0.987 (0.962 to 1.253) | 0.005 (0.002 to 0.005) | 0 (0 to 0) |
| panel-closed | 3 / 3 | 1.017 (0.995 to 1.07) | 1.018 (0.986 to 1.094) | 0.01 (0.007 to 0.011) | 0 (0 to 0) |
| agent-idle | 3 / 3 | 1.849 (1.813 to 1.976) | 2.552 (2.371 to 2.6) | 0.009 (0.008 to 0.01) | 0 (0 to 0) |

_GPU is reported separately from CPU on purpose. Low CPU does not mean no GPU activity, and neither means zero idle cost: memory, wake-ups and battery are separate costs this does not measure._
