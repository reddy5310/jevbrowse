# Native bridge surface

Threat (Table A.11): a malicious page invokes the native bridge.

## What exists

One script is injected into every document (`WebView2Lease.PageScript`). It may call `chrome.webview.postMessage` with exactly one of:

| Message | Meaning | Effect on the host |
|---|---|---|
| `jev:dirty-form` | user typed into a non-password input | sets the DirtyForm protection flag (vetoes auto-hibernation) |
| `jev:secret-field` | an `input[type=password]` exists | raises the tab's data class to SECRET (less persistence) |
| `jev:payment-field` | an `input[autocomplete^=cc-]` exists | raises the tab's data class to SECRET |

Site modules (ADR 0015) may additionally post, and the host only *counts*:

| Message | Meaning |
|---|---|
| `jev:yt-ad-pruned` | ad definitions were removed from a YouTube player response |
| `jev:yt-ad-skipped` | the player was in ad state and the skipper acted |
| `jev:yt-wall` | an anti-adblock wall element appeared |

The host (`CoreWebView2.WebMessageReceived`) compares the message to those literals and ignores anything else. It never parses JSON, never reads input values, and never exposes a host object (`AddHostObjectToScript` is not used anywhere).

## Abuse analysis

- A page can *only* make JevBrowse treat it as more protected or more sensitive. Both outcomes reduce what JevBrowse persists or automates; neither grants the page anything.
- A page that spams `jev:dirty-form` keeps itself live longer. Bound: the user's explicit Hibernate always works (user cause overrides protection), and the Resource OS reports the veto in "Explain". Follow-up: rate-limit and surface "this site keeps itself awake" in the resource receipt (Phase 11).
- Messages are accepted from any origin because they carry no authority. If a future message ever carries authority, it must be origin-bound and schema-validated before being added here.

## Invariants (enforced by review, and by tests where noted)

1. No `AddHostObjectToScript`. (grep gate in CI, Phase 11)
2. Messages are fixed strings, matched by equality.
3. The injected script never reads `.value` of any input. (`TrustOsIntegrationTests` covers the class transition; script review covers the read.)
