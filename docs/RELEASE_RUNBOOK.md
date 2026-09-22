# Release runbook

How a JevBrowse alpha gets built, tested and — only with explicit approval — published. Written after GitHub Actions was blocked mid-alpha.7 by the
repository owner's account billing (a spending limit / failed payment, not a JevBrowse problem), so the runbook treats **local verification as the
primary evidence** and CI as a second, independent check that is used when it is available and clearly labelled when it is not.

## Distribution decision (owner, 2026-09-22)

The first public release will be a **public GitHub repository with a GitHub Release** (not a private repo with the ZIP hosted elsewhere): the source and
the ZIP both become visible together, matching the MPL-2.0 license already in the repo. This happens **only after both outstanding gates below pass**;
the owner explicitly chose to wait rather than publish now and catch up on them afterward. Until then the repo stays private and nothing is published.

## Roles

- **Claude** builds, tests locally, writes the evidence, and prepares (but never runs unattended) the publish step.
- **The repository owner** decides when to spend GitHub Actions minutes, approves publishing a specific tested hash, and is the only one who can do
  anything that changes this machine's Windows settings, default browser, or the real GitHub billing/Actions configuration.
- **A designated tester** (the owner or someone the owner asks) runs the [clean-Windows checklist](CLEAN_WINDOWS_CHECKLIST.md) on a machine Claude does
  not have access to. Claude cannot do this step itself: `Get-VM` is denied on the build machine (no Hyper-V rights) and Windows Sandbox is not installed;
  neither was enabled, per standing instruction not to change Windows features without asking.

## Every change, in order

1. **Build and unit-test locally.**
   ```powershell
   . .\scripts\env.ps1
   dotnet test --nologo -v q          # all projects; ~600 tests, under a minute
   ```
   A change to a bug gets a regression that is shown to fail against the old code (temporarily undo the fix, run the test, watch it fail, restore) — see
   `CONTRIBUTING.md`. A change to what is recorded, persisted or sent anywhere gets a privacy test in the same style as `LibraryTests`/`LibraryPolicyTests`.

