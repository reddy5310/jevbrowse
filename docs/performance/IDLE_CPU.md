# Idle CPU measurement

What the browser costs when nothing is happening, measured the same way every time, with the shell and the WebView2 processes kept apart and GPU
reported separately. Result sets: [idle-cpu-2026-09-21/](idle-cpu-2026-09-21/) (every sample, every process, environment, campaign log).

**Not claimed:** that low CPU means no GPU activity, or that the app has "zero idle cost". Memory, timer wake-ups and battery are separate costs this
does not measure (memory is in `idle-cost.ps1`; wake-ups and battery are unmeasured).

## How it measures

`scripts/idle-benchmark.ps1`, one fresh app per run, a throwaway data directory, no network filter update, no AI:

1. Start the app in the scenario. The app writes down what state it is REALLY in (`benchmarks/idle-scenario-state.json`, or `hidden-load-state.json`);
   the harness reads the address bar back as well.
2. **Warm-up** (default 30 s), then **sampling for a fixed period** (default 60 s, every 2 s on a fixed cadence).
3. Per sample and per process: CPU seconds used, grouped as **shell** (`JevBrowse.App.exe`), **webview** (`msedgewebview2` tree) and other. CPU is percent of
   ONE core (16 logical cores here, so 100% is one sixteenth of the machine).
4. A background collector reads the Windows `GPU Engine` counters and whole-machine CPU during the same window. GPU is summed per group and reported in its
   own columns. If the counter returns nothing, GPU is `null` ("unavailable"), never `0`.
5. Only the process tree the harness started is stopped.

Scenarios: `static` (welcome page), `panel-open` (Receipt panel showing), `panel-closed` (panel opened then closed), `agent-idle` (an agent session with one
hidden static page, no requests), `busy-control` (the **positive control**: a page with a per-frame canvas, a CSS animation and a hot timer).

### A run is INCONCLUSIVE, not a number, when

the app exits; no window; window minimised; a live process cannot be read; the app reports a different scenario than asked (or none); the wrong page is
showing; fewer than 90% of samples or the wrong duration; the process tree is still settling; per-sample CPU disagrees with first-to-last CPU; whole-machine
CPU could not be read or was above 80%. Inconclusive runs are counted, listed with their reasons, and left out of every statistic. The suite is also
inconclusive if the busy control is not at least 10 points above the static page. The rules are pure functions (`IdleBenchmark.Lib.ps1`), tested with
made-up numbers by `idle-benchmark.selftest.ps1` (28 checks; each rule was mutated once to confirm the self-test fails when the rule is broken).

### The threshold comes from measured variation

Comparison uses the shell's CPU (the part this project's code controls). A regression needs **at least 3 valid runs on both sides**, a median difference
above `max(1.0 point, 3 x the BASELINE's own run-to-run spread)`, and every candidate run above every baseline run. It is report-only unless `-Enforce` is
given. See "What went wrong along the way" for why the baseline's spread, not the candidate's.

## Results (this machine, debug build, 2026-09-21)

Ryzen 7 6800H (16 logical cores), 23 GB, AMD Radeon + NVIDIA RTX 3050 Ti (counters cover both), Windows 11 Pro 10.0.26200, WebView2 runtime 153.0.4234.48,
Performance power plan, interactive session, machine otherwise in normal use (Chrome, VS Code, Claude open; whole-machine CPU was under the 80% guard).
Warm-up 30 s, sampling 60 s. Medians, with min to max in brackets; percent of one core.

| Build | Scenario | valid/attempted | Shell CPU % | WebView2 CPU % | Shell GPU % | WebView2 GPU % |
|---|---|---|---|---|---|---|
| fixed | static | 5/5 | 1.10 (0.96-1.15) | 1.02 (0.65-1.15) | 0.005 | 0 |
| fixed | **busy-control (positive)** | 5/5 | 1.02 (0.96-1.07) | **36.4 (34.7-38.8)** | 0.002 | **5.19 (5.16-5.20)** |
| fixed, second session | static | 3/3 | 1.13 (0.99-1.16) | 0.94 (0.86-1.17) | 0.005 | 0 |
| fixed | panel-open | 3/3 | 1.10 (1.08-1.16) | 0.99 (0.96-1.25) | 0.005 | 0 |
| fixed | panel-closed | 3/3 | 1.02 (1.00-1.07) | 1.02 (0.99-1.09) | 0.010 | 0 |
| fixed | agent-idle (1 hidden page) | 3/3 | 1.85 (1.81-1.98) | 2.55 (2.37-2.60) | 0.009 | 0 |
| **regressed** | static | 5/5 | **7.11 (5.91-8.71)** | 1.30 (0.94-1.36) | 0.005 | 0 |
| **regressed** | panel-open | 3/3 | **8.35 (7.82-10.25)** | 0.93 | 0.005 | 0 |
| **regressed** | panel-closed | 3/3 | **9.99 (8.28-10.25)** | 1.33 | 0.009 | 0 |

