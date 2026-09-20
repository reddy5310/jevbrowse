# ADR 0016 — Response to the independent code review

Status: accepted (2026-09-20)

An independent read-only review of the alpha found that the strongest privacy, safety and durability claims were not yet true in code, even though each component had tests. We verified every finding against the source before acting; all 15 areas were real. This ADR records what changed and the principles that now govern the code.

## Principles adopted
1. **Authorize before you act.** Persistence is decided before any capture or write, not after (thumbnails were captured on deactivation and only checked later).
2. **Identity is immutable.** A live renderer cannot change profile, so a workspace's container is fixed at creation. Crossing an identity creates a new tab; it never relabels one.
3. **Stricter evidence retroactively removes.** When a page's class tightens, everything the looser class stored is deleted and derived data is forgotten.
4. **Serialize ownership.** Renderer acquisition, disposal, activation, workspace switching, plan application and restore share one gate.
5. **Fail safe, not fail forward.** A failed capture aborts an automatic hibernation and keeps the renderer; a failed commit rolls the model back; a failed migration step rolls back completely.
6. **Authority comes from the user, not the token.** Agents can only narrow a user-approved ceiling; sessions are throwaway identities.
7. **Consent is per behaviour.** Turning AI on is consent to explicit actions; background calls need their own switch.
8. **A test must assert the system property.** Privacy claims are now checked against real renderers and the real disk (`--privacy-check`).

## What changed (review item → fix → proof)
| # | Finding | Fix | Proof |
|---|---|---|---|
| 1 | Private tabs wrote thumbnails; private workspaces were recorded in Time Travel | `AllowThumbnails` per lease, set by the kernel before capture; ephemeral workspaces never recorded or persisted | `--privacy-check` (real engine) + 6 unit tests |
| 2 | Moving a tab changed its label, not its profile; workspace container set after save | Immutable container; cross-identity move opens a new tab and purges the old; ephemeral workspaces get their own profile | `Moving_across_identities…`, `Workspace_container_is_persisted_at_creation…` |
| 3 | Temporary permissions persisted in WebView2's profile; grants keyed by registrable domain | `SavesInProfile=false`; key = container + exact origin; ephemeral grants in memory | `PermissionKeyTests` |
| 4 | Insufficient evidence treated as PUBLIC; signals replaced URL class | Stricter-of combination; page reports logged-in state; indexer re-checks after async extraction; purge on tightening | classifier + indexer tests. **`Unknown` class deferred** (documented) |
| 5 | Agent could request its own authority; workspace-name lookup reached personal identities; shared disposable profile | `AgentCeiling`, fail-closed host, per-session approval, fresh workspace and profile per session, palette grants registered | 8 authority tests |
| 6 | Weak destructive detection, navigation scope, quota, expiry | Element inspection, `NavigationGuard` on every navigation, hard quota, expiry sweep | gateway tests |
| 7 | Lifecycle operations unserialized | Single gate + Core methods | concurrency tests |
| 8 | Capture failure still disposed; stale checkpoint URL restored | Abort automatic demotion; tab URL is truth; final checkpoint at shutdown; warn on manual hibernate of protected tab | hibernation tests |
| 9 | Shield "disable" left layers; shared script id; stale delayed work | Per-renderer script ownership, disabled list baked into modules, document-versioned delayed passes, "Show what Shield hid" undo | code + manual; real-engine check pending |
| 10 | Admission bypassed scheduler residency | Aged-first admission with recorded reason | `Foreground_admission_records_why…` |
| 11 | AI audit and payload gaps | Automatic-judgments switch, Trust + size gates on typed calls, question text redacted, opaque option keys (**found a real leak of workspace names**), accurate log fields | Brain tests |
| 12 | Memory ranking and retention | BM25 sign fix, per-page docs on every navigation, retention past tab close + Clear, real DB size shown | Memory tests |
| 13 | Crash-safety claims | Per-step transactional migrations, commit rollback, filter-store recovery | storage tests |
| 14 | Performance evidence narrow | Documented limits in the evidence matrix; no new claims | matrix |
| 15 | Tests proved components | `--privacy-check`, evidence matrix, regression test per finding | this ADR |

Product items: default mode is Simple; modes are capability switches (Simple/Private stop indexing, leaving Developer stops DevSpace collection); conventional shortcuts added (Ctrl+L/T/W, Ctrl+Shift+T, F5/Ctrl+R, Ctrl+Tab, zoom).

## Not done, deliberately
Unknown data class; real-engine redirect enforcement test for agents; per-renderer real-engine test of Shield disable; bookmark import; accessibility audit; signed MSIX and update path; CPU/network agent quotas; whole-browser memory measurement. All are in `docs/ROADMAP.md` and marked in the evidence matrix.
