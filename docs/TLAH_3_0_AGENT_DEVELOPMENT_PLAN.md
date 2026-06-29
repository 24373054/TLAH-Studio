# TLAH Studio Agent Development Plan to 3.0.0

## 中文执行摘要

这是一份给本地 Claude Code 使用的开发交接文档，目标是把当前 TLAH Studio 从 2.6.x 推进到 3.0.0。文档主体使用英文，是为了让本地 agent 在执行时减少技术歧义；但内容基于当前仓库和本地 Claude Code 源码的实际对比，不是泛泛路线图。

执行方式：

1. 把本文和仓库根目录 `C:\Users\23157\CODE\00TLAH\TLAH` 一起交给本地 Claude Code。
2. 告诉它严格按 milestone 顺序开发，不要一次性大爆炸改完。
3. 每完成一个 milestone 必须运行 `.\tools\ci.ps1`，并提交清晰 commit。
4. 不要直接复制 Claude Code 源码，只吸收架构模式和产品行为。
5. Claude Code 完成后，由 Codex 按本文最后的验收清单复核、打包和发布。

This document is the development handoff for taking TLAH Studio from the current 2.6.x baseline to a professional 3.0.0 agent product. It is intended to be given directly to a local Claude Code instance as the project brief.

Do not treat this as a loose idea list. Treat it as an implementation contract: each milestone has target architecture, concrete files, tests, manual QA, and release gates.

## How To Use This Document

Give this file to the local agent together with the repository root:

`C:\Users\23157\CODE\00TLAH\TLAH`

Reference Claude Code source only for architecture and product behavior patterns:

`C:\Users\23157\CODE\00TLAH\_analysis\claude-code-src`

Important constraint: do not copy Claude Code source code into TLAH. Use it as a reference for design patterns, UX behavior, and testing targets. TLAH must remain an independent C# / WinUI implementation.

## Current Baseline

Current repository state observed on 2026-06-29:

- Main repo: `C:\Users\23157\CODE\00TLAH\TLAH`
- Current app version in project files: `2.6.0`
- Current latest release tag in git history: `v2.6.0`
- Current `main` also has a later website-only commit: `Refresh download landing page`
- Main stack: C# / .NET 8 / WinUI 3 / Windows App SDK / EF Core SQLite / Inno Setup
- Quality scripts:
  - `.\tools\ci.ps1`
  - `.\tools\build-release.ps1 -Version x.y.z -ReleaseNotes "..."`
  - `.\tools\verify-release.ps1 -Version x.y.z -AllowUntrustedAuthenticode`

The product has already moved well beyond a basic chat wrapper. The repo contains:

- Provider abstraction and streaming-capable providers:
  - `TLAHStudio.Core\Llm\OpenAICompatibleProvider.cs`
  - `TLAHStudio.Core\Llm\AnthropicProvider.cs`
  - `TLAHStudio.Core\Llm\ILlmProvider.cs`
- Agent persistence and event models:
  - `TLAHStudio.Core\Models\AgentRuntimeModels.cs`
  - `TLAHStudio.Data\TlahDbContext.cs`
- Agent runtime service layer:
  - `TLAHStudio.Core\Services\AgentRuntimeServices.cs`
- Main orchestration path:
  - `TLAHStudio.Core\Services\LlmService.cs`
- Tool platform:
  - `TLAHStudio.Core\Services\AgentTools.cs`
  - `TLAHStudio.Core\Services\WorkspaceCodeTools.cs`
  - `TLAHStudio.Core\Services\ToolSafetyKernel.cs`
  - `TLAHStudio.Core\Services\ToolPlatformService.cs`
  - `TLAHStudio.Core\Services\McpClientService.cs`
  - `TLAHStudio.Core\Services\McpAgentTools.cs`
  - `TLAHStudio.Core\Services\ExecutionBackends.cs`
- Context and memory:
  - `TLAHStudio.Core\Services\AgentContextServices.cs`
- UI:
  - `TLAHStudio.App\ViewModels\ChatPageViewModel.cs`
  - `TLAHStudio.App\Views\ChatPage.xaml.cs`
  - `TLAHStudio.App\ViewModels\ToolPlatformViewModel.cs`
  - `TLAHStudio.App\ViewModels\TeamWorkspaceViewModel.cs`
- Tests:
  - `TLAHStudio.Core.Tests\AgentRuntimeStreamTests.cs`
  - `TLAHStudio.Core.Tests\BuiltInAgentToolsTests.cs`
  - `TLAHStudio.Core.Tests\ToolPlatformServiceTests.cs`
  - `TLAHStudio.Core.Tests\LlmProviderTests.cs`
  - `TLAHStudio.Core.Tests\LlmServiceSafetyTests.cs`
  - `TLAHStudio.Core.Tests\WorkspaceServiceTests.cs`
  - `TLAHStudio.Core.Tests\UpdateServiceTests.cs`

