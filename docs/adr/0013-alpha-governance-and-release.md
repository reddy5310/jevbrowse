# ADR 0013 — Alpha: governance, CI gates, reproducible portable release

Status: accepted (Phase 11, 2026-09-20)

Decision:
- **CI** runs on every push/PR: invariant gates first (no `AddHostObjectToScript` anywhere in `src/`; no API keys in the tree), then `dotnet test`, then a Release build of the shell. A separate `perf` job publishes a self-contained build and uploads raw `--memory-lab` / `--restore-bench` JSON. Perf is informational until a stable benchmark machine exists; hosted runners vary too much to gate on absolute numbers (§22 "stable benchmark machine").
- **Release** is a reproducible portable build (`scripts/release.ps1`): tests must pass, self-contained win-x64 publish with the Windows App SDK bundled, `BUILD.json` (version, commit, SDK), `sbom-packages.json` (every NuGet package incl. transitive), one zip, `SHA256SUMS.txt`. MSIX and code signing wait on a certificate decision (Roadmap).
- **Governance** files: CONTRIBUTING (boundaries table, workflow), SECURITY (private disclosure, scope), GOVERNANCE (roles, ADR process, name/logo separation), ROADMAP (committed / exploring / rejected with rationale), PR template with the cost card and the §27 checklist, issue templates including a Shield breakage form.
- **License**: deliberately not chosen by the maintainer's tooling. Until a `LICENSE` file is added the repository is "all rights reserved", so nothing is granted by accident. Recommendation on record: MPL-2.0 + separate trademark policy.
- **Product modes** (Table A.13) are a visibility/default layer over the same kernel: Simple, Focus, Power (default), Developer (enables DevSpace), Agent, Private (switches to a Private-container workspace). Chosen via the sidebar or `JEVBROWSE_MODE`.
- **Command palette** (`Ctrl+K`) implements the §26 examples deterministically; free text falls through to Browser Memory search. "Grant Claude Code localhost + GitHub for 30 minutes" opens a real scoped session and prints the endpoint and token.
- **Session receipts** (§15) label every line as *measured* or *ESTIMATE*; transferred bytes are reported as "not measured in V1" rather than guessed.

Verified by: CI workflow on `main`; `scripts/release.ps1` producing a zip with checksums; existing 163 tests unchanged.
