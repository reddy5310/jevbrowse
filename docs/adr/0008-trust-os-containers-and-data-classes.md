# ADR 0008 — Trust OS: identity containers, data classes, permission grants

Status: accepted (Phase 6 gate, 2026-09-20)

Decision:
- **Identity containers** (§10.1) are separate WebView2 user-data folders: Personal, Work, Dev, Disposable, Private. Cookies, storage and site permissions never cross them. A workspace binds to one container; a tab renders in its workspace's container. Private and Disposable use a per-session folder that is deleted on shutdown and swept on the next start.
- **Data classes** (Table A.10) are assigned deterministically by `DataClassifier`: ephemeral container → EPHEMERAL; page signals (password or payment input present) → SECRET; URL heuristics → SENSITIVE (banking, health, payroll, checkout) or AUTHENTICATED (mail, accounts, login, dashboards, localhost); else PUBLIC. A per-site user override applies, but a password field still forces SECRET. Heuristics never lower what the user set.
- **`DefaultTrustPolicy`** is the persistence/AI matrix as code, with a test asserting it only gets stricter as the class rises. The kernel routes *every* write through `May(tab, operation)`: URL/title rows persist for all non-ephemeral classes (Table A.5); checkpoints persist through SENSITIVE; thumbnails through AUTHENTICATED; content indexing and cloud AI only for PUBLIC by default; ephemeral persists nothing, not even the tab row.
- Thumbnails that the policy forbids are deleted immediately after capture, not merely unreferenced.
- **Permissions** (Table A.7): notifications, MIDI and sensors are denied by default; geolocation, camera, microphone and clipboard ask. Grants are per registrable site and kind with optional expiry ("allow for 1 hour"); WebView2's `PermissionRequested` is answered from the stored grant when one exists and prompts through a single window-owned dialog otherwise.
- **Native bridge** (Table A.11): the page can only post three fixed strings (`jev:dirty-form`, `jev:secret-field`, `jev:payment-field`). No host objects, no parameters, no values read from inputs. See `docs/threat-model/NATIVE_BRIDGE.md`.

Verified by `TrustOsIntegrationTests`: public page keeps checkpoint + thumbnail; sensitive keeps scroll but the thumbnail file is deleted; password field → nothing but the row survives; Private container → no rows, no checkpoints, no files, while the tab remains usable in-session.

Deferred: per-container proxy, encrypted sensitive metadata at rest, the full Public Suffix List, "Authenticated" heuristics from page content (currently URL-only, conservative).
