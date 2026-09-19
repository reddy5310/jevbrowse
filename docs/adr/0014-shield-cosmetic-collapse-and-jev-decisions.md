# ADR 0014 — Shield visible-clutter layers, and Jev as a typed decision provider

Status: accepted (2026-09-20)

## Why
Live tests on youtube.com, cricbuzz.com and goodreturns.in showed the network layer working (10–59 requests blocked per page, Cricbuzz's 3 ad slots collapsed) but leaving **empty reserved boxes**: a 120 px blank band and an "ADVERTISEMENT" placeholder on Goodreturns, an empty white card in Cricbuzz's carousel. Goodreturns also uses `ad-shield` anti-adblock recovery that proxies ad frames first-party, so blocked-host matching alone cannot find them.

## Decision: three deterministic layers, then one optional semantic layer
1. **Network** (ADR 0006): unchanged, still the only hot-path layer.
2. **Cosmetic**: EasyList `##` element-hiding rules compiled into one stylesheet per host (generic + domain-scoped, minus `#@#` exceptions; procedural `#?#`/`:has()`/scriptlets skipped, never approximated). ~13.6 k selectors per page, injected at document start; per-site disable removes it. Compile cost for both engines stays ~260 ms.
3. **Collapse** (after load, twice): hide frames/media whose requests Shield blocked, and empty reserved boxes when *all* hold: ads were actually blocked on this page, the box is ad-named (id/class token up to 4 ancestors) or labelled "advertisement", it is ≥ 90×120 px, and it has no real text, media, links or live non-ad iframe. Every collapsed element is tagged (`data-jev-collapsed=reason`) so it is inspectable and reversible.
4. **Semantic** (optional, JevBrain layer 4): residual large empty boxes (≤ 12, structural descriptors only: tag/id/class/size/position/neighbour counts/short label) are sent as one batched Jev **noul** question each; boxes with p ≥ 0.8 are collapsed. Runs only with AI + Cloud on, on PUBLIC pages, after load, never on the request path; answers are numbers in the decision log.

Measured 2026-09-20: goodreturns.in home 53/163 blocked, 6 collapsed → clean; gold-rates 59/140 blocked, 11 collapsed → clean (see POSITIONING.md); cricbuzz 10/70 blocked, 0 structural collapses (residual card is the semantic layer's job).

## Jev is a decision model
TypeSafe Jev answers typed **choice / score / noul** questions with calibrated probabilities at `POST https://api.typesafe.ai/v1/systemone` (verified live: `jev-1.13.0`, ~1.1 s, sensible answers). It replaces the chat-shaped placeholder: `IDecisionProvider` + `BrainRouter.JudgeAsync` with the same gates as any cloud call (AI on, Cloud on, class ≤ AUTHENTICATED, never SECRET/EPHEMERAL, redacted state). Uses today:
- **Page classification** after load: may only **raise** the data class at confidence ≥ 0.7 (`TabKernel.RaiseClass`; deterministic SECRET still wins; advisory resets on navigation).
- **Search rerank**: per-candidate score × confidence.
- **Clutter**: the semantic layer above.
The state sent is structure (host, path, title, ≤ 8 headings, flags), never page text or query strings (`NETWORK_CALLS.md`).

## Not decided / risks
- False positives in layer 3 would hide legitimate empty containers; the four-condition guard plus the "ads were blocked here" precondition keeps it narrow, and per-site disable is one click. A breakage report template exists.
- YouTube video ads are server-stitched; none of these layers claim to remove them and the docs say so.
