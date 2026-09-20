# ADR 0027: An agent does not move the person's window

Status: accepted. Resolves the item tracked in ADR 0025.

## The problem, measured

Every agent `Navigate` called `SwitchWorkspaceAsync` and then `ActivateAsync`: the person's whole window jumped to the agent's
workspace and page. On the real engine (`--agent-window-check`, old gateway): the person's tab was no longer active
(`personsTabStillActive: false`), the workspace had changed, their page was hidden and the agent's shown. No test noticed, because the
existing tests only asserted what the agent could reach, never what happened to the person.

## Decision

An agent's pages run live in the agent's own workspace, **never shown unless the person chooses to**.

- `TabKernel.OpenIn(workspace, url)`: opens a tab for someone else without switching to it (`Open` stays for the person).
- `TabKernel.EnsureLiveInBackgroundAsync(id)`: gives the page a live renderer, wired to the same events as any other, hidden, in the
  Warm state; leaves the active tab, the active workspace and the screen alone. If the tab is the one being shown, it does nothing.
  The lease-creation code was extracted from `ActivateCoreAsync` (`EnsureLeaseAsync`) so both paths share it.
- The gateway uses those two and no longer switches or activates.
- Watching is the person's choice: **Agent activity > Show its page** activates the agent's page and moves the window there (the agent
  keeps working in its own workspace). Live-page limits, scope guards and revocation are unchanged.

## Evidence

- `AgentDoesNotMoveTheWindowTests` (7): after two navigations the person's tab, workspace and on-screen page are unchanged; the agent's
  page is live, in its workspace, not shown; Read works on a page that is not shown; only an explicit activate moves the window;
  `OpenIn` never switches; the live-page limit still holds. Against the **old** gateway 3 of them fail (the negative control), against
  the new one all pass. Suite: 357 tests.
- Real engine, real endpoint (`--agent-window-check`): person's tab still active, workspace unchanged, their page Visible; agent page
  live and Collapsed; navigate OK; Read returned "Example Domain" with 127 characters of text.
- `--agent-check` (redirect containment and revocation) still passes; the UI gate passes with the new button present.

## Found, not fixed

- ~~The Screenshot action returns no image in a Disposable session~~ **Resolved in ADR 0029** (its own in-memory operation; ordinary thumbnails were not enabled).
- A hidden agent page is invisible to the person except through the Agent activity panel. That is the intent, but it means the panel is
  now the only place to see that an agent is at work; there is no badge on the toolbar yet.
- Hidden WebView2 views may be throttled by the engine. Navigation and Read were fine on a plain page; heavier pages (timers, media,
  layout-dependent scripts) are unmeasured.
