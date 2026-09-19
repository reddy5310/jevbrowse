# ADR 0005 — Resource OS: deterministic scheduler with anti-thrash rules

Status: accepted (Phase 3 gate, 2026-09-20)

Decision:
- `DefaultScheduler` is layer 2 of the decision priority (§11): deterministic, no AI, no renderer references. It emits a `ResourcePlan`; the kernel executes it and re-checks protection at execution time, so a plan can never override a veto that appeared after planning.
- Pressure bands (GREEN/YELLOW/ORANGE/RED) come from available-memory fraction with **hysteresis**: worsening is immediate, improving needs a 3-point margin past the band's entry threshold.
- Live-renderer budget per band: GREEN = mode max, YELLOW = max−2, ORANGE = max/2, RED = 1. Battery in Balanced mode tightens to at least YELLOW.
- Anti-thrash (§20): minimum residency after any state change, per-resource cooldown after an automated transition, and a hibernation-count limit per 5-minute window.
- **The window limit applies to opportunistic (idle-triggered) evictions only.** Over-budget evictions are required to hold the invariant and bypass it. The 2-hour sawtooth simulation (`ThrashSimulationTests`) found that gating both lets a user who switches tabs every 20 s grow the pool without bound.
- Every action and every skip carries a human-readable reason set (`inactive_minutes`, `system_pressure`, `revisit_score`, `trigger`, `skipped`, `jev_consulted: no`, …) surfaced in the UI as "Explain".
- Revisit score (layer 3, local) = recency decay over an hour + visit frequency (≤0.3) × workspace priority. Prewarm is offered only in GREEN with headroom and score > 0.6, and is the first thing dropped when pressure rises.

Not decided here: Jev-assisted scoring (layer 4) stays out until the local scheduler is objectively useful without it (§29 item 9).
