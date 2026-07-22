# TLAH Studio 4.14.0 — Tool Intelligence & Professional Workbench

## Release Goal

4.14.0 turns tool use into a measurable product capability. A feature is complete only when it is discoverable in the normal UI, usable without slash commands or hidden prompt phrases, produces a verifiable result, survives restart, and is covered by automated and manual release checks.

The release must preserve the 4.13.0 permission model. Full access bypasses ordinary restrictions, Ask approval authorizes the persisted invocation, and immutable catastrophic-operation guards remain enforced.

## User Experience Contract

- Add a visible **Create & Research** entry to the expanded and compact sidebar.
- Add a composer entry that opens the same workbench without inserting a slash command.
- Provide first-class pages for Research, Spreadsheet, Document, Diagram, and Tool Quality.
- Every page includes a plain-language description, useful defaults, progress, cancellation, result preview, open-file/open-folder actions, and actionable failures.
- Generated files default to the active workspace. When no workspace is configured, the workbench uses the chat's isolated `%LOCALAPPDATA%\TLAH Studio\sandboxes\<chat>` root, displays its full location, and provides result preview/open actions.
- Agent-created artifacts appear in normal run activity and carry an accessible path, MIME type, size, and preview hint.
- No core workflow may require editing JSON, installing a separate application, or leaving TLAH Studio.

## Workstream Checklist

### A. Tool Platform V2

- [x] Extend tool definitions with namespace, category, strictness, deferred-loading, examples, output schema, and safety annotations.
- [x] Keep no more than 15 common tools callable at the start of a normal turn.
- [x] Select additional tools from user intent, recent conversation/tool context, failed-tool recovery, and catalog search results.
- [x] Replace the static catalog with the live registry, including trusted plugin and MCP tools.
- [x] Map provider capabilities for strict schemas, tool choice, allowed tools, and parallel calls with compatibility fallbacks.
- [x] Return structured content, normalized error codes, retryability, source references, duration, warnings, and artifacts.
- [x] Classify invalid arguments, permission waits, transient network faults, rate limits, timeouts, missing resources, unsupported content, and uncertain mutations.
- [x] Retry only safe/retryable operations and never blindly replay uncertain mutations.

### B. Deep Research

- [x] Support quick, balanced, and deep research modes.
- [x] Support allowed/blocked domains, recency, language, and requested source count.
- [x] Parse real HTML with relative-link resolution and bounded content extraction.
- [x] Deduplicate URLs and domains and preserve the complete source list.
- [x] Build evidence packs from multiple independent domains with excerpts and timestamps.
- [x] Report insufficient evidence and conflicting sources explicitly.
- [x] Support partial success when some pages fail.
- [x] Use adaptive zero-key search fallbacks: GDELT Project only for non-language-constrained news, and bounded language-matched Wikipedia search only when undated results satisfy the recency filter.
- [x] Give each structured search endpoint one attempt, falling through on 408/429/5xx, timeout, network failure, or Wikipedia `ratelimited`/`maxlag`; retain a local provider gate that prevents query-variant request bursts.
- [x] Preserve provider URLs and applicable Wikipedia CC BY-SA 4.0 attribution through evidence, reports, agent output, and visible previews.
- [x] Expose research directly in the workbench and as an agent tool.

### C. Spreadsheet

- [x] Read CSV and XLSX metadata, sheets, ranges, values, formulas, and inferred column types.
- [x] Create CSV/XLSX from pasted tabular data without requiring JSON.
- [x] Support headers, table styling, number formats, formulas, frozen headers, filters, and automatic widths.
- [x] Support safe cell/range updates and conflict-safe output naming.
- [x] Produce bar/line charts and include a preview artifact.
- [x] Reopen generated workbooks during validation.

### D. Documents

- [x] Read Markdown, DOCX, and PDF text and document metadata.
- [x] Create Markdown, DOCX, and PDF from title and Markdown-like content.
- [x] Support headings, paragraphs, lists, tables, images, and basic header/footer metadata.
- [x] Validate Open XML packages and reopen generated PDF documents.
- [x] Provide a readable preview and normal file/open-folder actions.

### E. Diagrams

- [x] Create flowcharts, architecture diagrams, bar charts, and line charts.
- [x] Export SVG and high-DPI PNG from the same deterministic layout.
- [x] Support light/dark-safe palettes, readable labels, and bounded canvas sizes.
- [x] Validate SVG XML and decode generated PNG files during tests.

### F. Quality and Release

- [x] Add versioned, local-only tool metrics without storing arguments or file contents.
- [x] Measure deterministic opportunity recall over 220 bilingual cases and expose local execution success, denial/failure, latency, shell fallback, and catalog-search metrics.
- [x] Add at least 200 generated selection cases plus focused execution fixtures.
- [x] Add UI automation names and keyboard-accessible entry points.
- [x] Run unit, integration, Release CI, publish startup smoke, and real artifact checks.
- [x] Synchronize version metadata, installer, manifests, update metadata, changelog, README files, architecture, privacy, development, and release documentation.
- [ ] Merge verified source, tag `v4.14.0`, publish GitHub Release assets, atomically deploy update files, and verify the public update chain.

## Release Gates

| Gate | Required result |
|---|---|
| Initial callable tools | 15 or fewer for a normal chat turn |
| Tool-definition context | At least 60% smaller than the 4.13.0 all-tools baseline |
| Selection evaluation | Expected specialist selected from the 51-tool registry, unrelated routed categories excluded, and deterministic sets verified across 220 unique bilingual cases |
| Tool schemas | Strict-schema normalization and runtime input-validation tests pass |
| Built-in execution fixtures | All focused artifact/tool fixtures pass |
| Research fixtures | All focused extraction, retry, partial-failure, and network-boundary fixtures pass |
| Verified research mode | At least two independent domains or an explicit insufficient-evidence result |
| Artifact integrity | 100% of generated XLSX, DOCX, PDF, SVG, and PNG fixtures reopen successfully |
| Permission regressions | Zero failures in the 4.13.0 permission and recovery matrix |
| User discoverability | Sidebar, compact sidebar, composer, and command palette all expose the workbench |

Manual UI verification also covers cold start, duplicate clicks, operation cancellation,
chat switching, Light/Dark theme changes, 200% DPI at 640×400 and maximized window
sizes, and direct research/XLSX/DOCX/SVG/PNG generation with in-app previews.

## Release Sequence

1. Land the platform contract and evaluation baseline.
2. Land research and artifact engines behind internal service interfaces.
3. Connect agent tools and the visible workbench.
4. Run targeted tests and the full CI gate.
5. Exercise the packaged application and generated files.
6. Build and sign immutable release artifacts.
7. Merge, tag, publish GitHub assets, and atomically deploy the update manifest last.
