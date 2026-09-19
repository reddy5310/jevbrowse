# Alpha security review (self-review, 2026-09-20)

Maps the threat model (Architecture Table A.11) to what is implemented, how it is verified, and what is still open. This is a maintainer self-review, not an external audit.

| Threat | Control | Verified by | Open |
|---|---|---|---|
| Malicious page invokes native bridge | No host objects; three fixed strings matched by equality (`WebView2Lease.PageScript`) | CI grep gate for `AddHostObjectToScript`; `docs/threat-model/NATIVE_BRIDGE.md` | Rate-limit `jev:dirty-form` spam; surface "keeps itself awake" |
| Agent reads private tabs | Manifest domain scope, content-class ceiling, hard secret-page deny, Disposable container default | `AgentGatewayTests` (13) | CPU/network accounting per agent |
| AI receives secrets | Class gate (SECRET/EPHEMERAL never), explicit-action rule, `Redactor` on every cloud call, preview before send | `BrainRouterTests`, `RedactorTests` | Local model provider so SENSITIVE can be summarized on-device |
| Snapshot leaks authenticated data | Trust matrix: thumbnails ≤ AUTHENTICATED, checkpoints ≤ SENSITIVE, nothing for SECRET; forbidden thumbnails deleted | `TrustOsIntegrationTests`, `TrustPolicyTests` monotonicity | Encryption at rest for `db/browser.db` |
| Extension overreach | No extension surface exists in V1 | n/a | Capability governor (roadmap) |
| Path traversal via download/title | Thumbnails named by ResourceId GUID; audit files by session id; downloads left to WebView2 default UI | code review | Explicit download path canonicalization when JevBrowse takes over downloads |
| Filter update compromise | HTTPS, ≥1000-rule validation, staging → atomic dir swap, `previous` rollback | `FilterListStoreTests` | Signature verification when lists publish signatures |
| Renderer compromise | Chromium/WebView2 process isolation retained; separate user-data folder per container; no host objects | design | — |
| Local DB theft | OS account boundary; no keys stored; ephemeral containers leave nothing | `Private_container_leaves_zero_durable_trace` | Optional encrypted sensitive metadata |
| Decision spoofing | `Decision` records carry source/rule/model/version from the router, not the provider; providers cannot set them | `BrainRouterTests` | — |
| Local agent endpoint abuse | Loopback only, per-run random bearer token, off by default, 1:1 with gateway | `Local_host_requires_token_and_forwards_to_gateway` | Token rotation UI; per-IP rate limit |

## Findings during the build (fixed)
- Scheduler window limit gated *required* evictions → unbounded pool under fast tab switching (ADR 0005).
- Agent data-class ceiling evaluated on the *container* class → Disposable made everything EPHEMERAL and bypassed the ceiling (ADR 0012).
- `CapturePreviewAsync` on a collapsed view never completes → could hang virtualize (ADR 0004; timeouts added).

## Not yet reviewed
MSIX packaging surface, crash-dump contents, and the Windows App SDK / WebView2 update path.
