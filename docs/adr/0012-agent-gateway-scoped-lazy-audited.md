# ADR 0012 — Agent Gateway: scoped, lazy, audited, human-in-the-loop

Status: accepted (Phase 10 gate, 2026-09-20)

Decision:
- Agents interact only through `IAgentGateway` with a session opened from an `AgentManifest` (§12.1). The check order is fixed: session/expiry/action budget → action grant → domain scope → data-class ceiling → renderer. Denials are audited like successes.
- The agent surface on a renderer is three narrow calls: `GetPageMapAsync`, `ClickAsync(selector)`, `TypeAsync(selector, text)`. There is no script evaluation and no raw DOM. Page Maps carry field names/types/labels, never values.
- The data-class ceiling is judged on **content** (URL heuristics + page signals), not on the container: the agent's Disposable silo does not make a banking page less sensitive. (`TabKernel.ContentClassOf`; found by `Data_class_ceiling_blocks_reads_on_sensitive_pages`.)
- Two hard rules sit above the manifest: a page with a password/payment field is never exposed, and typing into selectors that name secrets is refused before the page is reached (the page script refuses `type=password` / `cc-*` / `one-time-code` independently).
- Destructive-looking clicks require the human's "Allow once" in JevBrowse's own dialog (manifest may set `deny`; `allow` is meant for throwaway containers).
- Lazy renderers: the agent's pages are virtualized LRU-first to stay under `maxLivePages`, using the same kernel path as the scheduler, so protection vetoes still apply.
- A workspace created for an agent uses the manifest's container, Disposable by default, so no user cookies are reachable unless the user deliberately points the manifest at an existing workspace.
- The loopback HTTP host is opt-in, 127.0.0.1 only, bearer-token per run, and maps 1:1 onto the gateway. It is the contract an MCP adapter can wrap later.

Verified by `AgentGatewayTests`: workspace/container, domain scope + audit, action grants, live-page quota (5 pages, ≤2 live), page map without values, sensitive-class ceiling, password page hard deny even with an empty deny list, secret selector refusal, destructive confirmation (asked exactly when destructive), expiry and action budget, close virtualizes, and an end-to-end HTTP test (401 without token, 200/403 on allowed/denied navigation, audit, delete).

Deferred: CPU/network accounting per agent, MCP framing, per-agent rate limits, screenshot redaction of secret regions.
