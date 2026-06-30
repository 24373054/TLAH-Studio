# TLAH Studio 3.3.0 Stability and Tool Lifecycle Plan

## Goals

3.3.0 focuses on making the 3.2.0 agent sidebar and long-running execution reliable enough to build on. The release scope is limited to two engineering phases:

- Phase 0: establish a stable regression baseline for long chains, approval resume, restart recovery, persisted tool output, and activity history.
- Phase 1: route tool execution through one lifecycle that supports V3 safety preview, effect planning, hooks, progress events, result limiting, and rollback metadata.

This plan is additive. It does not roll back the 3.2.0 right-side activity panel, version updates, or contributor documentation.

## Claude Code Gap Summary

The Claude Code source snapshot in `_analysis` shows several mature runtime properties that TLAH Studio must match before adding larger features:

- Stable run state: every step, event, checkpoint, and pending invocation must survive long chains and app restarts.
- Unified tool lifecycle: preview, approval, execution, progress, output persistence, and rollback must share one source of truth.
- Observable execution: UI should replay historical activity from persisted events, not transient in-memory state.
- Conservative safety: the risk shown before approval must match the risk used at execution time.
- Regression discipline: long chain, approval, persistence, and restart behavior must have automated tests.

Out of scope for 3.3.0: subagents, todo/task tools, full skills, real LSP integration, remote agents, and copying Claude Code implementation details.

## Phase 0: Stable Baseline

Regression coverage must verify:

- A 50-step fake-provider run completes without losing steps, events, or checkpoints.
- Approval pause/resume continues the same pending invocation.
- Large tool output is persisted under `.tlah_context/tool-results`; context keeps only the preview.
- Restart recovery marks active runs as paused and keeps them resumable.
- The activity panel can query historical `AgentEvents` after run completion.

Existing 3.2.0 tests already cover approval resume, large-output persistence, and activity history. 3.3.0 extends this baseline with lifecycle-specific event persistence tests and keeps CI green.

## Phase 1: Unified Tool Lifecycle

`IToolLifecycleRunner` is the single execution path called by `ToolExecutionScheduler`. The fixed order is:

1. Normalize and find tool.
2. Validate input.
3. For `IAgentToolV3`, call `ClassifySafetyAsync` then `PlanEffectsAsync`.
4. Run `BeforeUse` hooks.
5. If a hook modifies arguments, update `ToolInvocation.ArgumentsJson`, revalidate, and recompute safety/effects.
6. Block on validation or safety failures.
7. Execute with progress (`ExecuteWithProgressAsync` for V3, `ExecuteAsync` for legacy tools).
8. Limit output to the tool/result budget.
9. Run `AfterUse` or `AfterFailedUse` hooks.
10. Generate rollback plan only after successful execution.

`ToolExecutionOutcome` now includes `EffectPlan`, `RollbackPlan`, and `ProgressEvents`. Legacy tools remain compatible and return null effect/rollback metadata.

## Events and UI Contract

The right-side activity panel remains layout-stable. It reads persistent `AgentEvents`, including:

- `tool_progress`: lifecycle progress events.
- `tool_hook_blocked`: hook-level blocks.
- `tool_rollback_plan`: rollback metadata after successful writes.

All event data is stored in `AgentEvent.DataJson` with secret redaction applied by the event stream.

## Test Matrix

Lifecycle tests cover:

- V3 execution order.
- `BeforeUse` block prevents execution.
- `BeforeUse` argument modification causes revalidation and replanning.
- Legacy tools still execute.
- Safety block prevents execution and after hooks.
- Progress events are collected.
- Rollback plans are generated after success only.
- Existing batch scheduling rules stay unchanged.

Quality gates:

- `.\tools\ci.ps1 -Configuration Release -Platform x64`
- `.\tools\build-release.ps1 -Version 3.3.0 -ReleaseNotes "<notes>" -AllowUntrustedCertificate -ForceSmokeTest`
- `.\tools\verify-release.ps1 -Version 3.3.0 -AllowUntrustedAuthenticode`

## Release and Rollback

Release steps:

1. Update App/Core/Data/Updater project versions, manifests, appsettings, installer metadata, and `latest.json` to 3.3.0.
2. Build the installer and signed update metadata.
3. Upload to `ubuntu@140.143.183.163:/var/www/download/tlah/windows/`.
4. Verify `https://download.matrixlabs.cn/tlah/windows/latest.json` reports 3.3.0 and the installer URL returns 200.

Rollback strategy:

- If CI fails, do not upload.
- If verification fails after upload, restore the previous `latest.json` and installer metadata on the server.
- If runtime regressions appear, keep 3.2.0 as the last known stable GitHub release point: `1c0f421 Release 3.2.0 agent activity sidebar`.
