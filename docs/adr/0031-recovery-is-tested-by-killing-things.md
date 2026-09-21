# ADR 0031: Recovery is tested by killing things, and a damaged database never blocks start

Status: accepted.

## The problem

The recovery claims were tested by simulating failure (a migration step that throws, a fake profile). Real interruption, upgrade from files real
older builds wrote, and a damaged file were untested, and one of them turned out to be a real gap.

## Decisions

- **Crash tests kill a real process.** `tests/Tools/CrashHarness` is a child process the tests start and then terminate with TerminateProcess (no `finally`,
  no flush, no dispose). It writes, migrates, or holds a throwaway profile; the test inspects what is left. `BrowserDb` gained one optional constructor
  argument, `beforeCommit`, called after a migration step's SQL and before its commit, so a test can kill the process at the one instant a migration is
  half-done (it is null in production). Running steps outside their transaction fails those tests (mutation-checked).
- **Older databases are real files.** `tests/Fixtures/db/v1..v9.db` are produced by compiling the `BrowserDb.cs` of the commit that introduced each version
  (`scripts/make-db-fixtures.ps1`), seeded identically, so a fixture is what that build wrote. Every one must upgrade with its data intact. **Released
  migration steps are hash-pinned** against hashes recorded from git history: editing a released step fails a test, because databases already migrated with
  the old text would never run the new one.
- **A damaged `browser.db` is set aside, never deleted, and a fresh one starts** (`BrowserDb.OpenOrRecover`). Only SQLITE_CORRUPT and SQLITE_NOTADB count as
  damage; a locked file, a permission error or a database from a newer build are thrown, because moving a healthy file aside would lose data for nothing.
  The person gets a dialog saying what happened and where the old file is. Before this the app opened a window that never finished starting (found while
  building the real-app check). A constructor that throws now disposes its connection, so the damaged file can be renamed.
- **The real app is killed too** (`scripts/recovery-check.ps1`): a normal tab plus a live Private session (a tab with a cookie), then TerminateProcess or a
  normal close, then inspection of disk, then a restart. Cases: crash, clean shutdown, corrupt database. 27 checks. Disabling the start-up sweep fails two of
  them (mutation-checked).

## What this does NOT establish

- **Power loss or a failing disk.** A killed process keeps the OS cache; a power cut does not. The database runs WAL with `synchronous=NORMAL`, which keeps
  the file consistent but may lose the last commits. Not simulated; whether to move to FULL is an open decision.
- **"The engine lingers after a crash" was my instrument, not the app.** Three early real-app runs reported about 22 WebView2 processes still alive 30 s after
  the app was killed. Windows reuses process ids: the check recorded the tree by id, and a later unrelated process (another application's WebView2, seen in
  the third case) that inherited a freed id counted as a leftover, and the script then stopped it. Now a process is "the same" only if id AND start time
  match, and only those are ever stopped. With that, 12 consecutive crash runs showed every engine process gone in about one second. Earlier runs of these
  scripts may have stopped an unrelated process that had inherited an id; nothing was deleted by that.
- Cookie values inside a leftover throwaway profile are on disk until the next start. A crash cannot avoid that.
- Fixtures are tiny; real users' databases are larger and messier. A partly damaged database larger than 256 MB gets only the core-table check, not a full one.
- Settings and preference files are still written non-atomically (a kill mid-write can lose a preference; the readers tolerate a truncated file).

## A lesson about the process

While mutation-checking, restoring a file by copying an older backup over it gave it an OLDER modification time than the mutated build output, so the
incremental build kept the mutated code and the app built from it failed to start. Restore mutated files by rewriting them (new timestamp), and treat "the
app suddenly does not start" after a mutation check as this until proven otherwise.
