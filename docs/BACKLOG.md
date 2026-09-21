# Pending list, validated against the code (2026-09-21)

Checked by searching the source, not from memory. "Not implemented" means no code exists for it.

| Item | State | Notes |
|---|---|---|
| Preserve Back/Forward history when a tab sleeps and wakes | **Done as address-based replay (alpha.6)** | Saved with the tab; Back and Forward walk it by replacing the page (no engine API can recreate entries). `--history-check`, 13 steps. Not full session history: POST state, form state and per-entry scroll are not restored. A history containing any Sensitive address is kept in memory only |
| Protect unfinished work during automatic sleep | **Done in alpha.3** | Gaps found and fixed: password-only typing, typing inside an iframe, upload in flight, long silent video. Already covered: typing, downloads, sound, camera, microphone, screen share, calls. `--protection-check` |
| Download manager (progress, cancel/retry, Show in folder) | Not implemented | Only "a download is running" protection and the Private-session prompt exist |
| Bookmarks (import/export) | Not implemented | |
| Browsing-history screen | Not implemented | Browser Memory (searchable saved pages) and Time Travel (workspace contexts) are different things |
| Rename/delete ordinary workspaces | **Done (alpha.6)** | Deletion is terminal (Time Travel records removed; stale restore refused). Workspaces panel: Rename… and Delete… (Default and temporary sessions excluded), with a confirmation that states what is and is not deleted |
| Open links from other applications, default-browser registration | Not implemented | No URL-scheme registration or single-instance hand-off |
| Preferences: search engine, remembered memory mode, persistent per-site zoom | Not implemented | Search is fixed to DuckDuckGo (`AddressInput.SearchUrl`); no zoom code |
| Full-screen video | Not implemented; unverified | No full-screen handling exists. Behaviour with the engine defaults is untested |
| Signed packaging, secure update path | Open | Unsigned portable ZIP only |
| Display scaling, high contrast, screen reader | Open | Need your participation or approval |
| Broader compatibility, multi-machine performance | Open | One development laptop plus a hosted runner |

## What a first OFFICIAL release needs from this list (judgement, 2026-09-21)

Required in code, done: unfinished-work protection, Back/Forward across sleep, workspace rename/delete. Required and **not** code: signed packaging, an update path, display scaling,
high contrast and a screen-reader pass, and a clean-machine run. Reasonable to ship WITHOUT, disclosed: bookmarks, a history screen, a download manager (the engine's own save UI
and the Private-session prompt exist), search-engine and zoom preferences, full-screen video, default-browser registration (only needed to be offered as the default browser).
Bookmarks and a search-engine choice are the two most likely to be missed by ordinary users and are the next candidates after the official-release blockers.