## Current Strengths

Keep these. They are good foundations.

1. Provider streaming exists.
   `OpenAICompatibleProvider` and `AnthropicProvider` both accept `IProgress<LlmStreamUpdate>` and implement SSE-style streaming. The app is not limited by provider capability alone.

2. The database already has agent primitives.
   `AgentRun`, `AgentStep`, `ToolInvocation`, `AgentEvent`, `AgentCheckpoint`, and `AgentArtifact` are modeled and indexed.

3. The tool protocol is already richer than a basic function-call wrapper.
   `AgentToolMetadata` includes read-only, concurrency, destructive, render hint, result size, persistence, user-facing name, activity description, and interrupt behavior.

4. The permission platform exists.
   `ToolPolicyRule` supports tool, path, and domain subjects with chat/project/global scopes.

5. The code tools are already present.
   `code_read`, `code_grep`, `code_glob`, `code_edit`, `code_multi_edit`, `code_diff`, `code_apply_patch`, `code_rollback`, and `lsp_diagnostics` exist.

6. The release pipeline is workable.
   `build-release.ps1` updates project versions, builds self-contained binaries, creates an Inno installer, signs manifest JSON, verifies SHA256 and smoke install, and can upload release files.

## Critical Gaps

These are the main reasons the product still does not feel like Claude Code / Codex-class agent software.

### 1. `LlmService` Still Owns The Agent Loop

`AgentRunEngine` currently wraps a continuation delegate and flushes events, but the actual loop, step handling, provider call, tool call extraction, approval wait, tool execution, checkpoint update, and finalization remain in `LlmService`.

Observed files:

- `TLAHStudio.Core\Services\AgentRuntimeServices.cs`
- `TLAHStudio.Core\Services\LlmService.cs`

This limits testability, makes UI streaming depend on service side effects, and makes future runtime features difficult.

Target: `AgentRunEngine` must become the real state machine. `LlmService` should become a thin facade for chat operations and persistence coordination.

### 2. Tool Scheduling Exists But Is Not The Main Loop

`ToolExecutionScheduler` can partition and execute tool batches, but the main agent loop in `LlmService` only takes the first tool call from a model response.

Observed issue:

- `LlmService.cs` uses first tool call behavior around the tool-call handling path.

Claude Code reference pattern:

- `src\services\tools\toolOrchestration.ts`
- `src\services\tools\StreamingToolExecutor.ts`

Target: multiple tool calls in one model response must be handled. Read-only and concurrency-safe calls should execute concurrently. Writes/destructive operations stay serial.

### 3. UI Rendering Is Too Expensive For Long Agent Runs

`ChatPage.xaml.cs` manually renders messages into panels and throttles full render requests. This works for small chats but becomes fragile when messages, live activity, tool output, thinking blocks, and file attachments grow.

Observed files:

- `TLAHStudio.App\Views\ChatPage.xaml.cs`
- `TLAHStudio.App\ViewModels\ChatPageViewModel.cs`

Target: UI should consume an event stream and update stable message view models incrementally. Use virtualized controls or segmented rendering. Do not rebuild long message trees on every token.

### 4. Streaming UX Is Simulated In Places

Provider-level streaming exists, and the ViewModel drains queued chars. However, final persisted messages can replace the live streaming message, and agent events are not the only source of truth. If the model has a long pre-answer thinking phase, first visible text can still feel delayed.

Target:

- UI displays model stream events as soon as provider bytes arrive.
- Thinking deltas are persisted as collapsible in-chat blocks.
- Text deltas stay in the same assistant message.
- Final reload cannot replace the live streamed message with a visually different static version.
- Add metrics for first-thinking latency, first-text latency, token/sec, UI render backlog, and DB flush latency.

### 5. Context Management Is Deterministic But Not Mature

`AgentContextManager` estimates tokens and inserts a deterministic summary boundary. It is useful but not equivalent to Claude Code style automatic context survival.

Observed file:

- `TLAHStudio.Core\Services\AgentContextServices.cs`

Claude Code reference patterns:

- `src\services\compact\autoCompact.ts`
- `src\services\compact\microCompact.ts`
- `src\services\compact\sessionMemoryCompact.ts`
- `src\services\compact\postCompactCleanup.ts`
- `src\query\tokenBudget.ts`

Target: add token budget tracking, reactive compaction on provider context errors, microcompaction of old tool outputs, model-assisted compact summaries, summary boundary records, and memory index rules.

