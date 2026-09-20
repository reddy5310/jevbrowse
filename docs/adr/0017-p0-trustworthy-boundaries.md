# ADR 0017 — P0: boundaries that hold, and a browser that says what it knows

Status: accepted (2026-09-20)

The second independent review re-checked the agent work from ADR 0016 and found two of my "already fixed" claims were
wrong. It was right. This ADR records the five acceptance conditions it set and how each is met.

## What was actually still broken

| Claim I made | Reality at `f4ad087` |
|---|---|
| "Expiry cleanup is fixed" | The expiry check set `Closed = true` and returned. The sweeper filtered on `!s.Closed`, so it skipped exactly the sessions still holding renderers. Cleanup also used `Cause.Scheduler`, which a protected page vetoes, while still reporting success. |
| "The quota is a hard limit" | Hard for *sequential* requests only. The host dispatches handlers concurrently, and check→await→activate is not atomic, so two requests both passed. `ActionsUsed++` was a non-atomic read-modify-write with the same flaw. |
| (found while validating) | `NavigationGuard` was attached *after* `ActivateAndWaitAsync` returned, i.e. after the page had already loaded. The first navigation and every redirect inside it were unguarded. |

## The five conditions

**1. Navigation — an allowed start cannot redirect out of scope before protection attaches.**
Policy is registered against the *resource* (`IRendererLeaseManager.SetNavigationPolicy`) and applied by the lease
manager at lease creation, before the first `Navigate`. Registered again before a restore acquires a new renderer.
Proof: `--agent-check` drives a real redirect (`youtu.be/<id>` 301s to `www.youtube.com`) with a grant for `youtu.be`
only — the renderer is left at `about:blank`, in scope. The unit test asserts the property the old code could not
satisfy: the decision is made from the *pre-registered* policy alone.

**2. Revocation — Stop and expiry reject, cancel, and finish cleanup even for protected pages.**
`CloseAsync` revokes first (cancelling in-flight work through a per-session token, so the guard starts refusing), then
releases every page with `Cause.User`: a page-level protection flag must not let an agent's renderer outlive the
authority that created it, and these are agent-owned pages in a throwaway workspace. It is idempotent and sets
`CleanedUp` only when every renderer actually went. The expiry path now calls it instead of just marking the session.
The sweeper keys on `CleanedUp`, not `Closed`. `StopAsync` is exposed per session in the Agents panel and reports
"Stopped" only when `CleanedUp` is true.

**3. Concurrency — simultaneous requests cannot exceed limits or run after revocation.**
A per-session gate makes check→reserve→activate atomic; budgets are counted inside it. Terminal state is answered
before queueing so a revoked session gives its precise reason. Tests: 8 concurrent navigations under a 2-page limit,
20 concurrent requests against a 5-action budget, and a request in flight when Stop lands.

**4. Privacy — `Unknown` gets conservative rules and "Not assessed" means it.**
`DataClass.Unknown = 1` (a numeric insertion, with migration v8 remapping stored overrides). A page is PUBLIC only on
positive evidence: a structurally public host, or the page reporting no password/payment input and no sign-out
affordance with real content present. Everything else is Unknown: kept on this device (row, scroll, preview), but
**not indexed and never sent anywhere**. Agents are still allowed, because their domain grant is the control there.
Evidence is combined by the stricter result, so a sign-in signal cannot mask a banking URL. The badge says
"Not assessed" and the site dialog explains it in those words.

**5. Restoration — the renderer distinguishes the outcomes and the UI reflects them.**
`CaptureCheckpointAsync` returns `CaptureResult` (`Captured` / `Partial` / `TimedOut` / `Cancelled` / `Failed`) and no
longer throws or swallows timeouts. An automatic demotion proceeds only on `Captured`/`Partial`; not knowing whether
the page was preserved keeps the renderer. A policy decision not to persist is `Captured` with nothing kept — a
decision, not a failure. The kernel emits `restoring` and records the last capture. The UI shows the page title,
"Restoring…", and the saved preview labelled **"Previous view — a saved image, not the live page"**; after 12 s it
offers Try again / Open the address / Keep this tab active; and it says so when the exact place could not be restored.

## Also fixed here
`NavigateToString` leaves `Source` as `about:blank`, and the kernel was recording that as a navigation — overwriting
the tab's real address and discarding its checkpoint. Navigations to `about:` are now ignored.

## Consequences
- Browser Memory indexes less: only pages with positive public evidence. That is the honest trade and it is documented.
- `--agent-check` joins the real-engine gates. `--privacy-check` still passes with `Unknown` in place.
- 231 unit tests.
