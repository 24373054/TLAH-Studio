# TLAH Studio 4.14.0

Version 4.14.0 makes research and professional artifact creation visible, direct, and measurable inside TLAH Studio.

## Create & Research workbench

- Open **Create & Research** from the expanded sidebar, compact sidebar, message composer, or command palette. The core workflow does not require a slash command, JSON payload, hidden prompt phrase, or separate application.
- Research public sources in Quick, Balanced, or Deep mode with domain, recency, and language controls; depth selects the source budget. Results preserve citations, extraction failures, evidence gaps, and conflicting sources, and can be saved as a Markdown evidence report.
- Create styled XLSX workbooks from pasted tabular data, including frozen headers, filters, automatic widths, and bar or line chart previews. Agent spreadsheet tools additionally support formulas and safe range updates.
- Create Markdown, DOCX, or PDF documents from normal text and structured sections, then inspect supported document formats through the agent tools.
- Create flowcharts, architecture diagrams, bar charts, and line charts as validated SVG and high-DPI PNG files.
- Generated files go to the active workspace. If no workspace is selected, the app uses that chat's isolated `%LOCALAPPDATA%\TLAH Studio\sandboxes\<chat>` folder. The workbench displays the full path and provides result preview, **Open result**, and **Open folder** actions, so no external setup is required.

## More reliable tool use

- A dynamic tool selector exposes no more than 15 relevant tools at the start of a normal turn instead of sending the entire catalog to the model.
- Tool search promotes only real registered tools into the next step, while 220 unique bilingual intent cases exercise the real 51-tool registry with category negatives and deterministic selection across code, files, Git, shell, web, MCP, tasks, memory, documents, spreadsheets, and diagrams.
- Official OpenAI and Anthropic endpoints receive strict schemas and safe read-only parallel-call hints. Compatible endpoints keep conservative payloads.
- Tool results now carry structured content, normalized error codes, retryability, source references, duration, diagnostics, warnings, and artifacts.
- Retry guidance distinguishes invalid arguments, permission waits, transient network faults, rate limits, timeouts, missing resources, unsupported content, and uncertain mutations without weakening the 4.13 permission model.

## Local quality view

- The workbench includes a local Tool Quality page for call volume, completion, failure, denial, latency, shell fallback, catalog search, and per-tool success rate.
- Quality queries use tool names, statuses, and timestamps only; prompts, arguments, results, and file contents are not read for these metrics.

## Validation

- The complete Release CI gate passes all 654 tests, including 220 deterministic bilingual tool-selection cases, real specialist provider schemas, artifact wrapper execution, coverage floors, and the NuGet vulnerability audit.
- Focused fixtures reopen generated XLSX, DOCX, PDF, SVG, and PNG artifacts and cover research partial failures, unsupported content, private-address blocking, path traversal, and content-free quality metrics.
- The x64 WinUI Release build completes with zero warnings and zero errors.

The official artifact is a self-contained Windows x64 installer for Windows 10 build 19041+ and Windows 11. Update metadata is ECDSA-signed and the installer is protected by SHA-256 and Authenticode signing; the current Authenticode certificate remains self-signed, so Windows may display an untrusted-publisher warning.
