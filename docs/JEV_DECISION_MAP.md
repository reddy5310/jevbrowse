# Jev decision map: where the CTO of decisions sits

Principle: **hard rules and the user decide what is allowed; Jev decides what is likely.** Every Jev call is a typed question with a calibrated probability, logged as numbers, off the request hot path, only with AI + Cloud on, and only on pages ≤ AUTHENTICATED with a structure-only state. Jev can make the browser *more* careful or *more* helpful; it can never override a veto.

| Decision | Deterministic layer decides | Jev question (type) | Effect of the answer | Status |
|---|---|---|---|---|
| Data class of a page | URL heuristics, password/payment signals, user override | `data_class` (choice: public / authenticated / sensitive) | Raise only, ≥ 0.7 confidence; resets on navigation | live |
| Residual ad slots after lists + collapse | Cosmetic rules, structural collapse | one `noul` per empty box: "is this an ad placeholder?" | Collapse at ≥ 0.8, tagged and reversible | live |
| Browser Memory ranking | FTS bm25 + recency + workspace boosts | `score` per candidate: irrelevant / partly / directly answers | Reorder by score × confidence | live |
| Which workspace a new tab belongs to | Active workspace | `workspace` (choice among workspace names) | Suggest a move in the status bar / palette, ≥ 0.8 | live |
| Prewarm candidate | Revisit score (recency + frequency) | `revisit` (score: unlikely / possible / likely) | Confirms or cancels a prewarm; **never used for eviction** | planned |
| Site breakage after a collapse | Collapse guards | `noul`: "does the hidden set look like site content?" | Warn and offer undo for that site | planned |
| Intent mode (READ / RESEARCH / SHOP / CODE) | User selection | `intent` (choice) | Suggest a mode; user confirms | planned |
| DevSpace error root cause | Error grouping and cascade | `noul` per group: "is this the root cause?" | Order groups for explanation | planned |
| Agent action risk | Destructive regex + manifest | `noul`: "is this click destructive?" | Tighten (ask the human) only; never loosen | planned |

What Jev never decides: whether content may leave the device, which renderer to dispose, whether to block a request, whether to grant a permission, anything on a SECRET or ephemeral page.

Cost model: $42 per billion input tokens; a page classification is ~400 tokens, so a heavy day of browsing costs a fraction of a cent, and everything is visible in the Brain panel's decision log.
