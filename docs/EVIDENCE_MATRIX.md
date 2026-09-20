# Evidence matrix

Every claim JevBrowse makes, and the strongest evidence behind it today. Levels, weakest to strongest:

- **Designed**: an ADR/doc says so; nothing verifies it.
- **Unit**: a test against fakes proves the logic (fast, but it cannot see engine or disk behaviour).
- **Real engine**: a benchmark/check drives actual WebView2 renderers and inspects real disk/DB state (`--privacy-check`, `--restore-bench`, …).
- **Release-validated**: verified on the packaged, signed build on more than one machine. **Nothing is at this level yet** (builds are unsigned and tested on one machine).

Last updated 2026-09-20 after the third independent review (ADR 0017, ADR 0018). 234 unit tests.

| Claim | Level | Evidence | Known gap |
|---|---|---|---|
| Disposing a renderer reclaims memory; suspend barely does | Real engine | ADR 0003, `--memory-lab`: 765 → 319 MB private (5 live → 1 live) | WebView2 process group only; the shell process is not in the number. One machine |
| Restore is fast | Real engine, thin | `--restore-bench`: p50 350 ms, p95 1.24 s | 8 samples, warm cache, measured to navigation-complete not "usable"; failed restores not counted |
| 50 tabs, ≤5 live; scheduler never thrashes | Unit | `TabKernelTests`, 2-hour thrash simulation, interleaving test (`PrivacyAndLifecycleTests`) | Simulation uses synthetic pressure; no low-RAM hardware run |
| Lifecycle operations cannot race (no duplicate renderers, no dispose-under-use) | Unit | 12 concurrent activations → 1 acquisition; 60 random interleaved ops keep tab state and renderer set consistent | Fake renderer latency, not WebView2's real async behaviour |
| **Private session leaves no durable trace** | **Real engine** | `--privacy-check` (real renderers): 0 thumbnails, tab rows, checkpoints, timeline entries, workspace rows, permission rows, index docs; private profile deleted on restart. Unit: `Switching_away_captures_a_thumbnail_only_when_policy_allows`, `Private_workspace_leaves_no_context_checkpoint…` | Crash dumps and OS-level artifacts (pagefile, DNS cache) are outside what we control and not checked |
| Sensitive pages store no pixels | Real engine | `--privacy-check`: real bank login page classified SECRET, 0 thumbnails, 0 checkpoint | One site; URL heuristics cover a fixed word list |
| A stricter class removes what the looser class stored | Unit | `Stricter_class_purges_thumbnail_checkpoint…`, `Tightening_a_pages_class_removes_it_from_the_index` | Not yet driven end-to-end in a real renderer |
| Identity boundaries are real (moving across identities creates a new tab; agents never touch personal identities) | Unit | `Moving_across_identities…`, `A_session_can_never_name_its_way_into_an_existing_identity…` | Cookie separation between profiles relies on WebView2 user-data folders (engine guarantee, not re-tested by us) |
| Permission grants are temporary and scoped to container + exact origin | Unit (key + policy) | `PermissionKeyTests` | `SavesInProfile=false` is set in the WebView2 adapter but not covered by an automated real-engine test |
| Page classification combines evidence by the stricter result | Unit | `Independent_evidence_combines_by_the_stricter_result` | — |
| **PUBLIC is earned, not assumed; "Not assessed" is honest** | Unit + real engine | `Public_must_be_earned_and_is_never_assumed`: the only routes to PUBLIC are a structurally public host or the user's per-site choice. No page-side signal promotes — absence of a sign-in affordance is not evidence of public availability | The host list is short and hand-maintained, so most sites sit at Not assessed until the user decides. That is the intended direction of error |
| **Nothing we could not assess leaves the device** | Unit | `A_page_we_could_not_assess_is_never_sent_even_on_an_explicit_request` covers both the chat and typed-decision routers with AI on, cloud on, providers configured and the user explicitly asking; `…MayLeaveDeviceOnExplicitRequest` is an enumerated set, not an ordinal test | Not driven through the real Ask dialog by an automated check |
| **Restore outcomes are distinguished and shown** | Unit | `CaptureResult` outcomes plus `PreservedParts` (Address/Position/Preview): `Shortfall` names the loss the user will notice and stays silent when only the preview was lost | The 12 s restore timeout and failure choices are not yet covered by an automated UI test |
| AI is off; background AI needs its own switch; nothing secret leaves | Unit | 21 Brain tests: kill switch, class gates, automatic-off, size cap, redaction of state *and* question text | Redaction is regex-based; `Redactor` recall on real pages is unmeasured |
| Cloud-call audit is accurate | Unit | task, class, automatic/explicit recorded; `CloudCallsByClass` counts typed Jev calls | UI shows the log; no export |
| Agents can only narrow a user-approved grant | Unit | `AgentCeiling` clamp tests; host fails closed without a ceiling; per-session approval | — |
| **An allowed URL cannot redirect an agent out of scope** | **Real engine** | `--agent-check`: real `youtu.be → www.youtube.com` 301 cancelled on the first load; policy registered before the renderer exists | One redirect shape; frame-level navigations not separately exercised |
| **Stop/expiry revoke and release, even protected pages** | Unit + real engine | `--agent-check` (0 live after Stop, `session_closed` after); `No_path_leaves_a_session_closed_with_live_pages` | — |
| Agent limits hold under concurrency | Unit | 8 concurrent navigations vs a 2-page limit; 20 concurrent requests vs a 5-action budget; in-flight request when Stop lands | Fake renderer latency, not WebView2 timing |
| Agent live-page quota is a hard limit | Unit | `Live_page_quota_is_a_hard_limit…` | CPU/network quotas do not exist |
| **Live capture blocks automatic hibernation, in frames and while a page is stalled** | **Real engine** | `--media-check`: WebRTC loopback negotiates and decodes frames (VP8/VP9/H264/AV1); real camera + microphone open through the permission path; the tab reports `MicrophoneActive, CameraActive`, the scheduler is refused **by those flags**, renderer and both tracks survive, and protection clears and sleeping resumes after stop. Frames served from `http://127.0.0.1`: a sibling and a frame nested two deep both capture, stopping one leaves the other protected, destroying a frame leaves the survivor protected. A page blocked for 12 s keeps `MicrophoneActive` and is still refused — a missed heartbeat means *uncertain*, never *idle* | **Not exercised:** a real Meet/Zoom session end to end, the screen-share picker, disconnect/reconnect under memory pressure. The `disconnected` recovery window and track-clone tracking are implemented but covered by design only. `SharedArrayBuffer` was absent on a probe page that sends no COOP/COEP — **not** evidence that JevBrowse disables it |
| Shield blocks ads/trackers on real sites | Real engine | `--shield-check` (goodreturns/cricbuzz/CNN), 110k rules, ~90 µs p95 | Debug build; long/adversarial URL cost unmeasured |
| Shield "disable for site" removes every layer | Unit + manual | per-renderer script ownership; disabled list baked into site modules | Needs a real-engine check that a disabled site really loads unmodified |
| YouTube ad definitions are pruned | Real engine | `--youtube-check`: Despacito 37 s of ads → 0 s | Ads are served non-deterministically; 3 videos; YouTube can change formats |
| Browser Memory ranks by relevance and stays bounded | Unit | BM25 sign fix test; budget test | Disk budget is text × 2, an estimate; real DB size is reported next to it |
| Migrations cannot leave a database that cannot start | Unit | `A_failed_migration_step_rolls_back_completely_and_the_next_start_recovers` | SQLite crash-kill (process abort mid-write) not simulated |
| Filter-list activation survives a crash | Unit | `A_crash_between_the_two_activation_renames…` | |
| CI enforces performance regressions | **Designed** | perf job is informational | No stable benchmark machine; no PR gate |
| Signed, updatable release | **Designed** | portable zip only | No code signing, no MSIX, no security-update path |

## Known limitations (deliberate, documented)
- **Most sites are Not assessed, and stay that way until you say otherwise.** PUBLIC comes only from a short list of structurally public hosts (Wikipedia, MDN, Microsoft Learn, …) or from your own per-site choice on the class badge. JevBrowse will not infer "public" from a page merely having content and no visible sign-out link — an authenticated report looks identical. The cost is that Browser Memory indexes little by default and Ask refuses on most pages until you classify the site. That is deliberate.
- **Product scope.** The architecture serves general users, developers, researchers and agents at once. The dependable first release is *durable workspaces with predictable memory-efficient tab restoration*; everything else is opt-in.
- **Not implemented:** bookmark import/export, extensions, password manager, sync, accessibility audit (keyboard-only and screen-reader passes are unverified), high-contrast theme testing.
