# ADR 0002 — Visible resource ≠ live renderer

Status: accepted (Phase 0)

Decision: A tab is a durable logical object (`VirtualTab`); a live WebView is a temporary lease. Only HOT/WARM/COLD/SUSPENDED hold renderers (`ResourceState.HasLiveRenderer`). Any protected condition (audible, WebRTC, download, dirty form, pinned, never-hibernate) vetoes *automatic* demotion; only an explicit user action can override.
Enforced by: `tests/Unit/JevBrowse.Domain.Tests/VirtualTabTests.cs`.
