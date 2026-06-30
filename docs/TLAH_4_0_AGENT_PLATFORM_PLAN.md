# TLAH Studio 4.0 Agent Platform Plan

## Goal

TLAH Studio 4.0 turns the agent from a chat transcript runner into a persistent work platform. The release completes the next large phase after 3.3.x: first-class tasks, structured context recovery, local background agents, tool discovery, and automation-facing event surfaces. Existing 3.3.x lifecycle, safety, hook, rollback, activity panel, web search, IME, and release work stays intact.

## Claude Code Gap Summary

Local Claude Code source under `../_analysis/claude-code-src` shows several durable runtime objects that TLAH only partially had:

- Tasks and todos are explicit objects (`TodoWrite`, `TaskCreate`, `TaskUpdate`, `TaskList`) and are reintroduced to the model after compaction.
- Compaction is layered: trim or microcompact tool output first, summarize only when needed, then reattach memory, active tasks, files, and task status.
- Background agents write output to task files and notify the parent session instead of flooding the main context.
- Tool search delays infrequent tool descriptions until requested, reducing prompt weight.
- Skills/plugins include frontmatter and execution boundaries so specialized instructions can be discovered without polluting the main session.
- SDK and UI consume the same event stream, making runs replayable after completion and app restart.

## 4.0 Scope

### Phase A: Persistent Task Layer

Add `AgentTaskItem` rows keyed by chat/run. Support `todo_write`, `task_create`, `task_update`, and `task_list`. Tasks have stable ids, title, description, status, priority, source, optional parent, and metadata JSON. The agent system prompt and every model turn receive a concise incomplete-task summary.

### Phase B: Context and Memory Recovery

Use the existing `ReactiveCompactor` in the main runtime before falling back to deterministic compaction. After any compaction, inject a structured runtime context block with project memory, active tasks, recent files/artifacts, recent failures, and persisted output references. Large tool outputs continue to live in `.tlah_context/tool-results`; add `read_persisted_output` for targeted recovery.

Structured compact summaries must include:

- `files_changed`
- `commands_run`
- `open_questions`
- `next_actions`

### Phase C: Local Background Agents

Extend background tasks with kind, output file, input JSON, and mailbox fields. Add `task_output`, `task_stop`, and `task_send_message`. `task_create` can launch a local background agent stub or shell task that writes to an output file now; full worktree isolation remains a later enhancement.

### Phase D: Tool Search, Skills, MCP, SDK

Add `tool_search` as the first lazy-discovery contract for built-ins, MCP tools, and skills/plugins. Expand skill metadata parsing to recognize `when_to_use`, `allowed_tools`, `model`, `paths`, and `hooks`. Keep execution local and bounded in 4.0. SDK/event work focuses on JSONL-compatible persisted event data and stable replay.

## UI Contract

The right Agent Activity panel keeps its 3.2 layout but presents three layers:

1. Run summary: status, steps, artifacts, events.
2. Task list: active/pending/completed task rows for the chat.
3. Tool timeline: persisted `AgentEvents` and `ToolInvocations`.

The panel must stay replayable after run completion. No horizontal scrolling; long text wraps or truncates.

## Acceptance Tests

- Todo/task tools persist and list tasks across service instances.
- Runtime model calls include active task summaries every turn.
- Context compaction emits structured metadata and reinjects memory/task/artifact/failure references.
- Large outputs are persisted and recoverable through `read_persisted_output`.
- Background task output is written to a stable file and can be read/stopped/messaged.
- `tool_search` returns ranked tool metadata without executing tools.
- Existing long-run, approval, web search, IME, lifecycle, and release tests remain green.

## Release Checklist

1. Update all app, updater, manifest, installer, and release metadata to `4.0.0`.
2. Run `tools/ci.ps1 -Configuration Release -Platform x64`.
3. Build installer with `tools/build-release.ps1 -Version 4.0.0`.
4. Verify local installer, `latest.json`, installer URL, SHA256, and update discovery.
5. Upload to `https://download.matrixlabs.cn/tlah/windows/`.
6. Commit, push, and include test/release evidence in the final delivery.

## Rollback

4.0.0 stores new data in additive tables/columns only. If rollback is needed, older clients ignore `AgentTaskItems` and new background task columns. Release rollback is performed by restoring `latest.json` to the previous signed 3.3.x payload and leaving local data untouched.
