## What

## Why

## Feature cost card (required for any feature; delete for docs/test-only PRs)

```text
Feature:
Binary delta:
Idle RAM delta:
Background CPU:
Network calls:        documented in docs/privacy/NETWORK_CALLS.md / none
Disk growth:          bounded by … / none
Cloud dependency:     optional / none
Disableable:          yes / no (explain)
Security surface:
Benchmark result:     PASS / FAIL (attach --memory-lab / --restore-bench / --shield-check JSON if relevant)
```

## Checklist
- [ ] Hard rules still outrank AI and agents (no lower layer widens a higher decision)
- [ ] Nothing new persists for SENSITIVE/SECRET/EPHEMERAL without a Trust OS decision
- [ ] No new first-party network call, or it is documented and disableable
- [ ] No `AddHostObjectToScript`; native bridge messages remain fixed strings
- [ ] Tests added; `dotnet test` green
- [ ] Not on the "V1 must NOT attempt" list (Architecture §27) — or an ADR explains why
- [ ] ADR added/updated if this changes an architectural decision