2. **Build the candidate.**
   ```powershell
   .\scripts\release.ps1 -Version 0.1.0-alpha.N
   ```
   Runs the full unit suite again (it aborts the build if any test fails), then publishes a self-contained ZIP with `SHA256SUMS.txt` and an SBOM into
   `artifacts\release\0.1.0-alpha.N\`. Record the version, the source commit (`BUILD.json` inside the ZIP), and the ZIP's SHA-256.

3. **Packaged smoke, on that exact ZIP.**
   ```powershell
   .\scripts\packaged-smoke.ps1 -Zip artifacts\release\0.1.0-alpha.N\JevBrowse-0.1.0-alpha.N-win-x64.zip
   ```
   Extracts to a fresh folder, verifies the checksum, and runs the recovery, additions, protection, history and external-link checks against the real
   extracted app. A timeout is a failure, never a pass.

4. **Local interaction pass, on that exact ZIP.**
   ```powershell
   .\scripts\interaction-pass.ps1 -ZipPath artifacts\release\0.1.0-alpha.N\JevBrowse-0.1.0-alpha.N-win-x64.zip
   ```
   Extracts the ZIP **itself**, into a fresh, uniquely-named folder (it never trusts a folder it was merely told about), then drives the real app with
   actual key presses, the mouse wheel and UI Automation: bookmarks including the real Windows file picker, search engine, history, downloads with Show
   in folder, marking a site Sensitive, zoom (keyboard, an iframe, Ctrl+wheel), browser shortcuts with the page focused, the address bar, a second copy on
   the same data, and a security negative check (a page/frame cannot forge a shortcut or zoom message by calling the browser API directly). It records the
   ZIP's SHA-256, the source commit, and the Windows/WebView2 versions with its results.
   **This is not clean-Windows evidence.** It runs on the build machine, which has development tools and a real user profile; treat it as "the features
   work when driven correctly, and the security boundary holds against the same trick this machine can try" — not as "this behaves the same on a machine
   that has never seen JevBrowse or Visual Studio before."

5. **Write the release record.** One file per version in `docs/releases/`, following the existing ones: what changed, the exact commands run and their
   pass/fail counts (link the JSON results), what was **not** verified and why, and the data-format note if the database version changed. Say plainly when
   a fix broke the very thing it was meant to protect and was caught by re-running the same checks — that is a normal part of an honest record, not
   something to omit.

6. **CI, when it is available.** Push the commits (source, docs, the release record — never `artifacts/`, a data folder, browser profiles or anything
   from `D:\Dashboard\backend\.env`). A push to `main` triggers `.github/workflows/ci.yml`'s blocking `test` job (invariant gates, unit tests, a Release
   build) and its report-only jobs (`perf`, `idle-cpu`, `recovery-app`). Check the run:
   ```powershell
   & D:\Tools\gh\bin\gh.exe run list -L 3 --json status,conclusion,workflowName,headSha
   ```
   - **If it is billing-blocked** (the annotation reads "recent account payments have failed or your spending limit needs to be increased"), that is the
     owner's account to fix, in GitHub's own Settings → Billing and licensing. Claude does not attempt to change billing, and does not silently mark work
     as CI-verified when it was not; **label it "locally verified; CI not run"** and keep going on local evidence per steps 1-5.
   - **If it is green**, record the run id and headline result (test count, pass/fail) alongside the local numbers in the release record. CI is a
     second, independently-hosted confirmation that a clean checkout builds and every unit test passes; it does not run the packaged smoke, the
     interaction pass, or the clean-Windows checklist (those need a real desktop and, for the last one, a machine this account does not control).

7. **Clean-Windows acceptance — the actual gate for handing the ZIP to anyone else.** The designated tester runs
   [CLEAN_WINDOWS_CHECKLIST.md](CLEAN_WINDOWS_CHECKLIST.md) (21 rows, about 20-30 minutes) on **the exact ZIP from step 2**, on a machine with no
   development tools and no `JEVBROWSE_*`/`WEBVIEW2_*` settings, and reports PASS/FAIL/what-was-seen per row plus the hash they checked against
   `SHA256SUMS.txt`. This is the only step that exercises a machine JevBrowse was never built or tested on, and it is the step nothing in this runbook
   can substitute for — not CI, not the interaction pass, not a longer local session.

8. **Independent review of anything security-relevant**, when the release record says one is outstanding (as of alpha.7: the per-renderer token that
   gates shortcuts and zoom messages from a page — see `docs/releases/0.1.0-alpha.7.md`). This means someone other than the author reading the mechanism
   and trying to break it, not another automated pass by the same tooling.

9. **Explicit approval, tied to a specific hash.** Nothing is tagged or published until the owner says so, naming the SHA-256 they are approving. Until
   then the candidate sits in `artifacts\release\` and in the release record, unpublished.

## Publishing (only after step 9)

**Repo visibility is switched by the owner, at publish time, not before.** Making `reddy5310/jevbrowse` public is a GitHub Settings action Claude does
not take on its own initiative (Settings → General → Danger Zone → Change visibility); it happens once steps 7 and 8 have both passed and the owner gives
the final go, at the same moment the release itself is created, so the source and the first downloadable build appear together.

Two ways, both requiring the exact bytes that were tested:

- **`scripts\publish-release.ps1`** — verifies the ZIP's SHA-256 against the one you say was tested and against `SHA256SUMS.txt`, checks `BUILD.json`
  inside the zip, and only then (with `-Publish`) creates a GitHub pre-release via `gh release create`. Without `-Publish` it only verifies.
- **`.github/workflows/release.yml`** — a two-step, hand-triggered workflow (`action=package` builds and keeps an artifact; `action=publish` needs that
  run's id and the tested hash, and refuses to publish anything else). Needs Actions minutes; use the local script while Actions is unavailable or
  while minimizing spend.

Both refuse silently-different bytes; neither runs on a push or a tag by itself.

## What NOT to push to GitHub

Source code, docs, scripts, tests, and the `docs/releases/*.md` + `*.json` records — yes. **Not**: anything under `artifacts\` (the built ZIPs, symbols,
SBOM — those are evidence kept alongside the record's hash, reproducible from the source commit, and are large), browser data folders, the machine's
`D:\Dashboard\backend\.env` or any key from it, or Windows Credential Manager tokens. `.gitignore` already excludes `artifacts/`; keep it that way.

## What "CI as the release gate" does and does not mean

Using GitHub Actions again does not change steps 7 and 8: no CI job can click through the new panels on a fresh Windows account, and no CI job is an
independent security reviewer. CI's job in this runbook is exactly two things: confirm a clean checkout builds and every unit test passes on a machine
other than this one, and (report-only) publish performance numbers. Treat "CI is green" and "the clean-Windows checklist passed" as two different,
both-required facts, never one standing in for the other.
