# Changelog

All notable user-visible changes are recorded here. The project follows semantic versioning for stable releases.

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

[4.12.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.12.0
[4.11.1]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.11.1
[4.11.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.11.0
[4.10.0]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.10.0
[4.9.9]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.9
[4.9.8]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.8
[4.9.7]: https://github.com/24373054/TLAH-Studio/releases/tag/v4.9.7
