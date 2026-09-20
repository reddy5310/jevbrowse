# JevBrowse

**Open many. Run few. Keep context. Explain everything.**

An open-source, Windows-first browser built as a scheduler, memory system, privacy boundary and safe agent runtime, not just a tab container. Core invariant: **visible resource ≠ live renderer**. Hundreds of tabs can be open; only the working set holds a Chromium renderer.

Status: **alpha**, 11 architecture phases implemented, 163 unit tests, every phase gated on a measured result. See `docs/BUILD_PLAN.md`.

## What it does today
| Subsystem | Behaviour | Evidence |
|---|---|---|
| Virtual Tab Kernel | Tabs are durable SQLite rows; renderers are leases. 50 visible / 5 live; survives restart | `TabKernelTests` |
| Hibernation | checkpoint → commit → dispose; restore p50 350 ms | ADR 0004, `--restore-bench` |
| Resource OS | Pressure bands with hysteresis, live-renderer budget, anti-thrash, every decision explainable | ADR 0005, 2-hour thrash simulation |
| Shield | 110k EasyList/EasyPrivacy rules compiled, ~86 µs p95 lookup, per-site disable, no top-level blocking | ADR 0006, `--shield-check` |
| Context OS | Workspaces are scheduling domains; Time Travel restores lazily | ADR 0007 |
| Trust OS | Identity containers (separate profiles), data classes gate persistence, temporary permission grants | ADR 0008 |
| JevBrain | Off by default. Hard policy → rules → local → Jev → LLM. Redaction and a decision log | ADR 0009, `docs/AI_POLICY.md` |
| Browser Memory | Local FTS5 over public pages only, 200 MB bounded, optional rerank | ADR 0010 |
| DevSpace | Optional module. Explicit environments (never guessed), PROD chrome, error/network intelligence | ADR 0011 |
| Agent Gateway | Manifest-scoped sessions, page maps not DOM, quotas, human confirmation, audit, opt-in local endpoint | ADR 0012, `docs/AGENT_SECURITY.md` |

Measured baselines: `docs/performance/BASELINES.md`. Every first-party network call: `docs/privacy/NETWORK_CALLS.md`.

## Build and run
Requires Windows 11, the WebView2 runtime (ships with Edge), and the .NET 10 SDK (no Visual Studio needed).

```powershell
. .\scripts\env.ps1                                   # keeps SDK, caches and data where you choose
dotnet test                                           # 163 tests
dotnet build src/JevBrowse.App -p:Platform=x64
.\artifacts\bin\JevBrowse.App\debug_win-x64\JevBrowse.App.exe
```

Benchmarks: `JevBrowse.App.exe --memory-lab | --restore-bench | --shield-check | --memory-check` write JSON to `<data>/benchmarks`.
Portable release: `.\scripts\release.ps1 -Version 0.1.0-alpha.1` (self-contained zip + SHA-256 + SBOM).

Optional AI: set `OPENROUTER_API_KEY` (and `OPENROUTER_MODEL`) or `JEV_API_KEY` + `JEV_API_BASE` in your environment, then turn AI on in the Brain panel. Nothing is sent without a click.

## Keyboard
`Ctrl+K` opens the command palette (§26): hibernate everything except current, search browser memory, restore a context, explain a decision, open in a disposable identity, grant Claude Code localhost + GitHub for 30 minutes, and more.

## Project documents
`docs/PRODUCT_CONSTITUTION.md` · `docs/adr/` · `docs/ROADMAP.md` · `CONTRIBUTING.md` · `SECURITY.md` · `GOVERNANCE.md` · `docs/SECURITY_REVIEW_ALPHA.md`

## License
Code: [Mozilla Public License 2.0](LICENSE). Modify and redistribute freely; changes to MPL-covered files must be published under the same license; embedding in larger works, including proprietary ones, is permitted. Name and logo: [TRADEMARK.md](TRADEMARK.md), modified builds must be renamed.
