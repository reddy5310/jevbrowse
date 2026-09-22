# Security review request: the per-renderer shortcut/zoom token

**Status: awaiting independent review. Not yet audited by anyone other than its author.** Do not read this document, or its passing local checks, as
verification. It exists to make the mechanism easy for someone else to read and try to break.

## What it protects, and why it exists

WinUI 3's `WebView2` control keeps keyboard input inside the web content when a page has focus: the app's own `KeyboardAccelerator`s (Ctrl+L, Ctrl+T,
Ctrl+W, Ctrl+D, Ctrl+H, Ctrl+J, Ctrl+B, Ctrl+Tab, Ctrl+Shift+T/O, F1) never fire once a page has been clicked into, and the WinUI managed surface does
not expose `CoreWebView2Controller.AcceleratorKeyPressed`, the native, page-independent keyboard hook a host-authenticated fix would use. To make those
shortcuts work with the page focused, a small script registered via `AddScriptToExecuteOnDocumentCreatedAsync` listens for the real `keydown` event and
forwards a short code (`jev:key:closetab`, `jev:zoom-in`, …) to the host over `chrome.webview.postMessage`. The same path carries the persisted zoom
level.

**The first version of this had no protection at all beyond the page's own `keydown` listener's `e.isTrusted` check.** That check only constrains that
one listener; nothing stopped any script on the page — the page's own code, an ad, a compromised dependency, an iframe — from calling
`chrome.webview.postMessage('jev:key:closetab')` directly, at any time, with no key press. Consequences: closing the active tab (losing unfinished work),
adding or removing a bookmark, moving keyboard focus into the address bar, writing a zoom level to disk — none of it something a page should be able to
do on the person's behalf.

## The mechanism

1. When a renderer is created (`WebView2Lease.CreateAsync`), the host generates a random value: `Guid.NewGuid().ToString("N")` — 128 bits, rendered as
   32 hex characters, .NET's `RandomNumberGenerator`-backed GUID source. Call it the token.
2. The token is substituted into the host-authored script text (`PageScript.Replace("{{TOKEN}}", lease.ChordToken, ...)`) **before** that text is handed
   to `AddScriptToExecuteOnDocumentCreatedAsync`. The substitution happens in C#, on the host side; the page never sends or requests the token.
3. Inside the script, the token is stored in a `const CHORD_TOKEN` inside an IIFE closure — never assigned to `window`, `document`, or anywhere else a
   second script running in the same page could read it by name. A shortcut or zoom message is sent as `<code>|<token>`.
4. On the host side (`OnCoreMessage` for the top document, `OnFrameMessage` for every frame), `WebView2Lease.StripChord(msg)` splits on the *last* `|`
   and compares the trailing part to `lease.ChordToken` with an ordinary string equality check. A match returns the code with the token stripped; anything
   else — including a `jev:key:`/`jev:zoom-` message with no `|` or the wrong tail — is silently ignored (not logged, not surfaced, not acted on).
5. Everything **not** prefixed `jev:key:` or `jev:zoom-` is unaffected by this check: typing happened, a password/payment field exists, media is playing,
   and similar signals are still taken from content as reported, unchanged. They only ever narrow when a tab is put to sleep; they cannot act on the
   person's behalf, so this document does not claim they need the same boundary.

Relevant lines: `src/JevBrowse.App/Renderer/WebView2LeaseManager.cs` — `PageScript` (~L218), token generation (~L427), `OnCoreMessage`/`OnFrameMessage`
(~L494-L560), `StripChord`/`ChordToken` (~L970-L981).

## Assumptions this relies on

- **`AddScriptToExecuteOnDocumentCreatedAsync` really does run before any page script**, for every document, including the first one. This is
  documented WebView2 behaviour and is already relied on elsewhere in this file (password-field and payment-field detection use the same ordering
  guarantee) — but it has not been independently re-verified specifically for the token's threat model, and the `AddScriptToExecuteOnDocumentCreatedAsync`
  documentation's ordering guarantee is about *script registration order*, not a hard real-time guarantee against every possible page-construction trick.
- **A closure variable in one script cannot be read by a different `<script>` running in the same page**, as long as it is never attached to a shared,
  reachable object. This is ordinary JavaScript scoping and is not expected to be controversial, but the reviewer should confirm nothing elsewhere in
  `PageScript` (or another script this app injects) accidentally exposes `CHORD_TOKEN` — for example through a debugging hook, a global error handler that
  serializes local variables, or a future edit that adds one.
