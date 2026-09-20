# ADR 0030: Screenshot is experimental; three capture findings fixed; agent indicator; hidden-page cost; an idle regression found on the way

Status: accepted. Follows ADR 0029. **The "never visible, not even for a moment" claim for hidden screenshots remains UNPROVEN** (see below).

## 1. Screenshot is experimental, opt-in per session, default off

A session gets Screenshot only if the person answers a separate dialog for that session ("Allow screenshots for this session? (experimental)";
Esc, Enter and the close button all mean No). An agent naming Screenshot in its request enables nothing, even inside the approved ceiling; with no
way to ask, it is dropped. `AgentSession.ScreenshotsApproved` is set only by the host after that answer (`IAgentGateway.ApproveScreenshots`), and the
gateway refuses every request without it (`screenshots_not_approved`), so the gate holds even for callers that skip the host. The ceiling checkbox
is labelled experimental. Tests: default off, an agent's request over the endpoint gets 403, a "no" drops it and a "yes" grants that session only,
the person is not asked when the session did not ask.

## 2. Three findings against `157a82b`, each with a regression that failed first

1. **A picture could be delivered after the session expired.** The post-capture check tested `Closed` and cancellation but not the clock, and the
   existing expiry test called the sweeper, which masked it. Now checked against `ExpiresAt` (and the session is closed). Regression: capture begins
   before expiry, the injected clock passes it without a sweep, the capture completes: no image, no successful audit entry.
2. **The document was not compared, only URL, domain and class.** A reload, or navigation to another allowed page in the same class, changed nothing
   those checks could see. `IRendererLease.DocumentGeneration` now counts commits (navigations, reloads of the same address, source changes) and the
   gateway also requires the same lease. Regressions: navigate to another permitted page during capture; reload the same URL during capture. Both fail
   against the old code and fail again when the comparison is reverted.
3. **Password and payment fields inside frames never reached the guard.** `OnFrameMessage` forwarded frame messages only to media tracking. A
   `PageSignalTracker` now keeps each frame's contribution (only the two secret flags, never "signed in"), the lease publishes the sum, frames leaving
   or replacing their document take back only their own, and a new top-level page resets everything.
   **Real-engine regression** (`--agent-frame-secret-check`, a local server under two origins, 127.0.0.1 and localhost): before the fix a picture was
   delivered for **all 7** cases (same-origin frame, cross-origin frame, payment field in a cross-origin frame, nested two deep, and a field appearing
   during the capture x3); after, all 7 are refused. The control (a plain page) and a harmless iframe appearing during capture are both delivered, so
   a refusal is not a broken capture and ordinary frame activity does not discard pictures. (My first version of this check was itself invalid: local
   servers are classed Authenticated and a "/payment" path trips a URL rule, so every case, the control included, was refused for the wrong reason. The
   control is what exposed it.) Seven unit tests cover the tracker.

## 3. "Show its page" pressed during a capture (real engine)

Six click offsets across the staging and capture window: the page ends active, shown, at (0,0), at the host's size, default alignment and clickable;
the person's page hidden; the capture still delivered and a later one works. Always collapsing the control after a capture fails exactly the offsets
that fall inside the staging window.

## 4. The external pixel observer is STILL unproven

`scripts/agent-screenshot-stage-check.ps1` refuses to judge blank frames. On this machine screen capture returned black again (even for a normal app
window), so it reported INCONCLUSIVE, and its deliberately visible positive control has **not** been run to a valid result. Until it is run on a
capturable desktop and its positive control is seen to FAIL, "never visible even for a moment" rests on geometry (the staged control sits far outside
the window, is not hit-testable and not a tab stop; the same measurement fails for an on-canvas mutant), not on pixels. The earlier PASS stays void.

## 5. Agent-running indicator with a direct Stop

Event-driven (`AgentGateway.SessionsChanged` on open, stop, expiry: no polling timer). While a session runs the toolbar shows "● <agent> working"
(opens Agent activity) and a **Stop** that ends every running agent in one press with no dialog, releases its pages and hides the indicator. It sits
in the wrapping bar, so it is reachable at any width and with the sidebar shown or hidden. Real-app check (invoked through automation peers, as a
keyboard or screen reader would): absent with no agent; present, named and opening the panel; Stop releases the pages and removes it. Found on the
way: `WrapPanel` never re-measured a child that started Collapsed (its desired size stayed 0x0 when it became visible); fixed at the root by watching
each child's visibility, not by a workaround at the call site.

## 6. An idle-CPU regression I introduced, found by measuring, and fixed

While measuring hidden pages the **baseline (no agent at all)** read ~7% of a core instead of ~1.4%. Confirmed real by building the P2-end commit in a
worktree and measuring it under the same conditions (1.4%), bisected across 18 commits to `d776ab2` (my restore-panel change), and traced to the cause:
the branch of `UpdateIdlePanel` that used to collapse `RestoreProgress` when no tab was active was now skipped, and nothing else ever collapsed it
after a restore. A visible indeterminate progress bar animates for as long as it is Visible, even inside a collapsed panel. Both places that end a
restore now collapse the bar. Regression: `--idle-invariants-check` (no restore in flight must mean no visible progress bar) failed before, passes
after; a source guard is mutation-checked. Idle CPU after the fix: 1.45%, 1.39%, 1.35%. The P2 idle-cost claim held at P2 and was silently broken
for about seven commits. **Nothing in CI catches this**: the idle-cost script is manual. That remains a gap.

## 7. What hidden agent pages cost (measured)

`scripts/idle-cost.ps1 -AgentLoad`, debug build, one machine, window in front, 40 s samples after 30 s settle, agent pages served locally and read back
as Collapsed. CPU is percent of one core; memory is summed over the app and its child processes.

| Scenario | CPU avg | CPU peak | GPU | Working set | Processes |
|---|---|---|---|---|---|
| Baseline (no agent) | 1.33% | 3.4% | 0.02% | 522 MB | 7 |
| Hidden, 1 static page | 2.77% | 11.9% | 0% | 857 MB | 13 |
| Hidden, 4 static pages | 3.25% | 9.8% | 0.01% | 1041 MB | 16 |
| Hidden, 1 busy page | 2.99% | 8.1% | 0.01% | 854 MB | 13 |
| Hidden, 4 busy pages | 5.15% | 11.7% | 0.02% | 1064 MB | 16 |
| **Control: 1 busy page SHOWN** | **38.99%** | 64.9% | **5.11%** | 870 MB | 13 |
| Baseline again (drift check) | 1.46% | 3.7% | 0% | 543 MB | 8 |

"Busy" draws 300 shapes to a canvas every frame, runs a CSS animation and a hot timer. Reading:
- **Hiding works as a throttle.** The same busy page costs ~13x less CPU hidden (3.0%) than shown (39%), and no measurable GPU. The shown control
  proves the instrument can see a busy page.
- **The first agent page is the expensive one: about +335 MB and six more processes**, because a Disposable session gets its own browser process
  set. Each additional page costs about +60 MB. Four hidden busy pages cost about +3.8 points of CPU over baseline.
- These pages count against the renderer pool like any other, so the admission rules of ADR 0028 apply to them.

Limits: debug build; one machine that was also running Chrome, Zoom and an editor; a static and a synthetic busy page, not real sites; video, audio and
WebRTC in a hidden page are unmeasured; the cost of the staging itself (about a quarter of a second per capture) is not measured.

## Not done

- Pixel-level observation on a capturable desktop and its positive control (needs the screen to actually produce frames).
- Display scaling (needs the owner to change it) and a real high-contrast theme (needs separate approval).
- Idle-cost is not a CI gate.
