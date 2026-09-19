# ADR 0007 — Context OS: workspaces are scheduling domains; Time Travel restores lazily

Status: accepted (Phase 5 gate, 2026-09-20)

Decision:
- A workspace is a `ContextId` that every tab belongs to. Switching workspaces changes **scheduling priority, not renderer lifetime** (§7): the previous workspace's live tabs stay live and the Resource OS drains them at a shorter idle threshold (`IdleBeforeVirtualize × BackgroundPriority`, default 0.3 → ~3× sooner). No mass-dispose on switch, so switching back is instant while pressure is low.
- The sidebar shows only the active workspace's tabs; the others remain durable, searchable later (Phase 8), and visible in the workspace selector's count. Visible ≠ live still holds one level up: *listed workspace ≠ live tabs*.
- Time Travel checkpoints (§7.1) are tiny records (workspace, active resource, tab id/url/title/was-live) written every 5 minutes and on every switch; retention is bounded (newest 50 per workspace, 30 days). Restoring a checkpoint recreates missing tabs as VIRTUAL and loads only the checkpoint's active resource.
- Activating a tab from another workspace switches to that workspace implicitly, so a tab is never shown outside its context.

Verified by `ContextOsTests`: 2 contexts × 40 resources, the inactive context drains fully to VIRTUAL under a Balanced scheduler while the active one keeps its working set; checkpoint restore recreates 3 closed tabs with 1 live renderer.

Deferred: per-workspace identity/container binding (Trust OS, Phase 6), notification muting enforcement (needs Trust OS permission plumbing), semantic index per workspace (Phase 8).
