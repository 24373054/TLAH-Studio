# Changelog

All notable user-visible changes are recorded here. The project follows semantic versioning for stable releases.

## [4.14.0] - 2026-07-16

### Added

- Added a visible **Create & Research** workbench reachable from the expanded sidebar, compact sidebar, composer, and command palette without slash commands or JSON.
- Added Quick, Balanced, and Deep public-source research with domain, recency, language, source-count, evidence, conflict, partial-failure, HTML/PDF extraction, and Markdown report support.
- Added spreadsheet creation, inspection, and update tools for CSV/XLSX, including formulas, styling, frozen headers, filters, automatic widths, and chart previews.
- Added Markdown/DOCX/PDF creation and inspection plus SVG/PNG flowchart, architecture, bar-chart, and line-chart generation.
- Added a local, content-free Tool Quality view with call, outcome, latency, shell-fallback, catalog-search, and per-tool success metrics.

### Changed

- Replaced the all-tools prompt payload with deterministic context selection capped at 15 initially callable tools and deferred catalog promotion.
- Extended tool contracts with namespaces, categories, strictness, examples, output schemas, safety annotations, structured results, normalized errors, retryability, sources, duration, and diagnostics.
- Enabled strict schemas and safe read-only parallel calls for official OpenAI and Anthropic endpoints while preserving conservative compatibility payloads.
- Added 220 bilingual intent-selection cases and artifact/research fixtures that reopen generated XLSX, DOCX, PDF, SVG, and PNG outputs.
- Preserved the 4.13 permission, approval, immutable safety, retry, checkpoint, and long-run recovery contracts across the new tools.

## [4.13.0] - 2026-07-15

### Fixed

- Unified permission decisions across tool preview, approval, resume, lifecycle, and final execution so an approved exact invocation can no longer be rejected by a second, inconsistent gate.
- Made approval argument editing explicit and read-only by default; edited payloads must be valid JSON objects and pass the selected tool's input validation before replacing persisted arguments.
- Detect incomplete Anthropic and OpenAI-compatible streams instead of treating truncated output as a successful response.
- Prevent unresolved or repeated tool failures from being reported as successful completion.
- Fence timed-out or disconnected mutating operations as unknown outcomes so Resume never replays a side effect that may already have completed.
- Resolve background command backends inside a fresh dependency scope instead of retaining disposed foreground database services.
- Isolate the local SDK and long-lived chat, sidebar, debug, and window services so concurrent UI refreshes no longer share one EF database context.
- Close immutable-guard bypasses involving PowerShell delete aliases, abbreviated switches, nested script blocks, Windows root wildcards, and WSL/POSIX system roots.

### Changed

- `Full access` now bypasses ordinary policy, host-path, network-allowlist, and sensitive-file prompts; only immutable catastrophic-operation guards and required user-interaction pauses remain.
- Added bounded transient-provider retry with stream reset, checkpointed pause after three failed attempts, and permission-preserving resume.
- Added failure-aware replanning and an explicit recovery question when the agent cannot safely make further progress.
- Made persisted chat, project, and global allow rules reach the inner file, network, Git, MCP, typed, and V3 execution gates instead of stopping at preview.
- Added adaptive long-chain budgets: useful runs extend automatically from the 48-step soft budget up to 192 steps, while an explicit Resume can add further bounded work without approval callbacks inflating the budget.
- Increased the default command runtime limit from 30 to 120 seconds.

## [4.12.0] - 2026-07-13

### Fixed

- Stabilized Activity lifecycle handling, right-workbench placement, DPI scaling, live refresh, and theme inheritance after reopening.
- Eliminated narrow-window zero-width panel states and stale Activity subscriptions.
- Centered and made Settings responsive so full-screen and compact layouts remain visible and scrollable.
- Removed double-framed slash-command, reasoning, and permission flyouts.

### Changed

- Separated reasoning effort (`Auto`, `Off`, `Low`, `Medium`, `High`, `Max`) from tool permissions.
- Improved scrolling, virtualization, diff rendering, and long-conversation collection work.
- Added refined compositor motion, press feedback, visual tokens, and reduced-motion behavior.
- Bounded self-signed Authenticode verification so release builds cannot wait indefinitely on trust-chain discovery.

## [4.11.1] - 2026-07-12

- Restored Activity placement and theme state after the panel was closed and reopened.

## [4.11.0] - 2026-07-12

- Introduced the Nocturne visual language, icon refinement, micro-interactions, and smoother scrolling behavior.

## [4.10.0] - 2026-07-12

- Completed a major frontend architecture, accessibility, and long-session performance pass.

## [4.9.9] - 2026-07-11

- Added conversation virtualization/paging, asynchronous loading hardening, stateful composer controls, and plan review improvements.

## [4.9.8] - 2026-07-11

- Hardened agent permission and plan-state behavior, versioned update signatures, and release verification.

## [4.9.7] - 2026-07-10

- Improved reliability, dependency security, background tasks, update rollout, and atomic deployment.

Earlier milestones are retained as historical design records in [`docs/`](./docs/README.md). Release artifacts and notes are available on the [GitHub Releases page](https://github.com/24373054/TLAH-Studio/releases).

[4.14.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.14.0
[4.13.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.13.0
[4.12.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.12.0
[4.11.1]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.11.1
[4.11.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.11.0
[4.10.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.10.0
[4.9.9]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.9
[4.9.8]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.8
[4.9.7]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.7
