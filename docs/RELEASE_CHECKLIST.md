# Release checklist

One list, one status per line, each pointing at its evidence. Updated 2026-09-21. **Nothing here is release-validated**: no build has been signed or tested on
a second machine. Statuses: **Done** (evidence exists and its limits are named), **Partial**, **Open**, **Blocked** (needs something only the owner can do),
**Later** (deliberately out of the alpha). The evidence matrix ([EVIDENCE_MATRIX.md](EVIDENCE_MATRIX.md)) has the strength of each piece of evidence.

Scope of the alpha: durable workspaces with predictable, memory-efficient tab restoration, Shield, Private sessions, the permission and data-class model,
and opt-in agent access. Extensions, sync, a built-in password manager and local AI are **Later** and do not gate it.

## 1. Recovery and data integrity

| Item | Status | Evidence / what is missing |
|---|---|---|
| Migrations are atomic and finish after a crash | Done | Real process kill inside steps 8 and 9 (`CrashRecoveryTests`). Power loss not simulated |
| Interrupted writes leave no partial commits | Done | 20 real kills; every acknowledged commit present. Process kill only |
| Upgrade from every earlier schema | Done | Fixtures written by the real historic builds; released steps hash-pinned (`UpgradeFromHistoricDatabasesTests`). Tiny fixtures, not a real user's data |
| Damaged database cannot block start; never deleted | Done | `DamagedDatabaseTests` + `recovery-check.ps1 -Scenario corrupt-db`. Very large partly-damaged files get a lighter check |
| Crash with a live Private session; next start sweeps | Done | `recovery-check.ps1` crash (real app, TerminateProcess). Intermittent engine-process lingering seen, cause unknown |
| Normal shutdown with a live Private session | Done | `recovery-check.ps1` clean-shutdown |
| Cleanup retries | Done | Unit, real file locks |
| Non-database files (settings, ui-prefs, filter lists) survive truncation | Partial | Prefs and settings are read defensively; writes are not atomic (a kill mid-write can lose the preference, not data). Filter activation is atomic |
| Power loss / failing disk | Open | Not simulated. WAL with `synchronous=NORMAL` can lose the last commits in a power cut; decide whether to move to FULL |
| Backup / export of the user's own data | Open | No export exists |

## 2. Installation and updates

| Item | Status | Evidence / what is missing |
|---|---|---|
| Code-signing certificate | **Blocked** | Owner decision and purchase; unsigned builds trigger SmartScreen |
| Signed installable package (MSIX or installer) | Open | Portable zip only. Packaging choice interacts with WebView2 user-data paths |
| Secure update path | Open | None. Needs signed manifests, downgrade protection, a rollback |
| Clean-machine install / upgrade / uninstall | Open | Never run on a machine other than the dev machine. Needs a VM or second PC; uninstall must keep or offer to remove user data |
| WebView2 runtime handling | Open | Evergreen runtime assumed present; behaviour when it is missing or too old is untested |

## 2b. The private alpha itself

See [FIRST_RELEASE_PLAN.md](FIRST_RELEASE_PLAN.md) for the ten review findings (all fixed with regressions) and the ordered steps.

| Item | Status | Evidence / what is missing |
|---|---|---|
| Review findings 1-10 | Done | Each has a regression that fails without its fix; 6 also on the real app |
| Freeze a commit, CI green | Open (do last) | |
| Release ZIP + checksum + SBOM | Ready to run | `scripts/release.ps1`; smoke: `scripts/packaged-smoke.ps1` |
| Packaged smoke on the extracted ZIP | Open (run at the freeze) | |
| Clean Windows account / VM run, offline, missing runtime | **Blocked** | Needs you or a VM |
| Downloads/uploads, real sign-in persistence | Open | Private-session downloads go to the ordinary Downloads folder |
| Release notes: commit, checksum, prerequisites, data location, backup, limitations | Open (template in the plan) | |

## 3. Everyday browsing

| Item | Status | Evidence / what is missing |
|---|---|---|
| 20-site sweep, cap holds, clean close | Done (thin) | `--site-sweep`, one machine |
| Ads/trackers on real sites; YouTube ads | Done (thin) | `--shield-check`, `--youtube-check` |
| Camera/mic/WebRTC keeps the tab awake | Done | `--media-check` |
| Downloads and uploads | Open | Not exercised: where files go, Private-session downloads, cancel/resume, file pickers |
| Sign-in persistence across restarts | Open | Personal profile persists in WebView2 user data, not tested end to end against a real login |
| Meetings (Meet/Teams/Zoom web) | Open | Only synthetic WebRTC loopback |
| Long sessions and low memory | Open | Scheduler simulated; no real low-RAM or multi-day run |

## 4. Accessibility and display

| Item | Status | Evidence / what is missing |
|---|---|---|
| Keyboard reachability, names, focus return | Done | `ui-a11y-check.ps1` (real keys, UI Automation) |
| Text size 125–225% | Done | `text-size-check.ps1` |
| Reduced transparency | Done | `transparency-check.ps1` |
| Reduced motion | Partial | `reduced-motion-check.ps1` was run against the real Windows setting earlier, but its result was not preserved in the repository and the matrix still says "not exercised". Re-run and record it (needs the setting toggled and restored, so it needs your go-ahead) |
| Display scaling 125/150/200% | **Blocked** | The owner switches scaling and tells Claude the value each time |
| Real high-contrast themes | **Blocked** | Needs separate approval to change the Windows setting, restored afterwards |
| Screen-reader walkthrough | Open | Not done. UI Automation coverage is not a screen reader |

## 5. Performance across machines

| Item | Status | Evidence / what is missing |
|---|---|---|
| Idle CPU harness, positive control, reproduced regression | Done | `docs/performance/IDLE_CPU.md`. Debug build, one machine |
| Idle CPU in CI | Done (report-only) | `idle-cpu` job; hosted runner runs valid |
| Enforced threshold | **Blocked** | Needs a stable dedicated runner |
| Release-build and whole-browser memory | Open | Everything measured so far is a Debug build; memory numbers are WebView2 group only |
| Hidden agent pages with timers, audio, video, WebRTC | Open | Only synthetic canvas/timer page measured |
| A second, different machine | Open | Everything is one Ryzen laptop |

## 6. Screenshot (experimental)

| Item | Status | Evidence / what is missing |
|---|---|---|
| Opt-in per session, default off; three review findings fixed | Done | `AgentScreenshotTests` (22) |
| "Never visible even for a moment" | **Blocked / unproven** | The external pixel observer returned black frames on this desktop. Needs a capturable, unlocked desktop where its deliberately visible positive control is seen to fail the check |

## Not gating the alpha (Later)

Extensions, sync, a built-in password manager, local AI, bookmark import/export.

## Standing rules

Never call something done without its limits; keep the repository private until the owner decides; no Windows setting changes without asking and restoring; commit only verified work.
