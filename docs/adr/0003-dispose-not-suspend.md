# ADR 0003 — Memory is reclaimed by disposing renderers, not suspending them

Status: accepted (Phase 0 gate, measured 2026-09-20)

Measurement (`JevBrowse.App --memory-lab`, 5 mainstream pages, private bytes of the WebView2 process group):

| Step | Procs | Private MB |
|---|---|---|
| 5 live | 9 | 846 |
| 4 suspended (`TrySuspendAsync`), 1 live | 9 | 799 (−6%) |
| 4 disposed (`Close()`), 1 live | 5 | 470 (−44%) |
| all disposed | 0 | 0 |

Decision:
- `SUSPENDED` is a CPU/timer measure only; the scheduler must never count it as memory reclaimed.
- `VIRTUAL` (renderer disposed, checkpoint retained) is the only memory-reclaiming state. Resource OS budgets are expressed in **live renderer count** and **measured process-group private bytes**, never in WebView count.
- WebView2 requires a view to be hidden before suspend; the lease manager hides on COLD→SUSPENDED.

Per-page marginal cost on this machine: ~150–240 MB private; fixed overhead of browser/GPU/utility processes ~140 MB.
Raw report: `D:\Browser\data\benchmarks\memory-lab-20260920-022653.json` (not committed; benchmark reports are local data).
