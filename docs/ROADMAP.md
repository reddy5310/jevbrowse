# Roadmap

## Committed (alpha → beta)
- MSIX packaging with signed builds (needs a code-signing certificate decision).
- Full Public Suffix List for first/third-party classification (Shield, Trust OS).
- Cosmetic (element-hiding) rules with per-element undo (§9).
- Attention Shield intent modes: READ / RESEARCH / SHOP / CODE / FOCUS (§9.1).
- Prewarm execution (the scheduler already nominates candidates).
- Page-health trends and performance doctor in DevSpace.
- Local model provider for JevBrain (the `Local` slot exists and is empty).
- Encrypted sensitive metadata at rest.
- Per-agent CPU/network accounting; MCP adapter over the local gateway host.
- Jev provider verification once the API shape is confirmed.

## Exploring
- Local embeddings + HNSW for Browser Memory (Table A.3 evolution path).
- Optional encrypted sync of workspaces/policies (only after local durability/privacy is mature, §27).
- Extension/capability governor with site-scoped grants (§14).
- Proxy per identity container.

## Rejected, with rationale
- **Custom engine / Chromium fork.** Maintenance cost dwarfs the benefit; WebView2 Evergreen keeps the engine current (ADR 0001).
- **Universal AI ad blocking on the request path.** Non-deterministic, slow and unauditable; the deterministic engine stays the hot path (ADR 0006).
- **Perfect SPA/JS state restoration.** Cannot be done safely; semantic checkpoints only (ADR 0004).
- **Autonomous agent access to personal browser state.** Manifest-scoped sessions only (ADR 0012).
- **Mandatory account, sync, rewards or AI.** Constitution rules 2 and 5.
- **RAM-saving claims before reproducible benchmarks.** Baselines first (docs/performance).

## Added after the independent review (ADR 0016)
- **`Unknown` data class** with conservative persistence, plus a positive-evidence model for PUBLIC (today unrecognised, signal-free pages default to PUBLIC).
- **Restore-latency work**: measure and reduce pre-navigation cost (cosmetic sheet registration, site modules) back toward the Phase 2 350 ms.
- Real-engine checks for: agent redirect enforcement, Shield per-site disable, permission lifetime across restart (`SavesInProfile=false`), a stricter class purging inside a live renderer.
- Whole-browser memory measurement (shell process + engine) on 8/16/32 GB machines, low-memory runs, background CPU.
- Bookmark import/export and a migration path from Chrome/Edge/Firefox.
- Accessibility pass: keyboard-only, screen reader, high contrast, scaling, narrow windows.
- Signed MSIX and a security-update path; crash recovery journal.
- Agent CPU/network quotas.
