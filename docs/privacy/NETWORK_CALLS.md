# First-party network calls

Constitution rule 4: every network call JevBrowse itself makes (as opposed to the pages the user visits) is listed here.
Anything not on this list is a bug.

| Purpose | Endpoint | When | Data sent | Disable |
|---|---|---|---|---|
| Shield filter lists | `https://easylist.to/easylist/easylist.txt`, `https://easylist.to/easylist/easyprivacy.txt` | First launch when no lists are on disk; when the user presses **Update lists** | Nothing beyond a `User-Agent: JevBrowse/0.1 (+filter-list-update)` header | Set env `JEVBROWSE_NO_FILTER_UPDATE=1` (Shield then runs with whatever is on disk, possibly nothing) |

| OpenRouter (optional AI) | `https://openrouter.ai/api/v1/chat/completions` | Only when the user presses an AI action (e.g. **Ask**) and both AI switches are on | Redacted page text (see `docs/AI_POLICY.md`), model name, `HTTP-Referer`/`X-Title` identifying JevBrowse | Leave `OPENROUTER_API_KEY` unset, or turn AI/Cloud off in the Brain panel |
| Jev (optional AI) | `$JEV_API_BASE/chat/completions` (endpoint shape pending confirmation) | Same as above, when Jev is the preferred provider for the task | Redacted text, model name | Leave `JEV_API_KEY`/`JEV_API_BASE` unset |

## Inbound (local only)

| Purpose | Endpoint | When | Disable |
|---|---|---|---|
| Agent Gateway local host | `http://127.0.0.1:<random port>/` with a per-run bearer token | Only after the user enables it in the Agents panel | Toggle it off; it never starts on its own |

Not yet implemented, will be listed when they are: WebView2/Windows App SDK updates (handled by Windows, not by JevBrowse).

Telemetry: none. Crash reports: none are sent anywhere.
