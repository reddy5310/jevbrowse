# ADR 0019: Ending a Private session is terminal

Date: 2026-09-20. Status: implemented and reviewed. The kernel and cleanup work was authored by Codex and reviewed and extended here; the shell rules were corrected against the four agreed cases (see Review).

## Behaviour

1. Entering Private preserves the normal workspace and uses a separate ephemeral identity.
2. Switching to another product mode returns to the normal workspace. Returning to Private reuses the open session, including its cookies.
3. **End private session** asks before closing every tab in that session, including virtual tabs, protected calls, downloads and dirty forms. Cancel keeps the session open. Normal tabs remain available.
4. Entering Private after ending it creates a new identity with no previous-session cookies.

Closing the window also attempts to end Private sessions. Closing the last tab alone does not end its session.

## Two completion conditions

The kernel serializes session termination with renderer acquisition and other lifecycle operations. It marks the session ended before releasing anything, closes without capture or scheduler veto, removes tab bookkeeping, and rejects later activation, navigation callbacks, permission answers and context restoration. A failed renderer release keeps the tab for another cleanup attempt; the identity stays ended.

Renderer closure is separate from profile deletion. The WebView2 adapter waits for `BrowserProcessExited`, which confirms the engine released its profile resources, then deletes that session's directory. Bounded waits return incomplete status when the process or files remain busy. The UI offers a retry and never calls pending data deleted. Startup sweeps abandoned directories. A per-profile exclusive lock prevents this version's other instances from sweeping active sessions. Deletion validates that its target is a direct child of the ephemeral profile root and rejects directory links.

The normal close path clears permission grants and marks their scope ended. An answer to an already pending prompt cannot grant access or recreate a remembered grant. Full workspace IDs key permissions; their first eight characters are no longer used as identity.

## Evidence and limits

- All 245 unit tests passed. Three HTTP listener tests required a run outside the sandbox. Six new tests cover switching, terminal cleanup, acquisition interleaving, renderer-release failure, locked files and startup retry.
- Release shell builds with zero warnings/errors.
- `--private-session-check`: 16/16 checks passed on one Windows machine, in 4,719 ms for the entire probe (not a cleanup latency measurement). Raw result: [private-session-check.json](../performance/private-session-check.json).
- The probe drives real WebViews and cookies, product-mode handlers and session-end methods. Media host messages and a delayed permission answer are injected. It does **not** open devices, click the confirmation dialog, force a crash or validate normal window shutdown. Startup sweep is unit-tested with abandoned files, not a killed browser process.
- Existing `--privacy-check` evidence still covers durable browser rows, previews and indexing. No claim is made about OS artifacts, explicit downloads or other data outside the temporary profile.

Reproduce by building the Release x64 shell, setting `DOTNET_ROOT` for the installed SDK/runtime, and setting `JEVBROWSE_DATA_DIR` to a **fresh test directory**, `JEVBROWSE_NO_FILTER_UPDATE=1`, `JEVBROWSE_AI=0`, `JEVBROWSE_MODE=Simple`, `JEVBROWSE_DEVSPACE=0`. Launch `JevBrowse.App.exe --private-session-check`. Inspect `benchmarks/private-session-check.json`; process exit alone does not indicate a pass.

## Review: the four agreed cases

The first implementation invented its own four behaviours because the agreed ones were not in its handoff. They are, and
two of them were not met. Corrected:

1. **Return goes to the workspace the user actually came from, by name.** The destination is recorded whenever the user is in
   an ordinary workspace, so entering by *any* route (mode box or workspace box) returns to the right place. The control reads
   "Return to Work", not a fixed "Return to Personal". Return changes what is visible and closes nothing; switching workspaces
   reactivates the tab that was last active there.
2. **The user's session is identified explicitly.** It is the workspace the shell created for the user, held by identity. The
   first version decided "is this the user's session?" from the container type alone. Agents can be granted a Private
   workspace (`AgentCeiling.GrantableContainers`), and activating an agent tab makes it the active workspace, so an agent's
   session would have shown the user's End button, ended when clicked, and flipped the user's browser into Private mode.
3. **End works while the session is in the background.** The control is visible whenever there is a session to end. The first
   version hid it unless the Private workspace was active, so switching away made a live session, with its cookies, impossible
   to end without returning to it. The button also no longer moves the user: ending from another workspace leaves them where they are.
4. **Completion reflects cleanup, retryably.** Kept from the original: the status says "deleted" only when tabs are closed
   *and* profile data is gone, otherwise it offers a retry; renderer closure and profile deletion are separate conditions.

The rules that decide (1) to (3) are a pure function, `PrivateSessionPresentation`, with unit tests for each case, because the
window itself cannot be unit tested and the ownership defect was exactly the kind of thing a UI-only fix would let regress.

## Still not tested

The confirmation dialog's buttons, normal window shutdown with a live session, and recovery after a forced crash are not
exercised by any automated check. The cleanup logic beneath them is.

## Cost card

Feature: explicit Private session termination and cleanup reporting.
Binary delta / idle RAM delta: not measured.
Background CPU: no new recurring service; bounded asynchronous retries during cleanup.
Network calls / cloud dependency: none added; probe pages are local.
Disk: one small ownership lock per active profile; pending profile data remains until deletion succeeds. No new disk quota.
Disableable: use a normal identity instead of starting a Private session.
Security surface: session tombstones, permission invalidation, renderer release and scoped filesystem deletion.
Benchmark: PASS for the 16-check functional probe; no comparative performance claim.
