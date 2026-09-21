# First private alpha: review response and release plan

Written 2026-09-21 in response to the independent code review of `a433ca0` (startup, browsing, renderer lifecycle, privacy, storage, agents, packaging). The
review was static; each finding below was first re-checked against the code, then fixed with a regression that fails without the fix, and the window-level
ones are also exercised on the real engine. **Not release-validated**: nothing here has run on a machine other than the development laptop.

## 1. The ten findings

| # | Finding | Confirmed in code | Fix | Evidence it fails without the fix |
|---|---|---|---|---|
| 1 | Address bar shows the wrong site after link, redirect, Back, Forward | Yes: only `activated` updated it | The tab in front updates its bar on every `navigated`; the person's own typing is never overwritten (judged from the text, not an event); Esc and leaving the box restore it; a background/agent navigation never touches it. `AddressInput` validates what is typed | `--nav-check` on the real engine: 7 steps (link, Back, Forward, redirect, typing kept, edit abandoned, agent in background). With the update disabled 3 of them fail (bar `/a`, engine `/b`) |
| 2 | Popups bypass managed-tab creation | Yes: no `NewWindowRequested` | Every request is handled (`e.Handled`); `PopupPolicy` allows only a user gesture from the page in front (not an agent page) to an http(s) address, opened as a managed tab in the **opener's workspace**; everything else is blocked and the person is told | `--nav-check`: with the handler removed the engine opens a real unmanaged `Popup` window (top-level window count 2→3/4) and the check fails; with it: script-opened popup blocked, clicked `target=_blank` becomes a managed tab, no extra window |
| 3 | A Public/Sensitive decision leaks to unrelated sites (`co.uk`, `github.io`) | Yes: key was the last two labels | Decisions keyed by the exact host (migration 10 adds `exact_host`). Old rows: a **stricter** old decision still protects; an old **Public** applies to nobody until the person decides again | `SiteDecisionTests` (5 fail with the old key). Trade-off: an old Public decision is dropped rather than risk loosening other sites |
| 4 | Marking a page Sensitive does not act immediately | Yes | `TabKernel.ReapplyPolicy()`; previews, saved-position pictures and indexed pages of that host are deleted at once; a surviving checkpoint no longer points at a deleted preview | `SiteDecisionTests.Marking_a_site_Sensitive…` |
| 5 | Startup and address-input failure paths | Yes | Nothing is clickable or accepts shortcuts until the kernel exists; init errors are observed and shown (dialog, `startup-error.txt`, Quit); the filter download no longer blocks start (5 s cap, finishes in the background); invalid addresses never throw | `recovery-check.ps1 -Scenario newer-db` found a **second bug in the fix** (the error dialog ran inside the constructor and crashed the launch); fixed and re-run |
| 6 | Agent shutdown/expiry threading | Yes | The host remembers its owner thread; the expiry timer hops back to it; `Stop()` no longer blocks; `StopEndpointAsync` is awaited on window close (6 s cap); background faults are reported, not swallowed. A finished agent session's throwaway profile is now deleted at once | `AgentHostThreadingTests` with a single-thread pump: 2 of 4 fail against the old code. `recovery-check.ps1 -Scenario agent-close`: window closes in ~1 s with a live agent, no processes left, profile deleted |
| 7 | Read and Click not rechecked after async waits | Yes | One `StillValidAsync` (session open, not expired, same lease and document, domain and class still allowed) after Read's engine wait, after Click's human confirmation, and before Type. Stopping the session ends the confirmation wait | `AgentAsyncWaitTests` (8): 6 fail with the check removed |
| 8 | Engine crash leaves a dead lease registered | Yes | `EngineFailed` on the lease; the kernel releases it, marks the tab Virtual (a crash outranks protection), and the window reloads the page in front on a fresh renderer (max twice a minute per tab). A failed browser process also invalidates its cached environment | `EngineFailureTests` (5) and `--nav-check`: browser process killed → new renderer, page reloaded, address still shown. (Before the environment fix the recovery hung) |
| 9 | Older build accepts a newer database | Yes | `UnsupportedDatabaseVersionException`, thrown **before** anything is written; never quarantined; the person is told to update and can Quit | Test: file byte-for-byte unchanged; `recovery-check.ps1 -Scenario newer-db` on the real app |
| 10 | Mouse close can reveal another workspace | Yes | `TabKernel.CloseAndSelectNextAsync`: both close paths select within the same workspace only | `CloseTabTests` (3) |

