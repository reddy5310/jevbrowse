# AI policy

JevBrain is a decision bus, not an assistant (§11). These rules are enforced in `BrainRouter` and tested in `JevBrowse.Brain.Tests`.

## Defaults
- AI is **off**. Cloud AI is **off**. Both are separate switches; basic browsing never depends on either.
- Keys come only from the OS environment (`OPENROUTER_API_KEY`, `JEV_API_KEY`, optional `JEV_API_BASE`, `OPENROUTER_MODEL`, `JEV_MODEL`). JevBrowse stores no keys.

## Hard rules (layer 1, cannot be overridden by any setting, provider or agent)
1. SECRET and EPHEMERAL pages, and anything in a Private/Disposable container, never leave the device.
2. Inputs above the configured size are refused before any provider is chosen.
3. Resource scheduling is decided by rules/local scoring; a provider is never consulted for it in V1.

## Cloud gate
A cloud provider may be used only when **all** hold: AI on, Cloud on, and either Trust OS allows `SendToCloudAI` for the page's data class (PUBLIC by default) or the user explicitly invoked the action on an AUTHENTICATED page (Table A.8 "cloud only by explicit override"). SENSITIVE pages are local-only.

## Redaction
Everything sent to a cloud provider passes `Redactor` first: emails, Luhn-valid card numbers, API keys/tokens, JWTs, IBANs, phone numbers, Aadhaar-shaped numbers. This is a backstop; the class gate is the primary control.

## Explainability
Every decision (including refusals) is appended to `decision_log`: time, task, source, rule, model, data class, redaction count, input size, a 200-char output preview. Never the input. `CloudCallsByClass()` is the privacy metric from Table A.15.

## Provider preference (Table A.8)
| Task | Order |
|---|---|
| Schedule hint | rules → local → Jev (never LLM) |
| Ambiguous classification | Jev → local → OpenRouter |
| Summarize page | OpenRouter → Jev → local |
| Explain error | OpenRouter → Jev → local |
| Rerank search | Jev → local |

## Jev is a decision model, not a chat model
Jev (TypeSafe AI System One) answers **typed questions** (choice / score / noul) with calibrated probabilities and no prose. JevBrowse uses it only for judgements, through `BrainRouter.JudgeAsync`, under the same gates as any cloud call:
- **Page classification** after load: may only *raise* a page's data class (PUBLIC → AUTHENTICATED/SENSITIVE) at confidence ≥ 0.7; never lowers. The state sent is structure only (host, path, title, headings, flags), never content.
- **Search rerank**: scores each candidate title; falls back to local order on refusal.
- Every answer is logged as numbers (`data_class=authenticated@0.90`), which makes the log auditable in a way prose never is.
Chat providers (OpenRouter) remain for explicit synthesis tasks only (Ask, Explain error).
