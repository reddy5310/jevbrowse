# Positioning: where the incumbents fail, and what JevBrowse does about it

Researched 2026-09-20 from public complaints, vendor announcements and issue trackers. Each row pairs a documented pain with the JevBrowse mechanism that answers it and how it is proven. Claims we cannot yet prove are marked.

## 1. Chrome: memory is the #1 complaint, and the fix was taken away
- Pain: "the most common complaint about the world's most popular browser"; ~1 GB per 10 tabs, 6–15 GB for heavy-app users (Atera, NinjaOne, Tabbiy benchmarks). Chrome's answer is "close tabs" or Memory Saver, which discards silently and restores blank.
- Pain: Manifest V3 removed uBlock Origin; Chrome 151 stripped the last MV2 paths (July 2026). Adblock Plus lost 7 M users in the transition. Ad blocking is now a declarative, capped rule set inside the vendor's own rules.
- JevBrowse: **visible resource ≠ live renderer**. 50 tabs, 5 renderers; measured 765 MB → 319 MB when 4 of 5 are virtualized (ADR 0003). Restore keeps URL, scroll, thumbnail, p50 350 ms (ADR 0004). Every eviction has an "Explain" with the inputs. Shield runs *inside the browser* on `WebResourceRequested` with 110 k EasyList/EasyPrivacy rules and no extension API ceiling (ADR 0006).
- Edge we can prove today: memory reclamation numbers, explainable decisions, first-class blocker that no store policy can remove.

## 2. Brave: the privacy browser that became the bloat browser
- Pain: crypto wallet, BAT rewards, Leo AI, VPN and news feed shipped by default; "PLEASE ditch crypto adware crap" threads; Leo hallucinated a malicious domain as safety advice (brave-browser #55879); no way to fully remove Leo/VPN from the UI (#54416). Brave's answer in June 2026: **Brave Origin, a $59.99 paid version with the bloat removed**.
- JevBrowse: Constitution rules 2, 5, 6: no rewards, no crypto, no mandatory AI; AI is *off* by default with two switches; every major feature is a module with a cost card; product modes hide what you don't use (Simple mode = tabs + Shield + privacy). Nothing to pay to remove.
- Edge: the Constitution is enforceable in code and CI (invariant gates), not a marketing promise.

## 3. Firefox: AI on by default, trust spent
- Pain: December 2025 revolt over AI features enabled by default and hard to disable; March 2025 Terms-of-Use backlash; kill switch promised only for 2026; local AI features raised CPU/RAM.
- JevBrowse: AI cannot run without the user flipping it on; SECRET/EPHEMERAL content is hard-denied even then; every AI decision is logged with source, rule and redaction count (ADR 0009). The "kill switch" is the default state, not a concession.

## 4. YouTube and anti-adblock: be honest
- Reality: since October 2026 YouTube targets Chromium-based browsers with server-side + client-side detection; Brave Shields "Standard" is too permissive, "Aggressive" trips detection. Server-side ad stitching cannot be blocked at the network layer by anyone.
- Our test (2026-09-20): youtube.com/watch — 7–13 tracking/ad requests blocked, video plays, no visible ad in the captured frame. **We do not claim to block YouTube video ads.** What we can claim: no tracking beacons, no `pagead` calls, and a Shield panel that shows exactly what was blocked so users are never surprised.

## 5. Indian ad-heavy sites: the visible-clutter problem
- cricbuzz.com: 10 requests blocked, 3 ad slots present, **0 visible** after cosmetic rules.
- goodreturns.in: 53–59 requests blocked (incl. the `ad-shield` anti-adblock recovery scripts); network layer alone left empty "ADVERTISEMENT" boxes and blank bands; cosmetic rules (13.6 k selectors) plus the collapse pass address the placeholders. Page-authored placeholders are exactly where a deterministic collapse pass beats list-only blockers, and where Jev's semantic classifier (opt-in) can add judgement without sitting on the request path.

## 6. What none of them have (our actual category)
| Capability | Chrome | Brave | Firefox | JevBrowse |
|---|---|---|---|---|
| Durable tabs independent of renderers, with explain + override | Memory Saver (silent) | — | — | Yes, measured |
| Workspaces as *scheduling* domains + Time Travel restore | Tab groups | — | Containers (identity only) | Yes |
| Identity containers with data classes gating persistence and AI | — | — | Multi-Account Containers (identity only) | Yes |
| AI as a policy-controlled decision bus, off by default, redacted, logged | Gemini | Leo | on-by-default | Yes |
| Agent Gateway: manifest-scoped sessions, page maps, quotas, audit | DevTools MCP (eager) | — | — | Yes, lazy |
| Session receipts labelled measured vs estimate | — | — | — | Yes |

## 7. Where Jev fits (and why it is a differentiator, not a gimmick)
Jev (TypeSafe AI) is a **typed decision model**: it answers Choice / Score / Noul questions with calibrated probabilities in 70–500 ms and never writes prose. That is the shape of every "uncertain, low-cost judgement" in the architecture: ambiguous data-class classification, clutter/annoyance likelihood, breakage risk of a filter rule, revisit likelihood for prewarm, and search rerank. It is cheap ($42 per billion input tokens), fast, and auditable (a probability is a number in the decision log, not a paragraph). It stays below hard rules and off the request hot path, and it is optional.

## 8. Claims we will not make yet
- "Uses X % less RAM than Chrome" — until the 100-doc / 50-site workloads (Table A.12) run on a fixed benchmark machine against Chrome on the same machine.
- "Blocks YouTube ads."
- "Prevents production mistakes" (DevSpace is a guardrail).