### 6. Tool Safety Needs Semantic Depth

`ToolSafetyKernel` already detects protected paths, destructive commands, read/write operations, and network restrictions. It is still too centralized and mostly regex-based.

Observed files:

- `TLAHStudio.Core\Services\ToolSafetyKernel.cs`
- `TLAHStudio.Core\Services\ToolPlatformService.cs`

Claude Code reference patterns:

- `src\tools\BashTool\commandSemantics.ts`
- `src\tools\BashTool\destructiveCommandWarning.ts`
- `src\tools\BashTool\pathValidation.ts`
- `src\tools\BashTool\readOnlyValidation.ts`
- `src\components\permissions`

Target: each tool should provide its own typed safety preview: files read, files written, domains accessed, commands executed, rollback available, risk level, and exact permission rule that would match.

### 7. MCP Is Present But Not A Full Platform

TLAH has STDIO and Streamable HTTP support plus UI config. Claude Code has connection lifecycle, reconnect/backoff, resource/prompt support, OAuth handling, status surfaces, dynamic tool/command/resource updates, and server-specific trust behavior.

Observed TLAH files:

- `TLAHStudio.Core\Services\McpClientService.cs`
- `TLAHStudio.Core\Services\McpAgentTools.cs`
- `TLAHStudio.App\ViewModels\ToolPlatformViewModel.cs`

Claude Code reference patterns:

- `src\services\mcp\client.ts`
- `src\services\mcp\useManageMCPConnections.ts`
- `src\services\mcp\auth.ts`
- `src\services\mcp\config.ts`
- `src\services\mcp\types.ts`

Target: MCP should be managed as a connection platform, not just a tool-call helper.

### 8. LSP Is Still A Tool, Not A Service

TLAH exposes diagnostics through a code tool. Claude Code has an LSP server manager with extension routing, file open/change/save/close synchronization, and passive feedback.

Observed TLAH file:

- `TLAHStudio.Core\Services\WorkspaceCodeTools.cs`

Claude Code reference patterns:

- `src\services\lsp\LSPServerManager.ts`
- `src\services\lsp\LSPServerInstance.ts`
- `src\services\lsp\LSPDiagnosticRegistry.ts`

Target: persistent LSP service with diagnostics cache, language server lifecycle, and tool access to symbols/references/diagnostics.

### 9. Memory Is Too Simple

TLAH project memory exists, but Claude Code memory is file-based, indexed, scoped, truncated safely, and explicit about what to save.

Observed TLAH file:

- `TLAHStudio.Core\Services\AgentContextServices.cs`

Claude Code reference patterns:

- `src\memdir\memdir.ts`
- `src\memdir\memoryTypes.ts`
- `src\memdir\findRelevantMemories.ts`
- `src\memdir\memoryScan.ts`

Target: typed memory directory with `MEMORY.md` index, memory files, relevance scan, truncation limits, and explicit save/update/delete tools.

### 10. Product Extension Surface Is Missing

Claude Code has plugins, skills, commands, hooks, SDK schemas, tasks, and remote/coordinator modes. TLAH currently has project/team settings and MCP, but not a coherent extension model.

Claude Code reference patterns:

- `src\plugins`
- `src\skills`
- `src\commands`
- `src\entrypoints\sdk\coreSchemas.ts`
- `src\tasks`
- `src\remote`

Target: TLAH 3.0 should not implement every Claude Code feature, but it needs a coherent plugin/skill/hook story and an automation/SDK surface.

## Architecture Principles For 3.0

1. Event stream is the source of truth.
   Every visible state transition must be produced by `AgentEventStream`: model request, thinking delta, text delta, tool request, approval request, tool start, progress, result, compact, checkpoint, error, pause, resume, completion.

2. UI consumes events, not service reloads.
   The chat surface should be able to display a run from events alone. Reloading from the database is for startup/recovery, not normal streaming.

3. Provider adapters are byte-to-event translators.
   Provider code should not know UI concepts. It converts vendor streaming payloads into normalized stream events.

4. Tool execution is scheduled by metadata.
   Read-only/concurrency-safe tools can run concurrently. Writes and destructive tools are serial. Tool results stream progress and final artifacts.

5. Long runs must be resumable.
   Checkpoints and event logs must be sufficient to resume after app restart, provider error, approval wait, or step budget pause.

6. Safety is explainable.
   Permission UI must explain exactly what will be read, written, executed, or accessed, and which rule will be saved when the user chooses a persistent decision.

7. Large output is referenced, not stuffed into context.
   Store large tool output and artifacts separately. Put summaries and references into model context.

