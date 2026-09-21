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

## Your first change in ten minutes
1. `.\scripts\dev.ps1 test` (about a minute), then `.\scripts\dev.ps1 run` to see the app.
2. Pick something small and pure: `src/JevBrowse.Domain` has no dependencies (address input, bookmark import, zoom steps, search engines). Add a search engine to `SearchEngines.All` and a test beside `BookmarkAndSearchTests`, or make bookmark import understand another export format.
3. `.\scripts\dev.ps1 check` before you open the PR.

## How a change is checked
- **A fix needs a regression that fails without the fix.** Show it: temporarily undo the fix, run the test, watch it fail, restore. (Twice this has caught a test that could never fail: a `javascript:` import test passed only because a different check caught it.)
- **Privacy rules are tests, not comments.** If a feature records something (history, zoom, downloads), a test must show Private sessions, agent sessions and Sensitive pages leave nothing behind. `LibraryTests` is the pattern.
- **Database changes are new migrations, never edits.** Add a step to `BrowserDb`; `UpgradeFromHistoricDatabasesTests` pins each released step by hash and upgrades real old database files.
- **UI behaviour that needs the real engine** goes in a `--*-check` mode (`MainWindow.*Check.cs`) or a script in `scripts/`; every wait has a timeout, and a timeout is a failure, never a pass.
- **Say what you did not verify** in the PR. "Tested by calling the same code path, not by clicking" is a fine sentence; leaving it out is not.

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
