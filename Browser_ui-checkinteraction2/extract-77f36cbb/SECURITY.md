# Security policy

## Reporting
Please report vulnerabilities privately through GitHub's "Report a vulnerability" on this repository (Security → Advisories). Do not open a public issue. You will get an acknowledgement within 72 hours and a fix or mitigation plan within 14 days for confirmed issues.

## Supported
Pre-release (alpha). Only the latest `main` is supported.

## Scope of interest
- Anything that lets a page reach the host beyond the three fixed bridge strings (`docs/threat-model/NATIVE_BRIDGE.md`).
- Anything that lets an agent session exceed its manifest (`docs/AGENT_SECURITY.md`).
- Anything that persists or transmits SENSITIVE/SECRET/EPHEMERAL content (`docs/AI_POLICY.md`, ADR 0008).
- Filter-list update tampering that survives validation/rollback.
- Path traversal via downloads, titles or thumbnails.

## Out of scope
- Chromium/WebView2 engine vulnerabilities (report to Microsoft); JevBrowse keeps its process isolation and adds no host objects.
- Issues requiring a compromised OS account (the local DB is protected by the OS account boundary by design; encryption at rest is a roadmap item).
