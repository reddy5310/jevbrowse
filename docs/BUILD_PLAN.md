# JevBrowse — Build Plan (working plan, derived from Architecture v1.0)

Core invariant: `VISIBLE RESOURCE ≠ LIVE RENDERER`.
Method: **measure first, feature second**. Every phase ends with a gate that must pass before the next starts.

## 0. Environment rules (disk)

C: is nearly full, so everything lives on D:.

| Thing | Location |
|---|---|
| Source | `D:\Browser\jevbrowse` |
| .NET SDK | `D:\Tools\dotnet` |
| NuGet packages + caches | `D:\Tools\nuget` |
| dotnet CLI home / temp | `D:\Tools\dotnet-home`, `D:\Tools\tmp` |
| Build output | `D:\Browser\jevbrowse\artifacts` (via Directory.Build.props) |
| Runtime data (dev) | `D:\Browser\data` (via `JEVBROWSE_DATA_DIR`; product default stays `%LOCALAPPDATA%\JevBrowse`) |
| WebView2 user-data folders | under the data dir, so they never touch C: |

`scripts/env.ps1` sets all of this per-shell; nothing is written to the user's global environment.

## 1. Key technical decisions (ADR seeds)

1. **Toolchain**: .NET 10 SDK + WinUI 3 / Windows App SDK, built with the `dotnet` CLI, **unpackaged** (`WindowsPackageType=None`) for dev so no Visual Studio/MSIX tooling is needed. MSIX comes at M11.
2. **Renderer**: WebView2 Evergreen behind `IRendererLease` abstractions. Core domain never references WebView2.
3. **Storage**: SQLite (`Microsoft.Data.Sqlite`) in WAL mode, with FTS5 for Browser Memory.
4. **Secrets**: `JEV_API_KEY` / `OPENROUTER_API_KEY` are read from the environment or the user's secrets store. The `D:\Dashboard` `.env` is a dev-only source and is never copied into the repo. AI is off by default and everything works without it.
5. **Testability**: Domain, ResourceOS, Shield and Brain are pure class libraries (`net10.0`) so scheduler logic is unit-testable and simulatable without a UI.
6. **Hard rules outrank AI**: JevBrain can only *advise* inside a veto envelope computed by deterministic rules.

## 2. Phases

Each phase maps to a milestone in the architecture doc (Table A.14).

## Status

| Phase | State | Evidence |
|---|---|---|
| 0 Foundations | DONE 2026-09-20 | ADR 0003; `--memory-lab` report: dispose −44%, suspend −6% |
| 1 Virtual Tab Kernel | DONE 2026-09-20 | 9 kernel tests (50 visible / 5 live, restart survival); real-pool lab: 5 live 765 MB → 1 live 319 MB |
| 2 Hibernation | next | |

### Phase 0 — Foundations (M0 Feasibility)
- Repo skeleton per §17, `Directory.Build.props`, solution, CI skeleton, docs (Constitution, ADR 0001–0005, cost-card template).
- WinUI 3 shell hosting one WebView2, with address bar.
- Process-group memory instrumentation (WebView2 process tree via `CoreWebView2.BrowserProcessId` + `ProcessInfo`, private working set).
- Experiments: create / dispose / `TrySuspendAsync` a WebView, quantify reclaimed RAM.
- **Gate**: reproducible report showing per-WebView memory and how much is reclaimed by dispose vs suspend.

### Phase 1 — Virtual Tab Kernel (M1)
- `Domain`: `ResourceId`, `VirtualTab`, `ResourceState` state machine (HOT/WARM/COLD/SUSPENDED/VIRTUAL/ARCHIVED) with guarded transitions.
- `Storage`: SQLite schema + migrations, durable logical tabs, session journal.
- `IRendererLeaseManager` + bounded renderer pool (WebView2 impl).
- UI: vertical tab strip that shows 50+ logical tabs with ≤5 live renderers.
- **Gate**: 50 visible / 5 live works; tabs survive app restart; unit tests for every state transition.

