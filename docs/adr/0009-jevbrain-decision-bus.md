# ADR 0009 — JevBrain: a policy-controlled decision bus, off by default

Status: accepted (Phase 7 gate, 2026-09-20)

Decision:
- `BrainRouter` implements the five-layer priority of §11 literally: hard safety/user policy → deterministic rules → local → Jev → LLM. Each layer can only *refuse* or *answer*; none can widen what a higher layer allowed.
- Two independent switches, both default **off**: `AiEnabled` and `CloudEnabled`. With AI off every request is answered by rules or refused, and still logged.
- Hard rules: SECRET/EPHEMERAL classes and Private/Disposable containers never leave the device, whatever the user clicks; oversized inputs are refused before provider selection; resource scheduling never consults a provider (§29 item 9).
- Cloud gate = AI on ∧ Cloud on ∧ (Trust OS allows `SendToCloudAI` ∨ explicit user action on a page ≤ AUTHENTICATED). SENSITIVE is local-only (Table A.8).
- Every cloud-bound text passes `Redactor` (emails, Luhn-valid cards, API keys/tokens, JWTs, IBANs, phones, Aadhaar-shaped ids). Redaction count is part of the decision record and shown to the user *before* sending.
- Providers are dumb OpenAI-compatible chat clients (`OpenAiCompatibleProvider`) built from environment variables only; JevBrowse stores no keys. Jev is wired through `JEV_API_BASE` pending confirmation of its API shape.
- Every decision, including refusals and provider failures, is appended to `decision_log` with source, rule, model, data class, redaction count and input size. Never the input. `CloudCallsByClass()` is the Table A.15 privacy metric.
- UI: **Ask** is the only cloud entry point in this phase; it shows provider, model, character count and redaction summary and requires a click to send. **Brain** panel holds the switches and the log.

Verified by `BrainRouterTests` (kill switch; secret/ephemeral/private hard-denied even on explicit request; explicit-only tasks; sensitive local-only; cloud switch; preference/fallback; provider failure recorded; oversized refused) and `RedactorTests`.

Deferred: local model provider (Local is a slot with no implementation), Jev-assisted classification and search rerank (Phases 8), per-task provider selection UI, cost/budget accounting per provider.
