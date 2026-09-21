# Pending list, validated against the code (2026-09-21)

Checked by searching the source, not from memory. "Not implemented" means no code exists for it.

| Item | State | Notes |
|---|---|---|
| Preserve Back/Forward history when a tab sleeps and wakes | **Not implemented; disclosed** | A sleeping tab keeps its address and scroll position only. WebView2 offers no way to recreate history entries in a new renderer, so this needs a design (record entries and replay them, or keep the renderer). Disclosed in the alpha notes |
| Protect unfinished work during automatic sleep | **Done in alpha.3** | Gaps found and fixed: password-only typing, typing inside an iframe, upload in flight, long silent video. Already covered: typing, downloads, sound, camera, microphone, screen share, calls. `--protection-check` |
| Download manager (progress, cancel/retry, Show in folder) | Not implemented | Only "a download is running" protection and the Private-session prompt exist |
| Bookmarks (import/export) | Not implemented | |
| Browsing-history screen | Not implemented | Browser Memory (searchable saved pages) and Time Travel (workspace contexts) are different things |
| Rename/delete ordinary workspaces | Not implemented | Workspaces can be created, ended (Private) and moved between; no rename or delete of ordinary ones |
| Open links from other applications, default-browser registration | Not implemented | No URL-scheme registration or single-instance hand-off |
| Preferences: search engine, remembered memory mode, persistent per-site zoom | Not implemented | Search is fixed to DuckDuckGo (`AddressInput.SearchUrl`); no zoom code |
| Full-screen video | Not implemented; unverified | No full-screen handling exists. Behaviour with the engine defaults is untested |
| Signed packaging, secure update path | Open | Unsigned portable ZIP only |
| Display scaling, high contrast, screen reader | Open | Need your participation or approval |
| Broader compatibility, multi-machine performance | Open | One development laptop plus a hosted runner |
