# ADR 0022: Narrow windows, real screens, and what a screen reader hears

Status: accepted (2026-09-20). Closes the "not verified" list in ADR 0020.

ADR 0020 shipped the one-row toolbar and listed what it had not seen: narrow windows, dialog and menu layouts, keyboard
behaviour, screen-reader names. `--ui-shot` cannot capture popups or WebView2 content, so this pass drives the **real running
window** through Windows UI Automation (the tree Narrator reads) and takes true screen captures. It found problems the static
screenshots could not.

## What the real screens and the accessibility tree showed

| Found | Fixed |
|---|---|
| The **Tools menu spilled past the window's right edge** (a flyout aligned to the left of a button at the far right) | both menus align to the button's right edge |
| The Shield dialog's main button was truncated ("Turn off for example.c…") | "Turn off and reload"; the site is already named in the body |
| At 700 px the address bar was clipped and **Tools was pushed off-screen** | controls wrap to a second row below 760 px, sit in a horizontal scroller as a last resort, and the sidebar can be hidden |
| **The class badge could not be reached by keyboard or screen reader at all**: a `TextBlock` with a `Tapped` handler, and it is the control for changing how a site is treated | it is a real `Button`, in the tab order, announcing "Personal profile, Not assessed. Press to change how this site is treated." |
| Three icon buttons announced as **raw glyph characters** (``, ``, ``); three dropdowns and the tab list had **no name** | named: Command palette, Help and welcome, Time Travel, Product mode, Memory budget, Workspace, Open tabs, New workspace |
| The **Receipt** was a monospace table that wrapped into misaligned columns and spoke in implementation terms; its Memory line was clipped | label-over-value rows in plain words ("What example.com did during your visit"); "Requests 0" became "Checked by Shield: 0 requests" because it counts only what Shield inspected |
| **Explain** printed the scheduler's raw record ("Current band: Yellow, live budget 6, live now 1") | sentences: "This is the tab you are looking at, so it stays awake." / "It was put to sleep because it had not been used for 12 minutes." / why a tab was spared / memory in words |
| Explain's default button changed what the tab does on Enter | the harmless button is the default |
| Workspace box truncated ("Default · Personal (") | the default identity is not repeated; it is on the address badge |

The wording of the Receipt, Explain, the stay-awake reasons and the status line are pure functions with tests that ban the
scheduler's vocabulary, so it cannot drift back onto the screen.

## Collapsible sidebar

A toolbar button and **Ctrl+B** hide the 276 px sidebar; the choice is remembered (`ui-prefs.json`). The button's accessible
name says what the next press does ("Hide sidebar" / "Show sidebar"). It is a toggle, not a resizable splitter: dragging to a
custom width is not built.

## A repeatable gate: `scripts/ui-a11y-check.ps1`

Launches the real app and **fails** (exit 1) if any interactive control has no accessible name, is named after a raw glyph,
is not reached by forward Tab in a full cycle, or a required toolbar control is missing. Latest run: 24 interactive controls,
0 failures, tab cycle complete (Shield → Explain → Receipt → This tab → Tools → page → sidebar → toggle → Back → Forward →
Reload → badge → address bar).

Limits, stated plainly:
- It needs an interactive desktop and sends real keystrokes. Windows can hand the foreground to another program; the gate
  proves the app is in front before every key and reports **INCONCLUSIVE**, not pass, if it is not.
- Reverse (Shift+Tab) order was observed for six presses and matched the forward order, then focus left the app. The same
  happened when the window was demonstrably not in front, so it is **not attributed to the browser and not claimed as verified**.
- One run printed no report at all, right after a rebuild in the same command; three later runs were identical. Cause not
  established (suspected race with the build finishing).
- No contrast, zoom, high-contrast or reduced-motion testing; no real screen reader was run. Names being present and correct
  in the tree is necessary, not sufficient, for good Narrator behaviour.
- With the sidebar shown at 700 px the address bar is still clipped and the control group scrolls sideways. Nothing is
  unreachable, but it is not pleasant; hiding the sidebar is the intended remedy there.
