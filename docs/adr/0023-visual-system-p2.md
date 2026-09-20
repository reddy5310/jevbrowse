# ADR 0023: The visual system (P2): tokens, themes, contrast, purposeful motion

Status: accepted (2026-09-20). Dark stays the default. Scope is what was built and what was *proven*; the open items are listed
plainly at the end, not folded into a success story.

## What "4D" means here, and what it does not

Depth, light, motion and continuity, each in a form that costs nothing while the window is still:

| Idea | What was built | Cost at rest |
|---|---|---|
| **Depth** | four surface levels (window < panel < raised control < hover/overlay) plus `ThemeShadow` elevation on the panels | none: static |
| **Light** | the surface you last worked in (sidebar, address bar or page) carries the accent edge; the others do not. A border colour set when focus moves | none: no animation |
| **Motion** | two brief fades, both explaining a change: the saved picture giving way to the live page, and the sidebar leaving or arriving | none: runs only during the ~120-200 ms of the transition, on the compositor |
| **Continuity** | the restore picture fades into the real page instead of snapping, so it is clear the picture was a stand-in | as above |

Not built, and not claimed: connected animations, parallax, ambient or looping effects. There is no infinite animation anywhere,
and a test fails the build if one is added.

## Tokens (`App.xaml`)

Nothing in the window names a raw colour any more. Semantic tokens for surfaces, text (exactly two: primary and secondary),
borders, focus, accent, tab state, data-class badges, environment strip and the preview scrim, in three dictionaries:
**Dark** (default), **Light**, **HighContrast** (Windows system colours only). Dimming text with `Opacity` is banned because it
produces a contrast nobody chose. Themes: dark, light, or match Windows (Ctrl+K → "Theme: …"), remembered, following Windows live.
Dialogs and web pages follow the app's theme too (the browser reports the app's scheme to pages), so the welcome page is light in
a light window.

## Contrast is computed from the file that defines it

`DesignTokenTests` parse `App.xaml` (and the welcome page's CSS) and compute WCAG 2.x ratios: primary text 7:1 and secondary 4.5:1
on every surface, control boundaries / focus / meaningful graphics 3:1, every badge and environment fill 4.5:1 with its text, in
both themes. They also fail the build if the window markup names a raw colour or dims a `TextBlock` with opacity.

Measuring the **existing** colours first found real defects, all fixed:
- data-class badge: white on the Public green was **2.15:1** and on Sensitive orange **2.33:1** (three of five classes failed);
- production/staging environment strip, a safety signal: **2.2:1** staging, **3.55:1** production;
- archived-tab dot **1.7:1**.
The tests also caught two near-misses in my own new values (dark boundary 2.83:1, welcome-page light teal 4.35:1) before they shipped.

## "No added continuous rendering at idle": measured against a rule fixed in advance

Rule, written before any P2 code: **average idle CPU ≤ 2.2% of one core and GPU ≤ 0.10% on all three "after" runs**, instrument
trust "ok". Method: `scripts/idle-cost.ps1`, only the launched process tree, window in front on the local welcome page, untouched.

| Set | CPU average (range) | GPU average | Trust |
|---|---|---|---|
| Before P2 (3 runs) | 1.12-1.67% (mean 1.32) | 0.01-0.05% | ok |
| After, first set | 1.15-1.32% (mean 1.25) | 0-0.01% | **2 of 3 UNRELIABLE** ("1 process reading failed") |
| After, rerun | 1.16-1.52% (mean **1.30**) | 0.01% | ok ×3 → **rule PASS** |

Honest notes:
- The first "after" set **failed the trust condition** on two runs, so under the rule as written it failed. The instrument was then
  changed to distinguish a *live process it could not read* (an error) from a *process that exited during sampling* (normal), and
  all three runs were repeated; both sets are kept in `docs/performance/`. In the rerun **zero** helpers exited and there were no
  read errors, so the guess that a helper process had exited was **neither confirmed nor refuted**; the cause of those two failed
  readings is unknown.
- The instrument was wrong twice before it was usable: an empty `catch` swallowed a PowerShell overload error and reported a flat
  0% for start-up, and a `data:` URL control was silently rejected. It is now validated: start-up reads 1.7% CPU, and a deliberately
  heavy animated page reads **GPU ~60%**, so the counters can see load.
- Measured: a still window. **Not** measured: cost during a transition, with heavy page content, or on other hardware. One
  machine, debug build.

## Acceptance criteria and where each stands

| Criterion | Status |
|---|---|
| Semantic tokens; dark default, light, match-Windows, remembered | **done**, seen on screen in both themes |
| Contrast (text, boundaries, focus, badges) | **done**, tested from the real files; three real defects fixed |
| High contrast | dictionary built and tested to use **only** Windows system colours; **not run on a real Windows contrast theme** (would change your desktop) |
| Scaling (display scale, text size) | **open**: not tested at 125/150/200% or with large text |
| Reduced motion | policy tested (animations off ⇒ zero duration; ceiling 250 ms); the Windows "Show animations" setting is read on each use; the welcome page honours `prefers-reduced-motion`. **Not exercised by actually toggling the Windows setting** |
| Reduced transparency | nothing relies on transparency: Mica sits behind an opaque root, and Windows removes Mica itself when transparency is off. No separate plumbing was added |
| No added continuous rendering at idle | **measured**, rule passed (above); no endless animation is allowed by a test |
| 700 px address bar clipped with the sidebar shown | **fixed and asserted.** Below 560 DIP of toolbar width the address bar takes a row of its own (its old column's 180 px minimum was what pushed the grid past the window), the badge shortens to its identity (full state stays in the accessible name and tooltip), and *This tab* / *Tools* drop to a further row instead of scrolling. `ui-a11y-check.ps1 -Sidebar open|hidden` fails on any interactive control that is clipped, squeezed or has no bounds. Run at 700/900/1422 x shown/hidden: no layout failure; 700 shown reached a full PASS (Tab cycle proven). Supported minimum window width is 700 px: at 420 the check fails, as it should |
| With the sidebar hidden, the palette and help buttons are not on screen | **open finding**: they live in the sidebar; Ctrl+K and F1 still reach them. Decide in P3 when the panels are rebuilt |
| Spacing / radius / elevation as tokens | **done**: radii (dot 5 / sm 8 / control 10 / card 12 / pill 14 / panel 16), a 2-4-6-8-10-12 spacing scale, named insets and four elevation depths live in `App.xaml`; markup and shell code use them; tests fail on a literal. A pixel diff against the pre-change build differed only in the live memory readout digits |
| Web pages follow the app's theme | implemented and seen for the welcome page in light; other sites only if they support `prefers-color-scheme` |

## Process failures worth keeping

- The app **stopped compiling** at the token step (`Grid` has no `Foreground`) and roughly 37 minutes of "verification" ran against
  the *old* binary, including screenshots where "light" was byte-identical to dark. The build check filtered for `: error`; the XAML
  compiler reports `XamlCompiler error WMC0011`. The unit tests stayed green throughout because they read the XAML file. Builds are
  now judged by exit code **and** a DLL newer than the source.
- Once building, the app **crashed at start-up**: subscribing to `AccessibilitySettings.HighContrastChanged` throws in an unpackaged
  app. The crash recorder named it. Theme changes now use `UISettings.ColorValuesChanged`, which Windows also raises for
  contrast-theme switches, and the theme plumbing can no longer stop the app launching.