8. Every milestone needs regression tests.
   Do not add large runtime behavior without fake provider tests, tool scheduler tests, DB/event tests, and at least one UI smoke scenario.

## Milestone 2.7.0: Runtime Ownership And True Event Streaming

### Goal

Move agent loop ownership out of `LlmService` and into a real `AgentRunEngine`. Normalize all model/tool/approval/output updates into one event stream.

### Files To Change

- `TLAHStudio.Core\Services\LlmService.cs`
- `TLAHStudio.Core\Services\AgentRuntimeServices.cs`
- `TLAHStudio.Core\Services\ILlmService.cs`
- `TLAHStudio.Core\Models\AgentRuntimeModels.cs`
- `TLAHStudio.Data\TlahDbContext.cs`
- `TLAHStudio.Core.Tests\AgentRuntimeStreamTests.cs`
- Add tests under `TLAHStudio.Core.Tests\AgentRunEngineTests.cs`

### Required Implementation

1. Create a real runtime state type:
   - `AgentRunState`
   - `AgentRunFrame`
   - `AgentModelTurn`
   - `AgentToolBatch`
   - `AgentRuntimeEvent`

2. Move the while loop from `LlmService.ContinueAgentRunInternalAsync` into `AgentRunEngine`.

3. `LlmService.RunAgentTaskAsync` and `ResumeAgentTaskAsync` should:
   - create or load run records
   - delegate execution to `AgentRunEngine`
   - return final `SendMessageResult`

4. Add event stream events for:
   - `model_stream_started`
   - `thinking_delta`
   - `text_delta`
   - `tool_call_delta`
   - `tool_batch_planned`
   - `tool_progress`
   - `tool_result_persisted`
   - `runtime_metric`

5. Persist events in batches:
   - append to in-memory stream immediately
   - flush to SQLite on interval or threshold
   - never block UI streaming on a SQLite write unless finalizing the run

6. Add an event subscription API:
   - subscribe by `AgentRunId`
   - replay from sequence number
   - then continue live

7. Support multiple tool calls in one model response.
   Do not use first-tool-call behavior. Build a batch from all valid calls.

8. Add cancellation handling:
   - provider call cancellation
   - tool execution cancellation
   - approval wait cancellation
   - safe run status update on cancellation

### Tests

Add fake providers and fake tools that emit controlled delays.

Required tests:

- first thinking event is delivered before model completion
- first text event is delivered before model completion
- multiple read-only tools run concurrently
- write tool waits for prior read-only batch to finish
- failed tool creates event and continuation context
- app restart can resume from latest checkpoint
- event sequence numbers remain unique and ordered under batched writes

### Manual QA

1. Ask a simple normal chat question.
   Expected: first visible assistant text appears before final response completes.

2. Ask an agent task that calls two read tools.
   Expected: two tool activities appear in the same step and complete independently.

3. Trigger an approval wait.
   Expected: UI stays responsive; approval dialog shows exact tool input; app can be closed and reopened with pending approval preserved.

4. Kill app during a run.
   Expected: next launch shows paused/interrupted run and can resume or discard.

### Done Criteria

- `LlmService` no longer contains the main agent while loop.
- UI can render live run state from events without waiting for final `SendMessageResult`.
- `.\tools\ci.ps1` passes.

## Milestone 2.8.0: Chat UI Virtualization And Stable Streaming UX

### Goal

Fix UI stalls and make long agent runs feel continuous. Chat rendering must scale to long histories, large tool output, attachments, and streaming messages.

### Files To Change

- `TLAHStudio.App\Views\ChatPage.xaml`
- `TLAHStudio.App\Views\ChatPage.xaml.cs`
- `TLAHStudio.App\ViewModels\ChatPageViewModel.cs`
- `TLAHStudio.App\ViewModels\Services.cs`
- Add UI model classes if needed under `TLAHStudio.App\ViewModels`

### Required Implementation

1. Replace full manual rerendering with stable item view models.
   Use a virtualized list control or an equivalent segmented renderer. Avoid rebuilding all message UI when one token arrives.

2. Split message content into blocks:
   - text block
   - thinking block
   - tool use block
   - tool result block
   - file attachment block
   - image/video preview block
   - error block

3. Streaming update rules:
   - append deltas to current block
   - do not recreate previous blocks
   - collapse thinking automatically when final answer text starts
   - keep collapsed thinking in the chat transcript

4. Remove visual-only live activity card as the primary event display.
   Activity should be represented as durable in-chat blocks. A compact sidebar/timeline is acceptable, but it must not be the only record.

5. Add performance instrumentation:
   - render queue length
   - render batch duration
   - dropped/throttled render count
   - max UI thread update interval

