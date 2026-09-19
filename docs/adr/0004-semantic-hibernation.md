# ADR 0004 — Semantic hibernation: checkpoint → commit → dispose

Status: accepted (Phase 2 gate, measured 2026-09-20)

Decision:
- A VIRTUAL tab is reconstructed from a `Checkpoint` (URL, title, scroll, favicon, thumbnail path). Nothing else from the renderer is persisted: no form values, cookies, JS heap, or password fields (Table A.5).
- Order is strictly checkpoint → single SQLite transaction → dispose renderer. If capture fails the tab still virtualizes without a checkpoint; a crash mid-way can only lose a checkpoint, never a tab.
- Protection flags come in two kinds: **user** flags (pinned, never-hibernate) are persisted; **detected** flags (audible, download in progress, dirty form) are transient, fed by the live page and cleared when the renderer goes away. Either kind vetoes automatic demotion; only a user action overrides.
- The dirty-form signal uses a one-way `postMessage` of a fixed string; password inputs are excluded and no host object is exposed to the page (Table A.11).

Platform findings:
- WebView2 `CapturePreviewAsync` never completes on a collapsed view, so thumbnails are captured when the tab leaves the foreground (`SetVisible(false)`), not at virtualize time. All capture calls carry a 3 s timeout so virtualize cannot hang.
- `TrySuspendAsync` requires a hidden view (already applied in Phase 0).

Measurement (`--restore-bench`, 8 pages, this machine, warm cache):
restore p50 **350 ms**, p95 **1,236 ms**; checkpoints 8/8, thumbnails 8/8 (~115 KB each), scroll restored 7/7 scrollable pages.
