# ADR 0018 — Unknown is not public, and it does not leave the device

Status: accepted (2026-09-20)
Supersedes the classification half of [ADR 0017](0017-p0-trustworthy-boundaries.md).

ADR 0017 added `DataClass.Unknown` and claimed "Not assessed … never sent anywhere". A third review checked that
claim against the code and found it was not true. It was right on all three counts. This ADR records what was wrong
and what replaced it.

## 1. Unknown could be sent to cloud AI

Both routing paths extended the explicit-user-action exception with an **ordinal** test:

```csharp
bool explicitException = !automatic && cls <= DataClass.Authenticated;   // BrainRouter.cs:79 and :138
```

Trust OS itself denied `(Unknown, SendToCloudAI)` correctly. The exception bypassed it. Inserting `Unknown = 1` into
the enum silently widened what may leave the device — the exact failure mode an ordinal comparison invites. The Ask
dialog enabled **Send** on the same basis (`cls < DataClass.Sensitive`).

It required AI on, cloud on and a deliberate click; nothing uploaded by itself. It still contradicted a promise the
UI makes in those words.

**Replaced with an enumerated set**, `DataClassExtensions.MayLeaveDeviceOnExplicitRequest()`: `Public` or
`Authenticated`, nothing else. An enum edit cannot widen it again. A page we could not assess is not a page the user
can knowingly consent to send, so it is not on the list. The Ask dialog now explains that and points at the class
badge.

## 2. "Positive public evidence" was not evidence of anything

The page script promoted to PUBLIC on: no password field, no payment field, no recognised logout link, and >200
characters of body text. An authenticated document or single-page app satisfies all four. Content length is not
publication. This rule put most of the web back where `Unknown` was introduced to stop it going.

**No page-side signal promotes a class any more.** PUBLIC has exactly two routes:

1. a structurally public host (`KnownPublicSuffixes`: Wikipedia, MDN, Microsoft Learn, RFC Editor, …), or
2. the user's own per-site choice, written through the class badge into `site_settings.data_class`.

The signal itself survives as `PageSignals.ContentRendered`, renamed to say what it actually observes. It only tells
the shell the assessment is settled rather than still loading, so a page mid-render is not labelled prematurely.

**Consequence, stated plainly:** most sites now sit at *Not assessed*. Browser Memory indexes little by default and
Ask refuses until you classify the site. That is the direction we want to be wrong in, and the class badge is a
two-click fix per site.

## 3. "Known preserved" was too strong

A scroll-extraction failure became `Partial`, and automatic demotion accepted `Partial` — but a missing preview
image and a missing scroll position are different losses, and only one of them changes what the user sees.

`CaptureResult` now carries `PreservedParts` (`Address` / `Position` / `Preview`) and derives `Shortfall` from the
parts, not from the outcome:

- position lost → "Page reopened; previous position unavailable."
- preview lost only → nothing said; the page came back intact and the panel simply had no picture.
- a class that forbids thumbnails → not a shortfall at all; policy said not to keep pixels.

The restore panel's "your place is saved" is now derived from `PreservedParts.Position` rather than from a checkpoint
row existing. Restore messages are cleared when the user moves to another tab, so a statement about one page cannot
be read as a statement about the next (`ClearStaleRestoreMessage`).

## Evidence
234 unit tests. New: `A_page_we_could_not_assess_is_never_sent_even_on_an_explicit_request` (both routers, providers
configured, asserts neither provider was called), `Public_must_be_earned_and_is_never_assumed` (rewritten: signals do
not promote; host and user choice do), `Losing_only_the_preview_is_not_reported_as_a_failed_restore`.
`--privacy-check` re-run on real renderers.
