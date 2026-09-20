# ADR 0020: One toolbar, and the interface speaks in words a person would use

Status: accepted (2026-09-20). Implements P1 of the second independent review.

## Toolbar

There were two toolbar rows: navigation, then twelve text buttons (Put to sleep, Pin, Keep active, Move…, Explain, Shield,
Receipt, Ask, Brain, Memory, Dev, Agents). Most of them were tab management or advanced tools competing with navigation
for the same attention, and **Reload did not have a button at all**.

Now one row: Back, Forward, **Reload**, the address bar (it gets the width), then **Shield**, **Explain**, **Receipt**,
**This tab ▾** and **Tools ▾**.

- *This tab* holds Put to sleep, Pin to top, Keep active and Move to workspace. The captions say what the next click does
  ("Unpin", "Let it sleep").
- *Tools* holds the mode-gated extras (Summarize this page, Search pages you have read, AI settings and log, Developer
  tools, Agents) and is hidden in Simple and Private, where it would open onto nothing.
- **Explain and Receipt are visible in every mode.** They were hidden in Simple, which is the default, so "why did this tab
  sleep" and "what did this site do" were unreachable for the people most likely to want them. Trust is not an advanced feature.
- Shield leads with **"Site not working?"**: the dialog opens on the fix (turn Shield off for this site and reload) in one
  sentence and one button. The rules table is under a collapsed "Technical details".

## Plain language, in the places a person actually reads

| Was | Now |
|---|---|
| status line echoing `activated live=2`, `decision virtualized`, `memory: skip: not assessed: content is not indexed without positive evidence…` | says nothing for bookkeeping; "A tab went to sleep to save memory." only when the scheduler did it; "Woke a sleeping tab in 806 ms." only for a real wake-up (a first load is not one); "Saved this page so you can search it later." |
| sidebar `1 tabs • 1/8 live • 5 procs • 199 MB private (measured)` | `1 tab open, 1 awake (up to 8)` / `Pages are using 200 MB of memory` / `Shield has blocked 0 requests this session` ("private" meant memory, and Private is now a feature) |
| workspace box `Default · Personal (0)` with one tab open | counts follow the tab set as tabs open, close and move |
| `Hibernate all` | `Sleep others`, with a tooltip saying what it does |
| permission prompt `<site> wants Other` | `<site> wants to <see your location / use your camera / read what you have copied / …>`; unknown engine permissions degrade to "use a browser feature" rather than "Other" |
| prompt's close button (and Esc) **blocked the site permanently** | Esc and "Don't allow" refuse this request only; a permanent block is an explicit "Don't ask this site again" tick box |

The status-line rule is a pure function (`KernelStatus`) so a test can guarantee raw event names cannot reach the screen again.

## Review fixes carried in

- The private session's tab count refreshes when tabs open, close or move, not only when the workspace changes.
- An earlier session's unfinished cleanup stays visible when a new session exists; ending the new one retries the old one, and the text says so.

## Evidence and limits

- 265 unit tests. New: status-line rules (no jargon, first load is not a wake-up), private-session text (stale cleanup).
- `--privacy-check`, `--agent-check`, `--private-session-check` pass on real renderers after the change.
- Screenshots (`--ui-shot`) reviewed in Simple and Power modes at 1422 px wide: navigation dominates and all controls fit.
- **Not verified:** narrow windows. The address bar has a 180 px minimum, but below roughly 1000 px the right-hand controls will
  crowd or clip; an overflow behaviour is still to do. The screenshot method cannot capture WebView2 content, the confirmation
  and permission dialogs, or menu flyouts, so those layouts were not seen. No screen-reader pass has been done on the new menus.
