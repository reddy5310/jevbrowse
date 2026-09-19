# ADR 0010 — Browser Memory: local FTS index gated by Trust OS, bounded on disk

Status: accepted (Phase 8 gate, 2026-09-20)

Decision:
- Browser Memory is a SQLite FTS5 index (`memory_docs` + external-content `memory_fts`, unicode61 with diacritics removed) over the **readable main text** of pages, extracted in-page by a Readability-lite script (article/main first, boilerplate stripped, densest block fallback). Pages under 200 characters of readable text are not indexed.
- Indexing happens only when Trust OS allows `IndexContent` for the tab's class and container: PUBLIC by default. AUTHENTICATED, SENSITIVE, SECRET (password field present) and everything in Private/Disposable containers are never indexed; the reason is surfaced in the status bar. Closing a tab forgets its document.
- Query path (§8): FTS5 bm25 (title weighted 3×) → local boosts (recency up to +50% within ~2 weeks, +25% same workspace) → optional Jev/LLM rerank via `BrainRouter` (`RerankSearch`), which receives only titles and snippets, never URLs or the query history, and falls back to local order on any refusal.
- Disk growth is bounded by a byte budget (default 200 MB); the oldest documents are dropped first. This is the cost-card "disk growth bounded" line for the feature.
- Search never leaves the machine unless the user has turned AI and Cloud on; even then only the rerank step does, with the data above.
- The indexer lives outside the kernel (`MemoryIndexer` subscribes to kernel events), keeping the kernel free of search concerns (Table A.4).

Verified by `BrowserMemoryTests` (roundtrip, prefix/diacritics, boosts, budget pruning, rerank + fallback) and `MemoryIndexerTests` (public indexed; authenticated/sensitive/ephemeral/password never; close forgets).

Deferred: local embeddings/HNSW (Table A.3 evolution), per-workspace index views, index of virtualized pages' checkpoints (currently only pages loaded in-session are indexed), Ctrl+K command palette (Phase 11).