6. Approval dialog:
   - remove empty JSON input boxes
   - show structured preview: command, paths, domains, file diff, risk
   - support copy raw JSON as secondary action

7. Attachment display:
   - image preview
   - video/audio preview when supported
   - file card with name, size, hash, open, save as
   - zip/txt/md/code preview where safe

### Tests

Core tests can cover view model behavior. Full WinUI automation can be light but must include at least a smoke check.

Required tests:

- streaming deltas update one message without duplicate final message
- thinking block remains after completion and collapses
- attachment message format round-trips
- large content does not produce unbounded render requests

### Manual QA

1. Run a 50-step agent task with multiple tools.
   Expected: scrolling and clicking remain responsive.

2. Expand and collapse old thinking blocks.
   Expected: no full chat freeze.

3. Send image, zip, txt, and generated artifact.
   Expected: visible file cards, preview where appropriate, download/open action.

### Done Criteria

- Long runs no longer freeze the window.
- Streaming message is not replaced by a static duplicate after completion.
- UI has measurable render backlog metrics.

## Milestone 2.9.0: Tool Scheduler, Tool Protocol, And Safety v3

### Goal

Make tools behave like first-class runtime components with scheduling, typed validation, streamable progress, semantic safety, and dedicated UI rendering.

### Files To Change

- `TLAHStudio.Core\Services\AgentTools.cs`
- `TLAHStudio.Core\Services\WorkspaceCodeTools.cs`
- `TLAHStudio.Core\Services\ToolSafetyKernel.cs`
- `TLAHStudio.Core\Services\ToolPlatformService.cs`
- `TLAHStudio.App\Views\ChatPage.xaml.cs`
- Add `TLAHStudio.Core\Services\Tools\*` folder
- Add `TLAHStudio.Core\Tests\ToolSchedulerTests.cs`

### Required Implementation

1. Split monolithic tools into per-tool classes.
   Keep compatibility with existing tool names.

2. Upgrade tool interface:
   - `ValidateInput`
   - `ClassifySafety`
   - `PlanEffects`
   - `RenderUse`
   - `RenderResult`
   - `ExecuteAsync`
   - `StreamProgress`
   - `CreateRollbackPlan`

3. Add effect model:
   - paths read
   - paths written
   - domains accessed
   - commands executed
   - environment variables consumed
   - credentials requested
   - destructive risk
   - rollback available

4. Add command semantics:
   - grep/rg no-match exit code is not a failure
   - diff exit code 1 means files differ
   - test false is not a tool crash
   - PowerShell and Bash have separate parsers and warning rules

5. Add tool progress:
   - command started
   - stdout chunk
   - stderr chunk
   - file written
   - artifact created
   - result truncated/persisted

6. Permission UI must use effect model.
   Do not show raw JSON as the main approval surface.

7. Add hook points:
   - before tool use
   - after tool use
   - after failed tool use
   Hooks can be internal first. External plugin exposure can wait.

### Tests

- scheduler partitions read-only and write tools correctly
- tool result order is stable
- dangerous PowerShell and Bash commands are classified
- command exit semantics are interpreted correctly
- permission rules match tool/path/domain patterns
- rollback plan is created for write tools where feasible

### Done Criteria

- Agent can process multiple tool calls per model turn.
- Permission dialogs explain concrete effects.
- Tool output can stream progress before final completion.

## Milestone 2.10.0: Context And Memory v2

### Goal

Make long tasks survive large histories and large tool outputs without losing important state.

### Files To Change

- `TLAHStudio.Core\Services\AgentContextServices.cs`
- `TLAHStudio.Core\Services\LlmService.cs`
- `TLAHStudio.Core\Services\AgentRuntimeServices.cs`
- `TLAHStudio.Core\Models\AgentRuntimeModels.cs`
- `TLAHStudio.Data\TlahDbContext.cs`
- Add memory services under `TLAHStudio.Core\Services\Memory`

### Required Implementation

1. Add real token budget service:
   - model context window
   - reserved output tokens
   - tool result budget
   - thinking budget
   - image/document estimate
   - provider-specific adjustments

2. Add context warning states:
   - safe
   - warning
   - compact soon
   - compact now
   - blocking

3. Add reactive compact:
   - when provider returns context length error, compact and retry once
   - if compact fails repeatedly, pause with actionable error
   - record compact failure count

4. Add microcompact:
   - old tool outputs are replaced by references and summaries
   - persisted tool results stay accessible
   - model receives a compact boundary explaining what was removed

5. Add model-assisted compact:
   - summarize old turns with a dedicated prompt
   - preserve unresolved tasks, file changes, user preferences, tool artifacts, and decisions
   - persist summary as a checkpoint boundary