- **The positive control is seen**: the busy page reads about 36% CPU and about 5.2% GPU in the WebView2 group, against about 1% and 0% for the static page.
  The shell does not change (1.0%), which is the point of keeping the groups apart.
- **The known regression is reproduced and separated.** "Regressed" is the current code with the two lines of the fix removed (a finished restore leaving the
  indeterminate `RestoreProgress` bar Visible; the fix is `01c5714`), built in a separate worktree so the fixed build is untouched. The shell reads 6 to 10x
  higher; the WebView2 group and both GPU columns do **not** move, so it is the app's own UI animating, not the page. In every scenario the harness says
  REGRESSION with the runs fully separated (`scripts/idle-compare.ps1`):

  ```
  static        REGRESSION. median difference 6.02 points against a threshold of 1 (baseline spread 0.193)
  panel-open    REGRESSION. median difference 7.26 points against a threshold of 1 (baseline spread 0.078)
  panel-closed  REGRESSION. median difference 8.97 points against a threshold of 1 (baseline spread 0.075)
  fixed vs fixed, two separate sessions:  NO REGRESSION DETECTED. difference 0.03 points
  ```
- **The GPU columns do not show this regression at all** (0.005% either way). A spinning bar costs about 6 points of CPU and is invisible in the GPU
  counter here. Neither number alone would be "the idle cost".
- **A hidden agent page is not free**: agent-idle costs about +0.75 points of shell CPU and +1.5 of WebView2 CPU over static (one extra WebView2 process
  tree), and this scenario also holds memory that `idle-cost.ps1 -AgentLoad` reports. GPU was still reading about 0.
- Run-to-run variation of the fixed build: shell spread 0.08 to 0.25 points, WebView2 up to 0.5, on this machine, this session.

## What went wrong along the way (kept, because it is the evidence the harness can be trusted)

1. **Silent GPU zeros.** A first campaign (`fixedA/B`, `regressA/B`, not published as results) read GPU as 0 for the busy page too, because counter objects
   returned from a background job are flattened to strings. The busy control's 0 GPU is what exposed it (an earlier tool had measured 5.1%). Fixed to
   return plain records, and "no GPU rows at all" is now `null`, not 0. The same defect meant whole-machine CPU was never read, so the machine-busy guard did
   not apply; a missing reading is now itself inconclusive (self-test case added, mutation-checked). The whole campaign was re-run; the published set is the re-run.
2. **The first threshold hid the regression it was built to find.** With `3 x the widest spread of either side`, the regressed build's own noise (5.9-8.7%)
   raised the bar to 8.4 points and the 6-point regression was reported "no regression detected". The threshold now uses the baseline's spread only. This
   change was made after seeing the data, so it is not an independent validation: the same rule also flags the separately collected first campaign (5.35 points
   against a 1.0 threshold), and does not flag fixed-vs-fixed (0.03), but 5 runs of one regression on one machine is what supports it.
3. A parameter typed `[string[]]` with `-File` and a comma list; the scenario switch written wrongly (null state path); `@(f)` around a function returning
   `,@()` making an empty list count as one problem (every run inconclusive). All caught by smoke runs before any result was kept.

## Limits

- One machine, one session, a **debug** build, an interactive desktop shared with other programs. Numbers are for comparing builds on the same machine, not
  for quoting as the browser's idle cost. A release build will read lower.
- Environment recording for the regressed build shows the main repository's commit (the worktree's exe path is in `results.json` under the label `regress2*`).
- The GPU counter is per process and per engine; browser GPU work is attributed to the WebView2 GPU process. It may not see work done on the app's behalf in other
  processes (for example the display compositor; not checked), so "GPU 0" means "no measured GPU-process load", not "no GPU activity".
- Only shell CPU is compared automatically; WebView2 CPU and GPU are reported and inspected by a person.
- Nothing here gates a merge. Enforcing a regression threshold needs a **stable dedicated runner** and a threshold chosen from that runner's own runs
  (`-Enforce`; the rule above is a starting point, not a decision). GitHub-hosted runners may not have a usable desktop; the job is `continue-on-error`,
  publishes a step summary and the raw JSON as an artifact, and an inconclusive result there is the correct outcome, not a failure.

## Running it

```
. scripts\env.ps1
scripts\idle-benchmark.ps1 -Scenarios "static,busy-control" -Runs 5 -Label mine -ReportOnly
scripts\idle-benchmark.ps1 -Scenarios static -Runs 5 -CompareTo <baseline>\results.json
scripts\idle-compare.ps1 -Candidate <a>\results.json -Baseline <b>\results.json -Scenario static
scripts\idle-benchmark.selftest.ps1        # the rules, with made-up numbers (runs in CI, blocking: it is deterministic)
```

Leave the machine alone while it runs: it takes about 2 minutes per run, and using the machine adds noise (a busy machine makes runs inconclusive).
