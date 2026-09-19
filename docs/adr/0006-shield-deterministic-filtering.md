# ADR 0006 — Shield: compiled deterministic filtering, conservative by design

Status: accepted (Phase 4 gate, 2026-09-20)

Decision:
- Network filtering is a compiled engine over a supported subset of EasyList/uBlock syntax (`||host^`, `|start`, `end|`, `*`, `^`, `@@`, `$third-party`, `$domain=`, resource types). Regex rules, cosmetic rules and unknown options are **skipped, never approximated**: a rule the engine does not fully understand is not applied (§9 "unknown → ALLOW").
- The engine sits synchronously on WebView2's `WebResourceRequested` path with no I/O, no awaits and no AI. Per-site enable state is an in-memory set; SQLite is only touched when the user toggles it.
- Top-level document navigations are never blocked in V1. A wrong rule may break a widget, never make a site unreachable.
- First/third-party classification uses registrable domain with a small built-in multi-label suffix table. Errs toward first-party. Full PSL is a documented follow-up.
- Every block records the exact rule; the Shield panel shows them and offers a one-click per-site disable that reloads the page.
- Filter lists live in `filters/{active,previous,staging}`. Downloads land in staging, are validated (≥1000 parsable rules), then promoted by directory rename; `previous` allows rollback. The only network call is documented in `docs/privacy/NETWORK_CALLS.md` and can be disabled.
- The lease manager exposes `OnCoreCreated` so Shield attaches without the renderer layer referencing it (Table A.4).

Measured (`--shield-check`, live EasyList + EasyPrivacy, 2026-09-20): 110,839 rules compiled; CNN 28/161 blocked, Forbes 18/99, The Verge 11/182, Wikipedia 0/26.

Not in V1 (§27): cosmetic/element hiding, Attention Shield intent modes, semantic clutter classifier. Cosmetic rules are the next Shield increment; they are parsed-and-skipped today.
