# ADR 0001 — WebView2 first, no custom engine

Status: accepted (Phase 0)

Decision: Use WebView2 (Evergreen) behind `Renderer.Abstractions`. JevBrowse owns browser semantics (tabs, scheduling, policy); Chromium maintenance stays external.
Consequences: Chromium site isolation may spawn several processes per WebView, so memory is measured per process group, never per WebView count. WebView2 request-interception and native messaging are security-sensitive and stay behind narrow adapters.
Build note: unpackaged (`WindowsPackageType=None`) during development; Windows App SDK pinned at 2.5.1; MSIX at Phase 11.
