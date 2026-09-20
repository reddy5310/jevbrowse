# UI design

Goal: the window should read as one layered, luminous surface, and teach itself.

## Language
- **Dark by default** (`RequestedTheme=Dark`), deep navy surfaces (`#0B0D12` → `#121620`), one accent gradient teal → violet (`#20D6C6` → `#8B7BFF`) used sparingly: title line, primary buttons, the welcome title.
- **Depth, not decoration**: Mica backdrop behind everything; sidebar, address pill and page frame are cards with `ThemeShadow` and Z translation (24 / 12 / 16 px). State dots glow (`Translation` Z 8 + shadow). The welcome page uses CSS perspective, hover `translateZ`, and soft glow: that is the "4D" feel, restrained enough to stay fast.
- **The information that matters is always visible**: state dot per tab, container + data-class badge inside the address pill, live-renderer count and measured memory in the sidebar footer, PROD frame when DevSpace says so.
- **Every automated action is one click from its explanation** (Explain, Shield, Receipt, Brain log).

## First-time users
1. First launch opens **Welcome** (`jev://welcome`, generated locally, no network): six cards covering durable tabs and the dots, Ctrl+K, Shield's four layers, workspaces and Time Travel, privacy classes and containers, and AI being off with what Jev/OpenRouter actually do.
2. Four **TeachingTips** anchored to the real controls (tab list, product mode, Shield, Brain), shown once, "Next" to advance.
3. **F1** or the `?` button reopens Welcome at any time. `settings.json` holds `firstRunDone`.
4. Product modes let a new user start in **Simple** and grow into Power.

## Rules
- No feature is hidden behind an unlabeled icon; tooltips on everything.
- Status bar speaks in plain sentences ("Jev raised x to SENSITIVE (0.86)", "…showed an anti-adblock wall…").
- Motion is limited to hover depth and dialog transitions; nothing animates while idle (Constitution rule 6 / Zero-Bloat).
