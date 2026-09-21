# Idle cost: regress2A

Verdict: **all runs valid**. Microsoft Windows 11 Pro 10.0.26200, AMD Ryzen 7 6800H with Radeon Graphics, 16 logical cores; GPU counters available. Build: debug, commit d8f9664ff962b25998c8952e46fdb1f0ab982e48, 4 uncommitted files.

Plan: 5 runs per scenario, warm-up 30 s, sampling 60 s every 2 s. CPU = percent of ONE core, medians over valid runs (min to max in brackets). GPU = summed engine utilisation, reported separately.

| Scenario | valid / attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |
|---|---|---|---|---|---|
| static | 5 / 5 | 7.111 (5.913 to 8.706) | 1.302 (0.936 to 1.358) | 0.005 (0.002 to 0.006) | 0 (0 to 0) |

Comparison with the baseline result set (shell CPU):
- **static**: no regression detected. median difference 6.02 points against a threshold of 8.38 (larger of 1 and 3 x the widest run-to-run spread)

_GPU is reported separately from CPU on purpose. Low CPU does not mean no GPU activity, and neither means zero idle cost: memory, wake-ups and battery are separate costs this does not measure._