Additional defects found while fixing (not in the review): the damaged-database dialog was unreachable from the constructor (found by the real-app check); a
Sensitive re-class left the checkpoint pointing at a deleted preview; the agent's throwaway profile survived a normal window close.

## 2. Where everything stands (end to end)

| Area | Status | Notes |
|---|---|---|
| Startup, first run, restore | **Done** | Gated, observed, bounded. Not yet run offline on a clean machine |
| Address bar, navigation, popups | **Done (this pass)** | OAuth pop-ups that need `window.opener` open as ordinary tabs and will not report back |
| Tab lifecycle, restore, scheduler, crash recovery | **Done** | Real-engine crash recovery now covered |
| Privacy: data classes, Private sessions, Shield | **Done** | Private-session downloads go to the ordinary Downloads folder: **Open** |
| Storage: migrations, crash, upgrade, damage, newer DB | **Done** | Power loss not simulated |
| Agents (Navigate/Read/Click/Type/Screenshot) | **Done for an experimental alpha** | Screenshot stays experimental and off by default. If a further agent defect appears, ship with the agent endpoint off |
| Packaging | **Partial** | `scripts/release.ps1` (self-contained portable ZIP, SBOM, checksums) and now `scripts/packaged-smoke.ps1`. Unsigned, no installer, no auto-update |
| Accessibility & display | **Partial** | Keyboard, text size, transparency verified. Display scaling, high contrast, screen reader **Blocked/Open** |
| Downloads, uploads, meetings, long sessions | **Open** | Not exercised |
| Diagnostics for a stranger ("something went wrong") | **Open** | `startup-error.txt` and `crash.log` exist; there is no in-app way to export a report |

## 3. Steps to the first private alpha (in order)

1. **Freeze** one commit after this pass; require its CI (`test`, and the report-only jobs' results read) to be green. *(Done: `ae197d8`. `test` green; on the hosted Windows Server runner the Release build passed all 36 recovery checks and all 20 `--nav-check` steps including the killed-browser recovery, and the idle harness gave 15/15 valid runs with the busy control detected.)*
2. **Build the ZIP**: `. scripts\env.ps1; scripts\release.ps1 -Version 0.1.0-alpha.1` (runs the unit tests, publishes self-contained, writes `SHA256SUMS.txt` and an SBOM).
3. **Smoke the extracted ZIP here**: `scripts\packaged-smoke.ps1 -Zip <zip>` (checksum, first and second launch, tips, offline simulation, all recovery scenarios against the packaged exe).
4. **Clean Windows account or VM (needs you or a VM)**, no development tools installed:
   - extract, run `packaged-smoke.ps1 -DefaultData` (tests the real `%LOCALAPPDATA%\JevBrowse` location);
   - first launch online, then genuinely offline; a machine **without the WebView2 runtime** (what does the person see?);
   - browse a few real sites; click a `target=_blank` link; a download and an upload; sign in to one real site, restart, still signed in;
   - open a Private session, close the window with it live, relaunch (nothing left);
   - restart persistence with several tabs; kill the app from Task Manager, relaunch.
5. **Record** in the release notes: commit, ZIP SHA-256, prerequisites (Windows 10 19041+/11, x64, WebView2 runtime), data location and how to back it up
   (close the app, copy `%LOCALAPPDATA%\JevBrowse`; nothing outside it is written except downloads), and the known limitations below.
6. Only then hand it to the first testers, privately, with the limitations attached.

## 4. Known limitations to state in the alpha notes

Unsigned (SmartScreen will warn); portable only, no installer or updates; pop-ups from sign-in flows that need their opener do not report back; downloads use the
ordinary Downloads folder even from a Private session; Screenshot is experimental and its "never visible even briefly" claim is unproven; display scaling above
100%, high contrast and a screen reader are untested; one Windows machine and one WebView2 version were used; power loss can lose the last few saved changes.
