# ADR 0011 — DevSpace: an optional module that never guesses

Status: accepted (Phase 9 gate, 2026-09-20)

Decision:
- DevSpace is off by default and attaches nothing to renderers while off, so general users carry no idle cost (§13). It is enabled with `JEVBROWSE_DEVSPACE=1` or the toggle in the Dev panel; listeners attach to renderers created after that.
- **Environment Spaces** come from an explicit, user-editable `devspace/projects.json` (host glob + optional port → LOCAL/DEV/STAGING/PROD). The only heuristic is loopback/`.local` → LOCAL. Everything else resolves to **Unknown**, never to a guessed PROD, and the resolution reason is shown (§13.1 "detect by explicit configuration first").
- **PROD chrome**: a 4 px red frame over the page and a badge, hit-test transparent. Staging is orange, Dev blue, Local green.
- **Production Safety Guard** confirms only URL paths the project explicitly lists under `ConfirmInProd`, and only when the environment is explicitly PROD. It is a guardrail, not authorization (§13.1); the UI does not promise universal interception.
- **Error intelligence**: console errors/exceptions are grouped by a normalized signature (numbers, URLs, hex ids, quoted values stripped) and errors that follow within 500 ms are attached to the first error as a probable cascade, so the root is what gets explained. Explanation is a separate, explicit AI action through JevBrain (`ExplainError`), subject to every Trust OS rule.
- **Network intelligence**: per-tab requests bucketed into api / static / third-party / failed / slow (≥1.5 s) / duplicate GETs.
- **Localhost dashboard**: TCP-connect probe of the project's configured services with latency.
- Data is bounded per tab (500 console entries, 1000 network entries) and lives only in memory.

Verified by `JevBrowse.DevSpace.Tests` (explicit-rule precedence, never-guess-PROD, port specificity, prod confirmation scope, store roundtrip and bad-JSON tolerance, error grouping and cascade, network buckets, live port probe).

Deferred: page health trends (DOM/memory over time), performance doctor timeline, per-workspace project binding UI, DevTools panel embedding.