6. Add memory directory:
   - `MEMORY.md` index
   - individual memory files
   - typed memory categories: user, feedback, project, reference
   - line/byte truncation
   - relevance search before loading

7. Add memory tools:
   - `memory_list`
   - `memory_read`
   - `memory_write`
   - `memory_update`
   - `memory_delete`

### Tests

- token budget threshold calculation
- deterministic compact fallback
- reactive compact retry path
- microcompact replaces old large result with persisted reference
- memory index truncates safely
- memory tools preserve encoding and newline

### Manual QA

1. Run a long task with repeated tool outputs.
   Expected: no context overflow; old output becomes references.

2. Restart app during compacted run.
   Expected: summary boundary and checkpoint restore correctly.

3. Save a memory and start a new chat in same workspace.
   Expected: relevant memory is loaded; irrelevant memory is not.

### Done Criteria

- Long tasks do not fail merely because tool output grew.
- Context compaction is visible, persisted, resumable, and test-covered.

## Milestone 2.11.0: Workspace Code Intelligence

### Goal

Make TLAH credible as a development agent, not only a shell/file executor.

### Files To Change

- `TLAHStudio.Core\Services\WorkspaceCodeTools.cs`
- Add `TLAHStudio.Core\Services\Code`
- Add `TLAHStudio.Core\Services\Lsp`
- `TLAHStudio.App\ViewModels\ToolPlatformViewModel.cs`
- Add tests under `TLAHStudio.Core.Tests\WorkspaceCodeReliabilityTests.cs`

### Required Implementation

1. Build a persistent workspace service:
   - workspace root selection
   - allowed roots
   - ignored paths
   - file index
   - encoding registry
   - file snapshot store

2. Upgrade code tools:
   - read with line ranges
   - grep using ripgrep where available
   - glob with ignore rules
   - edit with exact match and conflict detection
   - multi-edit with atomic apply
   - diff with unified and side-by-side render hints
   - apply patch with path boundary checks
   - rollback by snapshot id

3. Add LSP manager:
   - configure language server per extension
   - start/stop server
   - didOpen/didChange/didSave/didClose sync
   - diagnostics registry
   - symbols
   - references
   - definitions

4. Add diagnostics workflow:
   - run after code edits
   - summarize errors/warnings
   - attach diagnostics to tool result
   - allow agent to fix and rerun

5. Add file change conflict detection:
   - before hash
   - after hash
   - last write time
   - external modification warning

### Tests

- edit preserves UTF-8 BOM/no BOM, CRLF/LF
- multi-edit is atomic
- rollback restores exact bytes
- grep/glob respect ignored directories
- patch cannot escape workspace root
- LSP manager handles unavailable server gracefully

### Done Criteria

- Agent can safely edit a real C# project and produce diff/diagnostics.
- Every write operation has a rollback path.

## Milestone 2.12.0: MCP, Plugin, And Skill Platform

### Goal

Turn MCP from a configuration panel into a reliable extension platform. Add a lightweight TLAH-native skill/plugin model.

### Files To Change

- `TLAHStudio.Core\Services\McpClientService.cs`
- `TLAHStudio.Core\Services\McpAgentTools.cs`
- `TLAHStudio.Core\Services\ToolPlatformService.cs`
- `TLAHStudio.App\ViewModels\ToolPlatformViewModel.cs`
- Add `TLAHStudio.Core\Services\Plugins`
- Add `TLAHStudio.Core\Services\Skills`

### Required Implementation

1. MCP connection manager:
   - connected / pending / failed / needs auth / disabled status
   - reconnect with exponential backoff
   - server enable/disable
   - server list changed notifications
   - tool/resource/prompt list changed notifications

2. MCP auth:
   - header templates with credential broker references
   - OAuth placeholder architecture if full OAuth is not ready
   - safe credential redaction in logs/events

3. MCP resources and prompts:
   - list resources
   - read resources
   - list prompts
   - use prompt as context

4. MCP tool normalization:
   - stable tool names matching provider function-name restrictions
   - reversible mapping to server/tool name
   - collision handling

5. Plugin manifest:
   - local plugin folder
   - manifest JSON
   - optional MCP server configs
   - optional prompt templates
   - optional skills
   - trust and enable/disable controls

6. Skill loader:
   - load Markdown skill docs
   - expose selected skills to the model
   - keep skill content out of context unless selected or relevant

### Tests

- MCP STDIO config persists and reloads
- HTTP config persists and reloads
- invalid JSON is rejected with visible error
- tool name normalization is provider-safe
- credential references are not written into raw request/response logs
- disabled MCP server tools are unavailable

### Done Criteria

