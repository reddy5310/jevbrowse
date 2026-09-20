# Contributing to JevBrowse

Read `docs/PRODUCT_CONSTITUTION.md` first. It is the contract every change is measured against.

## Ground rules
- **Hard rules outrank AI.** Trust OS, then deterministic rules, then local scoring, then Jev, then LLMs. A PR that lets a lower layer widen a higher decision will not merge.
- **Visible resource ≠ live renderer.** Nothing may assume a tab has a WebView.
- **Performance regressions are correctness regressions.** Attach benchmark JSON when you touch the kernel, scheduler, Shield or storage. Baselines: `docs/performance/BASELINES.md`.
- **Every feature has a cost card** (PR template). Major features are modules, not resident services.
- **Every first-party network call is listed** in `docs/privacy/NETWORK_CALLS.md` and disableable.

## Architecture boundaries (Table A.4)
| Project | Owns | Must not |
|---|---|---|
| Domain | models, state machine, invariants | reference anything else |
| Renderer.Abstractions | lease contracts | know WebView2 |
| VirtualTabs | tab identity, lifecycle, checkpoints, leases | UI, AI providers |
| ResourceOS | pressure bands, plans, anti-thrash | WebView events |
| ContextOS (in VirtualTabs) | workspaces, time travel | renderer lifetime |
| TrustOS | classes, policy matrix, permissions | UI prompts |
| Shield | rule engine, list store | cloud AI |
| Brain | router, redaction, providers | hard overrides |
| Memory | index, search | bypassing Trust OS |
| DevSpace | environments, diagnostics | mandatory footprint |
| AgentGateway | scopes, page maps, quotas, audit | raw host access |
| App | WinUI shell and WebView2 adapters | policy |

## Workflow
1. `. .\scripts\env.ps1` (keeps SDK, caches and data on the drive you choose), `dotnet test`.
2. Branch from `main`; one concern per PR.
3. Add tests in the matching `tests/Unit/*` project. Kernel behaviour is tested against `FakeLeaseManager`, never a real WebView.
4. Architectural changes need an ADR in `docs/adr/` (next number).
5. CI must be green, including the invariant gates.

## License of contributions
Inbound = outbound: by submitting a contribution you license it under MPL-2.0, the same terms as the project (MPL-2.0 §3.1 / §3.3). No CLA. Do not add third-party code under licenses incompatible with MPL-2.0 (GPL-only code cannot be mixed into MPL files; Apache-2.0 and MIT are fine and must keep their notices).

## Coding standards
- .NET 10, nullable on, warnings are errors.
- Match the surrounding style. Comments explain *why*, not *what*.
- No secrets in the repo, ever. Keys come from the environment.
