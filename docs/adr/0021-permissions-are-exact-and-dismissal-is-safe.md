# ADR 0021: A permission grant covers exactly one permission, and dismissing a prompt never decides anything permanent

Status: accepted (2026-09-20). Two defects found in review of ADR 0020's prompt changes; the first predates it.

## 1. Different permissions shared one remembered decision

Grants are stored per `(site, kind)`. The engine's permission names were mapped to a small enum, and everything not listed
(automatic downloads, file access, local fonts, autoplay, window management, MIDI system-exclusive, and any permission a newer
SDK adds) became `PermissionKind.Other`. So "Allow automatic downloads for an hour" stored a grant under `Other`, and the next
request from the same site for *file read/write* found it and was allowed **without asking**. Clearer prompt wording (ADR 0020)
did not change what was stored.

- Each permission we can name has its own kind. New members are appended to the enum, so existing stored integers keep their
  meaning, and `Other` stays 7.
- **`Other` is never remembered, in either direction.** A permission we cannot name is asked every time; a stored row under
  `Other` from before the split is ignored, not honoured. One remembered answer for "a thing we cannot name" would cover every
  unrelated thing.
- MIDI system-exclusive is denied by default alongside plain MIDI and sensors; the other new kinds are asked.
- The engine-name mapping matches by name, so an SDK that adds or renames a member degrades to `Other` instead of failing to build.

## 2. Escape could permanently block

The dialog had a "Don't ask this site again" tick box on its close button. `ContentDialog` returns the **same result** for the
close button and for Escape, so the two cannot be told apart; ticking the box and pressing Escape recorded a permanent block.

The dialog now has three explicit outcomes and no box:

| Button | Meaning |
|---|---|
| **Allow** (with "Just this time" / "For 1 hour", default the first) | grant, for that long |
| **Block this site** | refuse, and remember |
| **Not now** (also Escape, and the default button so Enter cannot grant) | refuse this request only, remember nothing |

The mapping from button to decision is a pure function (`PermissionChoices.FromDialog`) so "dismissal is always the safe one"
is a test rather than a convention.

## Evidence and limits

- 9 new unit tests: every nameable permission maps to a distinct kind; an unnamed one is `Other`; a grant for one kind does not
  answer another; `Other` is asked even when a stored row says yes; dismissal is `BlockOnce` regardless of other choices; only
  explicit buttons produce a permanent or timed decision.
- Three new checks in `--private-session-check` run the real `PermissionAdapter` against a real database: a grant is remembered
  for its own kind, a different permission from the same site is asked separately, an unnamed one is asked every time.
- Old `Other` rows are left in the database, ignored. They are harmless and not purged.
- Not verified on screen: how the new dialog looks, and Escape/Enter behaviour in the running window. `--ui-shot` cannot capture
  popups, so the button semantics are covered by the pure mapping and by the WinUI default-button setting, not observed.