- **`GetWindow`/COM identity is not in scope here** — this mechanism does not rely on window handles or process identity at all; it is pure
  shared-secret-in-a-closure. (Contrast with `interaction-pass.ps1`'s *unrelated* foreground-window checks, which protect the test driver, not the app.)
- **The token is per-renderer, not per-message or per-navigation.** It is generated once when the `CoreWebView2` is created and reused for every
  navigation and every document within that renderer's lifetime (a tab waking from sleep gets a fresh renderer and a fresh token; an in-place navigation
  or reload does not). This was a deliberate simplification, not something ruled out and rejected — see "possible exposure/replay paths" below.

## Possible exposure/replay paths (candidates for a reviewer to try to make actually work)

1. **Reading the token via a side channel other than direct JS scope access.** E.g.: does WebView2 expose script source or closures to DevTools/CDP in
   a way a same-page script could reach programmatically? Does any diagnostic surface (a crash dump, a performance trace, a `--remote-debugging-port`
   session if one were ever enabled) leak it? Not tried.
2. **Replay within the same renderer's lifetime.** The token does not change per-message, so any script that legitimately observes a valid
   `<code>|<token>` string once (for example, by intercepting the outgoing `postMessage` call itself — see #3) could resend it later, including a
   different `<code>` than was originally sent, for as long as that renderer lives. Not tried, and no defence beyond the renderer's own lifetime exists.
3. **Intercepting `chrome.webview.postMessage` itself.** A page script that runs *after* `PageScript`'s closures are established (i.e., any ordinary
   page script, which is exactly the threat model) can still monkey-patch the global `chrome.webview.postMessage` function before `PageScript`'s own
   calls to it execute, if timing allows — `PageScript` calls the ORIGINAL `chrome.webview.postMessage` reference captured in its own closure
   (`const post = m => { try { chrome.webview.postMessage(m); } ... }` reads the CURRENT global property value at call time, not a captured reference at
   registration time), so a later monkey-patch on `window.chrome.webview.postMessage` **would** intercept `PageScript`'s own calls, including genuine,
   real-key-press-triggered ones, and could read the token off of them. **This was not tried and is the most likely real gap**: the negative check in
   the interaction pass (row 24) tests a page calling `postMessage` directly with no token, not a page that captures a real forwarded message to learn
   the token. A reviewer should try this specifically.
4. **The `AddScriptToExecuteOnDocumentCreatedAsync` ordering guarantee under a race.** Whether a sufficiently early inline `<script>` in the page's own
   HTML — before its `<head>`, or via some document-write trick — could execute before, or interleaved with, the injected script in a way that lets it
   patch `chrome.webview.postMessage` before `PageScript` reads it. Not tried.
5. **A stale token surviving a renderer that should have been retired.** Whether any code path reuses a `WebView2Lease` object (and therefore its
   `ChordToken`) across what should be two distinct trust boundaries — for example, a tab that changes identity container without a new renderer. Not
   specifically checked beyond the existing renderer-lifecycle tests; worth confirming a token is never carried across an identity-container change.

## What was and was not tested

- **Tested (real WebView2, `scripts/interaction-pass.ps1` row 24):** a page's own `<script>` and an embedded (same-origin, same local test server)
  frame's own `<script>` each call `chrome.webview.postMessage` directly — `'jev:key:closetab'`, `'jev:zoom-in'`, `'jev:key:bookmark'`, `'jev:key:addr'`
  — with **no token appended at all**. None of it closed the tab, changed a bookmark, changed zoom, or moved keyboard focus; a genuine Ctrl+W
  immediately afterward still closed the tab normally.
- **Not tested: the cross-origin case.** The interaction pass runs one local HTTP server, so its "embedded frame" is same-origin with the top page. A
  genuinely cross-origin hostile frame (the realistic case — an ad network, a widget from a different domain) was not exercised. The token mechanism as
  designed does not distinguish by origin — every frame gets the same per-renderer token via the same script-injection path, and the check is purely
  "did the message carry the right token," not "did it come from the top-level origin" — so there is no reason to expect a different *result*, but this
  has not been verified and should be, especially alongside path #3 above (a cross-origin frame monkey-patching a shared global is a more realistic
  attack shape than a same-origin one).
- **Not tested: paths #1, #2, #3, #4, #5 above.** None of them were attempted. #3 in particular should be tried before this boundary is called sound.

## What would make this "independently reviewed"

Someone other than the author of this fix reading `WebView2LeaseManager.cs` at the line ranges above, and at minimum attempting path #3 (capture a
genuine forwarded message via a monkey-patched `chrome.webview.postMessage`, then replay its token with a different, more damaging code) against a real
build. A clean pass on that attempt, plus a look at #1, #2, #4 and #5, is what this document is asking for — not a re-read of this write-up alone.
