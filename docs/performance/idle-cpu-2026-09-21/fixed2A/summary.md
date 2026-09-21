# Idle cost: fixed2A

Verdict: **all runs valid**. Microsoft Windows 11 Pro 10.0.26200, AMD Ryzen 7 6800H with Radeon Graphics, 16 logical cores; GPU counters available. Build: debug, commit d8f9664ff962b25998c8952e46fdb1f0ab982e48, 4 uncommitted files.

Plan: 5 runs per scenario, warm-up 30 s, sampling 60 s every 2 s. CPU = percent of ONE core, medians over valid runs (min to max in brackets). GPU = summed engine utilisation, reported separately.

| Scenario | valid / attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |
|---|---|---|---|---|---|
| static | 5 / 5 | 1.096 (0.959 to 1.152) | 1.02 (0.65 to 1.15) | 0.005 (0.005 to 0.014) | 0 (0 to 0) |
| busy-control | 5 / 5 | 1.016 (0.962 to 1.068) | 36.366 (34.66 to 38.838) | 0.002 (0.002 to 0.004) | 5.189 (5.156 to 5.195) |

Positive control: DETECTED (busy page total CPU median 37.357 vs static 2.117)

_GPU is reported separately from CPU on purpose. Low CPU does not mean no GPU activity, and neither means zero idle cost: memory, wake-ups and battery are separate costs this does not measure._