- User can add a practical STDIO MCP server, save, close, reopen, and use its tools.
- MCP failure is visible and recoverable without app restart.

## Milestone 2.13.0: Sandboxes, Remote Execution, And Background Tasks

### Goal

Make long-running and risky tasks reliable without freezing the app or relying only on local PowerShell.

### Files To Change

- `TLAHStudio.Core\Services\ExecutionBackends.cs`
- `TLAHStudio.Core\Services\SandboxCommandService.cs`
- `TLAHStudio.Core\Services\AgentRuntimeServices.cs`
- `TLAHStudio.App\ViewModels\ChatPageViewModel.cs`
- Add `TLAHStudio.Core\Services\Tasks`

### Required Implementation

1. Backend abstraction:
   - restricted local
   - WSL2
   - Docker
   - remote HTTP sandbox

2. Backend capability detection:
   - installed
   - available
   - configured
   - failed with reason

3. Resource limits:
   - runtime
   - output
   - memory
   - process count
   - file size
   - network allowlist

4. File sync:
   - upload selected attachments into sandbox
   - export generated artifacts
   - artifact manifest with hash/size/type

5. Background tasks:
   - create task
   - list tasks
   - inspect output
   - stop task
   - attach task results to chat

6. Interruption behavior:
   - stop current run
   - stop current tool
   - keep artifacts and partial output
   - resume from checkpoint when possible

### Tests

- backend selection falls back safely
- command timeout kills process tree
- output is truncated and persisted
- artifact hashes are stable
- stopped task changes status and does not continue writing events

### Done Criteria

- Long shell commands no longer freeze the UI.
- A user can send files into sandbox and receive generated artifacts back in the chat.

## Milestone 2.14.0: SDK, Automation, And Observability

### Goal

Add enough automation and observability to debug real user failures and support external integrations.

### Files To Change

- Add `TLAHStudio.Core\Services\Diagnostics`
- Add `TLAHStudio.Core\Services\Sdk`
- Add optional local HTTP/pipe host in `TLAHStudio.App`
- Update privacy/diagnostic export UI

### Required Implementation

1. Runtime metrics:
   - first thinking latency
   - first text latency
   - token/sec
   - event write latency
   - render backlog
   - tool queue wait time
   - approval wait time
   - compact duration

2. Diagnostic package:
   - app version
   - OS version
   - provider settings without secrets
   - recent agent event log
   - crash logs
   - update logs
   - redaction preview before export

3. Local SDK surface:
   - start run
   - stream events
   - approve/deny tool
   - stop/resume run
   - list chats/runs
   - export run transcript

4. JSON schema contract:
   - event schema
   - tool schema
   - permission schema
   - MCP config schema
   - diagnostics schema

5. Performance regression tests:
   - fake 10k-token stream
   - fake 100 events/sec
   - fake 50-message history
   - fake 20 artifacts

### Tests

- diagnostic export redacts API keys and tokens
- event stream schema round-trips
- SDK stream can replay from sequence number
- performance test does not exceed defined render/event thresholds

### Done Criteria

- A failed user run can be diagnosed from exported logs without exposing secrets.
- External automation can subscribe to an agent run event stream.

## Milestone 3.0.0: Professional Agent GA

### Goal

Stabilize the product as a professional Windows agent application.

### Required Work

1. Architecture freeze:
   - no remaining agent loop in `LlmService`
   - all agent state changes go through event stream
   - all tool actions go through scheduler and permission system

2. Product polish:
   - consistent light/dark theme
   - high-DPI verified
   - accessible keyboard flow
   - clear status indicators
   - no raw JSON as primary UI except inspector/debug surfaces

3. Reliability:
   - app restart recovery
   - crashed run recovery
   - interrupted update recovery
   - corrupted DB recovery path or clear-data option

4. Security:
   - no API key in raw logs
   - no credential in events
   - path boundary tests
   - domain allowlist tests
   - destructive command tests
   - signed manifest verification tests

5. Release quality:
   - clean install smoke test
   - upgrade from last public version smoke test
   - uninstall/reinstall smoke test
   - update loop regression test
   - Windows Defender / SmartScreen limitations documented

6. Documentation:
   - user guide
   - MCP examples
   - workspace/code-agent guide
   - privacy/data guide
   - troubleshooting guide
   - release verification guide

### 3.0 Acceptance Scenarios

1. Normal chat:
   - first visible response appears quickly
   - thinking is collapsible if provider sends it
   - final message does not jump or duplicate

2. Agent file task:
   - user sends a zip
   - agent extracts, reads, edits, runs tests
   - user sees tool progress live
   - changed files can be inspected and downloaded

3. Code task:
   - agent edits a C# file
   - shows diff
   - runs tests
   - shows diagnostics
   - rollback works

