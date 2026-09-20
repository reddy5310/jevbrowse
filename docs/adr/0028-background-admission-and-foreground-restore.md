# ADR 0028: Background admission never costs the person a page, and never touches their restore

Status: accepted. Follows ADR 0027 (agent pages run hidden).

## 1. Admission

Agent pages share the renderer pool with the person. `MakeRoomAsync` treated the active tab as an ordinary victim candidate, so a full pool
let a background agent page evict the page being read. It now takes a `background` flag:

- The **active tab is never a candidate** for background admission; protected tabs never were.
- A victim is only released after its place was saved. If the save fails, background work is **refused**, not forced.
- Background work **never exceeds the pool**. With no legal victim it returns false; the kernel raises `background-refused`; the
  gateway answers `renderer_pool_full` ("the browser had no spare capacity, and it will not close the page you are reading to make
  room"), closes the never-loaded tab it had just opened, and records the refusal. The same request succeeds once capacity returns.
- The **person's own** action keeps the older rule (it may exceed the budget when everything live is protected): their work is never
  dropped to honour a budget. A test pins both directions.
- Background work may still release pages the person is *not* looking at (their sleeping-eligible background tabs); each is saved
  first and restorable. That is a policy choice, recorded here, not an accident.

## 2. Foreground restore message, timer and controls

Kernel events carry `Background`. Events about an agent's page coming live (`restoring`, `restored`, `loaded`, `warmed`,
`background-refused`) are marked, and:

- the restore panel, its 12-second timer and its Retry / Open-address buttons ignore them (`ShowRestoring` and `FinishRestore` only
  run for the person's own restores). Before, an agent's restore replaced the tracked restore id, so the person's real restore finished
  unnoticed and the panel could stay up;
- `KernelStatus` produces no message for them (no "Woke a sleeping tab in 300 ms" about a page nobody is looking at).

**A separate bug found by the real-engine check:** with no active tab (start-up, or after the last tab was closed) the panel was
overwritten with "Nothing open here / Choose a tab on the left" the moment the restore began, so the person saw a false empty state
while their page was coming back. An in-flight restore now keeps the panel, and a restore whose tab was closed is cleared.

## Evidence

- `BackgroundAdmissionTests` (10): pool full with only the active page live is refused and the page stays; background may release an
  unwatched page but never the watched one; a protected page is not released; a failed save refuses instead of forcing; the person's
  own action can still exceed the pool; background events are marked and the person's are not; background events make no status text;
  a slow foreground restore finishes intact when background work arrives during it, and the acquires happen in that order; a slow
  background restore does not take over or evict when the person then switches (the person's action may release the agent's older
  page, which is correct); six simultaneous requests never exceed the pool or touch the active page. Mutation check: disabling the
  active-page protection fails 3 of them.
- Gateway: refusal with `renderer_pool_full`, no orphan tab, audit entry; the same request succeeds when capacity returns.
- Real engine, real endpoint (`--agent-window-check`): the person's page put to sleep and woken while the agent navigates: the panel
  tracked only the person's page (samples never showed the agent's), the restore completed, the panel ended collapsed and tracking
  nothing. Mutation check: removing the UI guard makes the check fail (the panel tracked the agent's page). Suite: 369 tests.

## Known limits

- The kernel does one thing at a time. A slow background acquisition can delay a foreground click by that acquisition time (creating
  the renderer, typically well under a second); the agent's page-load wait happens outside the kernel's turn. Yielding the turn
  mid-acquire would risk two renderers for one tab, so it was not done. The test asserts order and integrity, not foreground latency.
- There is still no toolbar indicator that an agent is working.
