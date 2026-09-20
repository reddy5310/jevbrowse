# ADR 0026: Larger Windows text size (P3 follow-up)

Status: accepted. Display scaling is reported separately (below) and is **not** covered by this ADR's evidence.

## What was tested

Windows **Text size** (Settings > Accessibility > Text size, registry `TextScaleFactor`) at 125%, 150% and 225% (the maximum), by
`scripts/text-size-check.ps1`. Only that one value is changed. The original (here: not set, the 100% default) is written to
`textsize-original.json` before anything changes, restored in a `finally` block and read back; `-Restore` repairs a run that was
killed. The app reports the scale it sees, so a setting that did not reach it is inconclusive rather than "fine at that size".
At each size, at 1422 and 700 px wide, sidebar shown and hidden: the accessibility/layout gate (clipped, squeezed, missing or
unreachable controls; every panel by keyboard; search; More menu), the **permission** and **New workspace** decision dialogs (every
button present, on screen and unclipped), and window captures that were looked at.

## What it found, and what changed

The first run failed at every size. Layout used fixed width thresholds (760 and 560 px) chosen at 100% text.

- **Toolbar:** at larger text the trust buttons (Shield, Explain, Receipt, This tab, Tools, More) were pushed out of the window and
  the address badge was clipped. Now the toolbar decides from the buttons' **measured** widths, never a constant: the trust buttons
  sit beside the address bar only if everything fits, otherwise they wrap onto rows of their own in a new `WrapPanel` (no scroll bar
  hiding controls, nothing squeezed); if even the navigation buttons plus a readable address do not fit, the address bar gets a
  full row and its badge shortens to the profile name (full state stays in the accessible name and tooltip).
- **Sidebar top row** (New, Sleep others, filter, help) wraps the same way; it used to squeeze the help button to 7 px at 125%.
- **Sidebar tab list vanished at 225%:** the header and the memory readout took all the height. The readout is now held to two lines
  at large sizes (full text in its tooltip), and the gate now requires the tab list to be on screen with the sidebar open, so it
  cannot disappear silently again.
- **Icon-only buttons:** first frozen at 100% size (they looked tiny beside big text; small click and touch targets), then made to
  follow Text size up to 150%. Words scale fully.
- The toolbar re-lays itself out when Text size changes while the app is open.
- Gate: a control mostly scrolled out of a panel's scroll area (a sliver) is not a layout fault; it is reached by scrolling.
- Harness bug found on the way: the script leaked its start-page setting into the gate, which then looked for a tab that was not open.

## Result

**0 failures at 125%, 150% and 225%**, at 1422 and 700 px, sidebar shown and hidden, with every decision dialog passing. Three runs
(one full, two partial) were needed because some cells were first inconclusive while another window held the foreground; each cell
has at least one clean PASS, none has a failure. Two harness mistakes are recorded: the script leaked a start-page setting into the
gate (false failures), and a comma list passed through `-File` arrived as 125150 (out of range; restored and confirmed). Text size
values are now range-checked before the registry is touched, and a run that tested nothing can no longer report PASS.

## Not done / still open

- **Display scaling (125/150/200%) is not tested.** The plan is for the owner to change it and report the value; nothing here
  covers it. Text size and display scaling are different: display scaling changes DPI, text size does not.
- At 225% the workspace name and tab titles in the fixed-width sidebar are cut with an ellipsis (the accessible name has them in
  full). Legible, not clipped controls, but not ideal.
- A gate run is inconclusive by design, for its keyboard steps, while another window holds the foreground.
- High contrast (a real contrast theme, not just tokens) and reduced transparency: not yet run.
- Agent-driven tab switching (an agent's page becomes the visible tab) and the unexplained transient gate failures noted in ADR 0024
  remain **unresolved and tracked**.