4. Long task:
   - 40+ steps
   - compact occurs
   - UI remains responsive
   - run survives app restart

5. MCP task:
   - user configures STDIO MCP
   - tools appear
   - approval shows concrete effects
   - failure is recoverable

6. Update:
   - old version installs
   - update prompt downloads latest
   - installer replaces files without update loop
   - about page shows correct version

## Implementation Order For Claude Code

Follow this exact order unless a blocker is discovered.

1. Create a branch:
   - `git checkout -b agent-3-roadmap`

2. Run baseline quality gate:
   - `.\tools\ci.ps1`

3. Add or update tests before large refactors where possible.

4. Implement one milestone at a time.
   Do not combine all milestones into one giant unreviewable commit.

5. After each milestone:
   - run `.\tools\ci.ps1`
   - commit with a clear message
   - update this document if the implementation changes the plan

6. Before requesting release:
   - run full CI
   - inspect `git status --short`
   - provide summary of changed modules, tests, and known gaps

7. Do not publish or upload installers unless explicitly requested.
   The user plans to ask Codex to verify and publish after Claude Code finishes.

## Coding Standards For This Work

1. Keep public APIs small and typed.
2. Prefer services with explicit interfaces where tests need fakes.
3. Do not add UI blocking waits on the WinUI thread.
4. Do not store secrets in raw request/response, events, diagnostics, or crash logs.
5. Do not use ad-hoc string parsing where JSON/schema/path APIs exist.
6. Preserve existing user data with additive migrations.
7. Keep release scripts compatible with current `tools` workflow.
8. Add tests for bug fixes and runtime behavior, not only happy paths.

## Suggested New Project Structure

This is a target structure, not a mandatory exact layout.

```text
TLAHStudio.Core/
  Agent/
    AgentRunEngine.cs
    AgentRunState.cs
    AgentEventBus.cs
    AgentCheckpointStore.cs
    AgentContextPipeline.cs
  Providers/
    ProviderStreamAdapter.cs
    ProviderEventNormalizer.cs
  Tools/
    IAgentTool.cs
    ToolScheduler.cs
    ToolEffectPlan.cs
    ToolSafetyClassifier.cs
    File/
    Git/
    Shell/
    Code/
    Mcp/
  Context/
    TokenBudgetService.cs
    AutoCompactService.cs
    MicroCompactService.cs
    MemoryDirectoryService.cs
  Workspaces/
    WorkspaceRootService.cs
    FileSnapshotStore.cs
    FileIndexService.cs
  Lsp/
    LspServerManager.cs
    LspDiagnosticRegistry.cs
  Diagnostics/
    RuntimeMetricsService.cs
    DiagnosticPackageService.cs
```

The current files can be migrated gradually. Do not break binary compatibility inside one huge move unless tests are already in place.

## Release Process After Development

When the implementation is ready and the user asks for release:

1. Confirm clean status:
   - `git status --short`

2. Run:
   - `.\tools\ci.ps1`

3. Build release:
   - `.\tools\build-release.ps1 -Version 3.0.0 -ReleaseNotes "<release notes>" -AllowUntrustedCertificate -Upload`

4. Verify:
   - `.\tools\verify-release.ps1 -Version 3.0.0 -AllowUntrustedAuthenticode`

5. Validate server manifest:
   - download `https://download.matrixlabs.cn/tlah/windows/latest.json`
   - check version, SHA256, size, signature

6. Manual upgrade test:
   - install previous public version
   - open app
   - accept update
   - verify current version after restart

## Final Verification Checklist For Codex

When Claude Code reports completion, Codex should verify:

- `LlmService` no longer owns the agent loop.
- `AgentRunEngine` is a real state machine.
- event stream replay works from sequence number.
- streaming text appears before final model completion.
- thinking blocks persist and collapse correctly.
- multiple tool calls are scheduled correctly.
- read-only concurrent tools run concurrently.
- write tools are serial.
- permission UI uses effect previews.
- context compaction has tests and persisted boundaries.
- large tool outputs are persisted and referenced.
- code tools preserve encoding/newline and support rollback.
- MCP configs persist and reload.
- UI remains responsive during long runs.
- diagnostic export redacts secrets.
- release scripts still work.

## Non-Goals Before 3.0

Do not spend core runtime time on these unless the user explicitly changes priority:

- enterprise deployment and managed policy distribution
- paid license system
- cloud sync backend
- real code-signing certificate procurement
- marketplace distribution
- full browser automation beyond current tool scope

These are important later, but 3.0 should first make the agent runtime, tool safety, context survival, code tools, and UI responsiveness solid.
