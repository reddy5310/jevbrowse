# JevBrowse Product Constitution

Core invariant: **VISIBLE RESOURCE ≠ LIVE RENDERER.**

1. No ads in chrome, no affiliate URL rewriting.
2. No mandatory account, cloud sync, crypto/rewards, or AI. Basic browsing works fully with every AI provider disabled.
3. No selling browsing history; no opaque telemetry.
4. Every first-party network call is inspectable and listed in `docs/privacy/NETWORK_CALLS.md`.
5. Every cloud AI feature is disableable.
6. Major features are modules, not permanently resident services.
7. Performance regressions are correctness regressions.
8. Users own and can export their tabs, workspaces, policies, local index and settings.
9. Sensitive pages default to less persistence and less AI, never more.
10. Automated decisions expose a human-readable reason and a direct override.
11. Security isolation is never weakened to win RAM benchmarks.
12. Extensions/capabilities get least privilege by default.

Decision priority (hard rules always win): hard safety/user policy → deterministic rules → local scoring → Jev → GPT/Claude/local LLM.