### Phase 2 — Hibernation (M2)
- Semantic checkpoint (URL, title, favicon, scroll, thumbnail, policy-gated clean text). Secrets never persisted (Table A.5).
- Virtualize → dispose renderer → restore from checkpoint; crash-safe commit (write-then-rename / SQLite txn).
- Protected-context veto: audible, WebRTC, download, dirty form, pinned.
- **Gate**: crash-during-checkpoint test recovers; virtual restore p95 measured.

### Phase 3 — Resource OS (M3)
- `SystemPressure` sampler (available memory, process-group working set, power/metered).
- `IResourceScheduler.Evaluate` → `ResourcePlan`; pressure bands with hysteresis, min residency, cooldown, max hibernations/window, prewarm-cancel-first (§20).
- Memory modes (Performance / Balanced / Low RAM / Battery / Data Saver / Custom).
- Decision records with human-readable reason + override buttons (§11.1).
- Simulation harness that replays pressure traces against the scheduler and asserts no thrashing.
- **Gate**: thrash-free on injected pressure trace; benchmark suite + baselines committed.

### Phase 4 — Shield Engine (M4)
- Compiled rule engine (ABP/uBlock-style network subset → hashed/trie index), first/third-party classification, `WebResourceRequested` adapter, conservative "unknown → ALLOW".
- Per-site disable, page shield panel, breakage log. Cosmetic rules with undo.
- Signed/validated filter list update with atomic activation + rollback.
- **Gate**: lookup p95 in µs range; blocked/allowed counters; site-disable works.

### Phase 5 — Context OS (M5)
- Workspaces as scheduling domains (priority feeds Resource OS), context switching, timeline checkpoints ("Time Travel"), lazy restore.
- **Gate**: 2 contexts × 40 resources switch with inactive context ~fully virtual.

### Phase 6 — Trust OS (M6)
- Identity containers (Personal/Work/Dev/Disposable/Private = separate WebView2 user-data folders/profiles).
- Data classes PUBLIC/AUTHENTICATED/SENSITIVE/SECRET/EPHEMERAL; `ITrustPolicy`; temporary permission grants; narrow origin-bound native-bridge message schemas.
- **Gate**: private container leaves zero durable trace; security tests for bridge.

### Phase 7 — JevBrain (M7)
- `IBrainRouter` with layered priority (hard → rules → local → Jev → LLM), provider interfaces None/Jev/OpenRouter/custom.
- Explainability log; redaction before any cloud call; per-data-class cloud-AI gate; kill switch.
- **Gate**: tests proving zero secret-field transmission and that Brain cannot override hard veto.

### Phase 8 — Browser Memory (M8)
- Content extraction → boilerplate reduction → FTS5 index, privacy-class gated; optional local embeddings later.
- Query path: FTS → local rank → optional Jev rerank → optional LLM synthesis only on explicit request.
- **Gate**: search across virtual tabs works offline.

### Phase 9 — DevSpace (M9) — optional module
- Environment spaces (LOCAL/DEV/STAGING/PROD) with PROD chrome, localhost dashboard, network/error grouping.

### Phase 10 — Agent Gateway (M10)
- Agent manifests (§12.1), scope enforcement (workspace/domain/data-class/actions/quotas), lazy virtual-tab-aware page maps, audit trail. Optionally exposed as a local MCP server.
- **Gate**: unauthorized access attempts blocked; live-renderer quota respected.

### Phase 11 — Alpha (M11)
- Command palette (§26), resource receipts (§15), product modes, MSIX + signing, SBOM/checksums, governance docs, perf-CI baselines, security review.

## 3. Cross-cutting rules
- Every feature PR carries a cost card (`docs/governance/COST_CARD_TEMPLATE.md`).
- No feature calls the network without being listed in `docs/privacy/NETWORK_CALLS.md`.
- Performance regressions are correctness regressions: benchmarks run in CI from Phase 3 on.
- Things V1 must NOT do (§27) are enforced as a checklist in the PR template.

## 4. Definition of done for the whole V1
Success metrics in Table A.15: fixed-workload RAM, restore p50/p95, crash recovery, idle CPU, filter latency, prewarm hit-rate, zero secret transmission, agent scope enforcement.
