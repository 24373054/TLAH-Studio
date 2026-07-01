# TLAH Studio 4.1.1 Tool Capability Plan

## Objective

4.1.1 raises the reliability and breadth of built-in agent tools without changing the 4.0 platform architecture. The target is a stronger default tool set: fewer terminal fallbacks, better code navigation, more resilient web search, and clearer tool discovery for long agent runs.

## Claude Code Gap Summary

- Claude Code succeeds because the model has compact, reliable primitives for reading, searching, editing, and inspecting code before it reaches for shell commands.
- TLAH already has lifecycle, approval, rollback, MCP, and persisted activity history, but the built-in catalog still lacks common file management actions and symbol-level navigation.
- Web search failure currently depends too much on one DuckDuckGo HTML shape, which can terminate otherwise recoverable runs.

## 4.1.1 Scope

- Add file management tools: `file_info`, `file_mkdir`, `file_move`, and `file_delete`.
- Upgrade `file_search` with regex mode, case sensitivity, result limits, binary skipping, hidden/generated directory skipping, and safer error messages.
- Add `symbols` for lightweight class/function/member discovery across C#, XAML-adjacent, TypeScript/JavaScript, Python, and Markdown/code files.
- Harden `web_search` with multiple DuckDuckGo parsers, generic anchor fallback, redirect handling, result limits, and diagnostics that do not collapse the tool protocol.
- Improve `tool_search` with categories, aliases, and richer metadata so models can discover the right tool by intent.
- Register every new tool in DI, metadata, UX labels, safety assessment, prompts, tests, and release packaging.

## Acceptance Checks

- Old tools remain compatible and existing tests pass.
- New file tools reject path traversal and report clear outcomes.
- Search tools return useful results from fixture HTML and local fixture files.
- Symbol search returns stable `path:line kind name` rows.
- Tool metadata marks read-only tools as concurrent and write/delete tools as serial.
- `tools/ci.ps1` passes before release.
- `tools/build-release.ps1 -Version 4.1.1 -Upload` completes or reports a precise network/signing blocker.

## Release Notes

4.1.1 is a tools-focused release: broader file operations, code symbol discovery, stronger file and web search, better tool discovery, and regression coverage for the new tool catalog.
