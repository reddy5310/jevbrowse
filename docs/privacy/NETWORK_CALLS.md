# First-party network calls

Constitution rule 4: every network call JevBrowse itself makes (as opposed to the pages the user visits) is listed here.
Anything not on this list is a bug.

| Purpose | Endpoint | When | Data sent | Disable |
|---|---|---|---|---|
| Shield filter lists | `https://easylist.to/easylist/easylist.txt`, `https://easylist.to/easylist/easyprivacy.txt` | First launch when no lists are on disk; when the user presses **Update lists** | Nothing beyond a `User-Agent: JevBrowse/0.1 (+filter-list-update)` header | Set env `JEVBROWSE_NO_FILTER_UPDATE=1` (Shield then runs with whatever is on disk, possibly nothing) |

Not yet implemented, will be listed when they are: WebView2/Windows App SDK updates (handled by Windows, not by JevBrowse), optional AI providers (Phase 7; explicit user action only, per data class).

Telemetry: none. Crash reports: none are sent anywhere.
