# ADR 0029: Screenshot is its own operation, in memory, from a page that is not shown

Status: accepted. Resolves the "Screenshot returns no image" gap recorded in ADR 0027.

## The problem

The agent Screenshot action went through the **checkpoint thumbnail**, which Trust OS refuses for private and disposable containers.
An agent lives in a Disposable workspace, so a grantable action never worked ("no image"), with the page shown or hidden. The only way
to "fix" it inside that design was to enable ordinary thumbnails for private pages, which is exactly what must not happen.

## Decision

- `IRendererLease.CaptureScreenshotAsync` is a **separate operation**: it never reads `AllowThumbnails`, never writes a file, returns PNG
  **bytes in memory**, and takes no decision about whether a picture may be taken (the gateway does).
- **No files, so nothing to clean up.** The response carries the picture as base64 (`AgentResponse.Screenshot`); `ScreenshotPath` stays
  null. Stop, expiry and a crash have nothing to leave behind. Startup removes anything older builds left in
  `agents/screenshots` (only a folder literally named `screenshots`).
- **Checks specific to a picture**, on top of the session, domain and data-class checks every action gets: the action is granted
  separately from Read; nothing with a password or payment field on it (`hard:secret_on_screen`); its own budget
  (`MaxScreenshots`, default 10, a ceiling can only lower it); a size cap (4 MB) and a check that the bytes are a PNG.
- **A second look after the capture.** The engine cannot be told to stop, so the gateway stops *waiting* when the session is stopped or
  expires (`cancelled` is audited) and whatever the engine returns later is dropped unread. If the session ended or the page turned
  into a login form or left its allowed domain while it was drawing, the picture is discarded.
- The audit records size and "held in memory only", never content. The Agent activity panel words the refusals.

## How a page that is not shown gets photographed (measured)

A collapsed WebView2 produces no frames, and a capture waits for a frame. On the real engine **every** engine-level way of asking a
collapsed control for a picture timed out: as is, lifecycle "active", focus emulation, an emulated viewport. So for the capture only,
the control is given a real size (1280x800) and placed **off-canvas** (at -30000,-30000, outside the window's client area, so it is
clipped), non-hit-testable and not a tab stop, for about 250 ms; then it is collapsed and put back exactly as the kernel wants it.
`IsVisible` reports what the kernel asked for, so staging can never read as "shown". A page the kernel is already showing is captured
directly.

## Evidence

- `AgentScreenshotTests` (14): in-memory, no folder created; works on a hidden page (never asked to be shown, `ShowCalls == 0`, hidden at
  capture time, the person's tab and page unchanged); ordinary thumbnails not enabled and no thumbnail stored; must be granted
  separately; a password page is never photographed; own budget and a ceiling that can only lower it; oversize and non-PNG refused;
  **stopping mid-capture** returns nothing and audits `cancelled`, and a late finish delivers or keeps nothing; a capture that finishes
  as the session ends is not delivered; a page that turns into a login form mid-capture has its picture discarded (mutation: removing
  that guard fails the test); **expiry** mid-capture; legacy leftovers removed only from a folder named `screenshots`; over the real
  local HTTP endpoint the picture arrives as base64 and no file exists. Suite: 383 tests.
- **Real engine** (`--agent-window-check`): 20 KB PNG of the agent's hidden page, 256 distinct byte values (not blank), showing
  "Example Domain" (looked at); the agent's page Collapsed before and after; the person's page shown; keyboard focus and the foreground
  window unchanged; no `agents/screenshots` folder.
- **Geometry while staged** (measured in-process at the moment of capture): the control sat at (-29719, -29871), 1280x800, against a
  1137x584 window: no overlap, not hit-testable, not a tab stop. **Positive control:** with staging moved on-canvas the same check
  reports overlap (X=281, Y=129) and fails.

## What is NOT proven

- **Pixel-level observation from outside the app is unproven.** `scripts/agent-screenshot-stage-check.ps1` watches the person's window
  while captures run and compares frames with a baseline. It reported PASS with zero changed pixels, but the frames were **solid
  black**: screen capture was returning black on this machine (even for a normal app window), so it compared black with black. That was
  a hole in my instrument: its positive control (page staged on canvas and held for 1.5 s) also "passed". The script now refuses to judge
  from a blank baseline (INCONCLUSIVE) and can save the frames it saw. Until it is run on a capturable screen and its positive control is
  seen to fail, "never visible for a moment" rests on the geometry above, not on pixels. The earlier PASS is void.
- The "session ends inside the capture" test passes through the cancellation path; the discard-if-closed branch is covered by behaviour,
  not isolated by a test.
- Staging briefly makes a real window handle visible (off-canvas), so the page runs unthrottled for that moment (timers, animation), and
  is laid out at 1280x800, which may differ from its previous size. A concurrent "Show its page" during a capture is handled in code
  (the kernel's wish is re-applied afterwards) and was exercised on the real engine at six click offsets (ADR 0030).
- Pictures are only as private as the agent that receives them: an agent may send one anywhere. The controls are the grant, the
  budget, the page checks and the audit, not what happens after the bytes leave.

Update (ADR 0030): Screenshot is now experimental and opt-in per session; three findings against this design were fixed (expiry after capture, document identity, password/payment fields in frames).
