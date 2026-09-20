# Governance

## Roles
- **Maintainers** merge PRs, cut releases and approve ADRs. Initially: @reddy5310. Maintainers are added by consensus of existing maintainers after sustained, quality contributions.
- **Contributors** are anyone with a merged PR.

## Decisions
- Day-to-day: maintainer review on PRs.
- Architectural: an ADR in `docs/adr/`, open for comment for at least 72 hours, accepted by maintainer consensus. ADRs are never deleted; superseded ones say so.
- Performance regressions beyond the gates in `docs/performance/BASELINES.md` require an ADR and explicit maintainer approval (Constitution rule 7).
- Conflicts are resolved by maintainer vote; ties defer to the Product Constitution.

## Roadmap transparency
`docs/ROADMAP.md` lists items as **committed**, **exploring**, or **rejected with rationale**. Rejections stay listed.

## Name and logo
The software license (MPL-2.0, see `LICENSE`) and the JevBrowse name/logo are separate. Forks are welcome and must not present themselves as official builds; see `TRADEMARK.md`.

## Releases
Reproducible from a tagged commit with `scripts/release.ps1`; every release publishes SHA-256 checksums and the SBOM produced by `dotnet` package listing. Telemetry: none.
