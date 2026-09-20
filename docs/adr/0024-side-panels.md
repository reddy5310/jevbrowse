# ADR 0024: Side panels for reading, dialogs for deciding (P3, part 1)

Status: accepted. Applies to Shield, Explain and Receipt; further panels follow the same host.

## Decision

Information a person reads and dismisses is shown in one **side panel** beside the page instead of a dialog that stops the whole
window. Decisions stay **dialogs on purpose**: permission prompts (Allow / Block this site / Not now), "Clear Browser Memory?",
agent-session grants, and other confirmations. A panel can be ignored and left open; a decision must not be answerable by accident
or by walking away, so those keep their blocking, default-safe shape. A unit test pins that split.

## Shape

- `SidePanel` in `MainWindow.xaml`, one instance, opened through `OpenPanel(id, build, home)` in `MainWindow.Panels.cs`.
- **Wide:** docked to the right of the page (360 px); the page shrinks, it is never covered.
- **Narrow** (the area is under 820 DIP): the panel takes the page's place. It is never drawn over the page, because a web view
  is drawn above other controls. While it is open below that width the page is hidden, not closed.
- **Not modal.** Tab moves through it like anything else; the page and toolbar stay usable.
- Opening moves focus to the panel's close button (the reading order starts at the title, and Esc works at once).
  **Esc** or the close button closes it; pressing the same toolbar button again toggles it. Focus returns to the control the person
  opened it from (or that panel's own button), and only if focus was still inside the panel; a person who has clicked into the
  page is not pulled back.
- Content about "this page" is rebuilt when the active tab changes or navigates, so it never describes the previous page.
- **Shield, Explain, Receipt stay directly reachable** as toolbar buttons. Their actions (turn Shield off and reload, update block
  lists, keep this tab active / let it sleep) are explicit buttons inside the panel, named for what they do, none of them default.
- **Palette and Help** are in the toolbar's **More** menu (Ctrl+K and F1 still work), so they are on screen with the sidebar hidden.
  This resolved the open finding from ADR 0023.

## Evidence

`scripts/ui-a11y-check.ps1` now sends real keys for each panel: Enter opens it, focus lands in it, Tab keeps moving (no trap),
Esc closes it and focus is back on the button that opened it, and the close button behaves the same; the clipped/squeezed layout
check is repeated with each panel open; and the More menu is opened and both items are found. Passed at 700 and 1422 wide with the
sidebar shown and hidden (9 of 9 repeat runs; see the note on the first matrix below). Negative control: with focus restoration
deliberately removed the gate failed with "after Escape focus is on '+ New', not on the Receipt button that opened it".

## Review of the pilot (before extending it)

- Fine: no overlap of the web view; no address-bar clipping at 700 with the sidebar shown or hidden; focus rules hold.
- **Known limit:** below 820 DIP the page is hidden while a panel is open. Audio or video keeps playing; nothing is suspended.
- **Known limit:** Esc only closes the panel while focus is inside it. Esc inside a web page belongs to the page (fullscreen,
  dialogs), so it is deliberately not a global shortcut.
- **Known limit:** panel content is a snapshot taken when opened, refreshed on tab change and after its own actions, not live.
- The first four-run matrix after adding the all-panels check showed two runs that exited without a report and one "the app
  produced no window"; nine runs straight afterwards were clean. The cause was not found. It is recorded, not explained.
- **Not yet done:** Ask/Summarize, Memory search, Agent Gateway, DevSpace, JevBrain settings and the workspace dialogs are still
  dialogs; they are converted case by case (some are decisions), not blanket.
- **Scaling and text size are still untested**, so the UI is not declared release-ready.
