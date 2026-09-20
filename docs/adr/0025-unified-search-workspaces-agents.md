# ADR 0025: Unified search, workspace previews and agent activity (P3, part 2)

Status: accepted. Builds on the panel host of ADR 0024.

## Unified search

Ctrl+K (and More > Command palette) opens a **Search panel** in place of the palette dialog: one box over open tabs, workspaces,
commands and pages you have read. `UnifiedSearch` (pure, in `JevBrowse.VirtualTabs`) ranks: every word must match; a title that
starts with the word beats a word inside the title, then a substring, then the detail. Each group is capped so thirty matching
commands cannot push the one matching tab off the list. Browser Memory hits arrive already matched by its own full-text index, so
they are ordered by that and never dropped by this ranking.

- **Privacy:** tabs and workspaces of a private or disposable session are left out unless that session is the active one. Browser
  Memory only holds pages Trust OS allowed to be indexed and is searched for the active workspace only. The panel says
  "Searched on this device only" because it is.
- **Behaviour:** focus lands in the box; Up/Down move; Enter runs the top result and closes the panel without pulling focus back
  to the toolbar (a tab activating puts focus in the page); Esc closes and returns focus to where it was.

## Workspace session previews

More > Workspaces overview (and the search command) shows, per workspace: name, identity, tabs and how many are awake, the tabs
themselves (title, host, state in words), and up to three saved images. `WorkspacePreviews` holds the rules: a private session
is left out unless it is the one you are in, and **never carries a saved image**; long lists are cut and the cut is counted
("and 3 more"). Images are labelled as saved images from earlier, never the live page. A tab row goes straight to that tab
(switching workspace first); "Switch to" goes to the workspace.

## Agent activity

More > Agent activity shows, per session: agent, status (running with time left / stopping / stopped / expired), actions used of
allowed, pages open, what it may do and on which sites, and the last eight actions **including refusals with the reason in words**
(`domain_not_allowed:x` becomes "x is not on the approved list"; an unknown code is shown as it is). **Stopped** is only said once
the pages are actually released. Stop and Stop-all take one click and never ask first: stopping is the safe direction. Setting up
access (what agents may ever do) remains the Agent Gateway **dialog**, because it is a decision. The panel updates its words in
place every two seconds instead of rebuilding, so it never throws away the focus of someone tabbing through it.
A unit test caught the reason codes that start `hard:` (rules no setting can relax) being split on their colon; fixed.

## Found while building this (recorded, not smoothed over)

- The accessibility gate could have sent keys (including Ctrl+A then Backspace) to another program if focus moved mid-run. Every
  key now goes through one function that checks the app is in front at that moment; focus seen outside the app marks the run
  **inconclusive** and voids the failures recorded after it, so it can never be reported as an app fault. One run earlier had
  already produced false failures when focus briefly went to the editor's terminal.
- Focus did not return to More after opening a panel from a menu item (the item is gone once the menu closes). Found by the gate,
  fixed with a home-control fallback.

## Not done

- Ask / Summarize, Memory search (with Clear index), Agent Gateway setup, DevSpace, JevBrain settings and the workspace dialogs
  are still dialogs. Some are decisions and should stay so; the rest are converted case by case, not blanket.
- A live agent (Claude Code) has not been driven through the panel; the demo used the same local endpoint and gateway with a scripted session.
- An agent's page becomes the active tab in the window when it navigates (seen in the demo screenshot). That is existing behaviour;
  whether an agent should be able to take over the visible tab is a question for the next design pass.
- **Scaling (125/150/200%) and text size are still untested**, and reduced-transparency and a real high-contrast theme run were not done.
  The UI is **not** declared release-ready.
