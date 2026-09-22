# Security review request: the per-renderer shortcut/zoom token

**Status: awaiting independent review. Not yet audited by anyone other than its author.** One gap this document originally flagged as untried — path #3,
a page intercepting `chrome.webview.postMessage` to steal a token off a genuine key press — was found to be real and has been fixed; see path #3 below.
**Passing that regression establishes that ONE specific attack (path #3) is blocked. It does not establish that the token boundary as a whole is secure.**
Paths #1, #2, #4 and #5 remain untried, and a fix this document's own author wrote and verified is exactly the kind of thing that needs a second reader
before anyone relies on it. Do not read a passing local check as verification of the mechanism as a whole. It exists to make the mechanism easy for
someone else to read and try to break.

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
3. **Intercepting `chrome.webview.postMessage` itself — CONFIRMED REAL, NOW FIXED.** A page script that runs *after* `PageScript`'s closures were
   established could monkey-patch the global `chrome.webview.postMessage` before `PageScript`'s own calls to it, because `post`/`postChord` used to look
   up `chrome.webview.postMessage` fresh on every call rather than holding a captured reference. Confirmed by direct testing (not by reasoning alone):
   with the old code, a page and a genuinely cross-origin frame (a second port) each captured the real, valid token off a real Ctrl+ zoom press —
   `jev:zoom-in|<32-hex-chars>` — reproducibly, in both the top document and the frame. **Fix:** `PageScript` now captures
   `chrome.webview.postMessage.bind(chrome.webview)` into `const nativePostMessage` as the very first statement in its IIFE, before anything else runs in
   that document (this ordering is exactly what `AddScriptToExecuteOnDocumentCreatedAsync` guarantees), and `post`/`postChord` call that captured
   reference exclusively from then on. A later monkey-patch on the global no longer has any effect on this app's own outgoing calls. Re-tested with the
   fix applied: the same real Ctrl+ press, captured by the same interceptor, in the same cross-origin frame, yields nothing — only the interceptor's own
   blind, invalid-token replay guesses appear in its log. See `scripts/interaction-pass.ps1` row 25 and `docs/releases/0.1.0-alpha.7.md`.
   **This one path is no longer open, but it was real, and a reviewer should not take that on trust — re-derive it from the code at the line ranges
   above, not from this paragraph.**
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
- **Tested (real WebView2, row 25): path #3, INCLUDING the cross-origin case.** A page and a genuinely cross-origin embedded frame (a different port —
  a different origin under Chromium's same-origin policy, not the same local server as row 24's) each install a `chrome.webview.postMessage` interceptor
  before a real Ctrl+ zoom press occurs in that document, then replay whatever they captured (or a blind guess, if nothing was captured) after a delay.
  Mutation-checked in both directions: the old code leaks a real, valid token from both the top document and the cross-origin frame; the fixed code leaks
  nothing from either, and the replay attempts (with no valid token) have no effect. This also answers the cross-origin question this section used to
  leave open for path #3: the mechanism's origin-independence holds in practice here, not just by design.
- **Still not tested: paths #1, #2, #4, #5 above**, and the cross-origin case specifically for row 24's *direct, untokened* message (row 24 itself remains
  same-origin; only the interception case in row 25 was extended to a real cross-origin frame). None of #1/#2/#4/#5 were attempted.

## What would make this "independently reviewed"

Path #3 no longer needs independent confirmation of the *vulnerability* — it was reproduced directly, not just reasoned about — but the *fix* still does:
someone other than its author reading `WebView2LeaseManager.cs` at the line ranges above and confirming the capture genuinely happens first, in every
code path that creates a renderer, not only the one this pass happened to exercise. Beyond that, at minimum attempting paths #1, #2, #4 and #5 against a real
build. A clean pass on that attempt, plus a look at #1, #2, #4 and #5, is what this document is asking for — not a re-read of this write-up alone.
