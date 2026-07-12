# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TLAH Studio (Talk Like A Human) is a Windows-native AI agent workspace built on C# + WinUI 3 + Windows App SDK (.NET 8). It captures the complete raw HTTP request/response for every LLM API call and provides a persistent agent runtime with tool execution, safety policies, and sandboxing. Every run is stored locally — raw provider requests/responses, agent steps, tool calls, approvals, artifacts, checkpoints, and update metadata.

## Development Roadmap

The 4.8 → 5.x evolution plan lives in `docs/TLAH_5_0_ROADMAP.md` (entry point), backed by four phase plans: Phase 0 foundation fixes (4.8.0), Phase 1 agent autonomy (4.9.0), Phase 2 multi-agent orchestration (5.0.0), Phase 3 platform & differentiation (5.1+). Each task carries current-state code refs, Claude Code benchmarks (`_analysis/claude-code-src/`), implementation steps, and acceptance criteria. **Read the roadmap before starting any version work** — it defines priorities, cross-phase dependencies, and the four differentiating strengths (CJK token estimation, deterministic session memory, raw HTTP capture, GUI visualization) that must not be weakened by Claude Code alignment work.

## Coding Style & Conventions

- **C#** with nullable reference types and implicit usings enabled. File-scoped namespaces. Four-space indentation.
- **Async methods** must end in `Async` (e.g. `SendMessageAsync`).
- **Immutable DTOs**: prefer `record` types for data transfer objects (e.g. `AgentRunState`, `AgentRunFrame`, `ToolEffectPlan`).
- **WinUI views and view models** pair by name: `ChatPage.xaml` with `ChatPageViewModel`, `SettingsContentDialog.xaml` with `SettingsDialogViewModel`.
- **LLM providers** must use direct `HttpClient` calls (never official SDKs) so raw HTTP capture remains intact.
- **Tests** are named `MethodOrFeature_Condition_ExpectedResult` and live in `TLAHStudio.Core.Tests/` as `*Tests.cs`. Guard Windows-only behavior with `OperatingSystem.IsWindows()`.

## Build & Development Commands

```bash
# Restore dependencies
dotnet restore

# Build (Debug)
dotnet build TLAHStudio.sln -c Debug

# Build App project (Release, x64)
dotnet build TLAHStudio.App/TLAHStudio.App.csproj -c Release -p:Platform=x64

# Run unit tests
dotnet test TLAHStudio.Core.Tests/TLAHStudio.Core.Tests.csproj -c Release

# Self-contained publish (produce standalone .exe)
dotnet publish TLAHStudio.App/TLAHStudio.App.csproj -c Release -r win-x64 --self-contained true

# CI quality gate
.\tools\ci.ps1 -Configuration Release -Platform x64

# Release build + sign + verify + upload
.\tools\build-release.ps1 -Version X.Y.Z -ReleaseNotes "<notes>" -CertificateThumbprint <thumbprint> -AllowUntrustedCertificate

# Verify an existing release
.\tools\verify-release.ps1 -Version X.Y.Z -AllowUntrustedAuthenticode

# Run a single test (filter by name)
dotnet test TLAHStudio.Core.Tests/TLAHStudio.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SkillLoaderV2Tests"
```

**Installer packaging:**
```bash
cd TLAHStudio.Installer && iscc setup.iss
```

Requires Visual Studio 2022+ with Windows App SDK / WinUI workloads. Open `TLAHStudio.sln` in VS for XAML Hot Reload. SDK pinned to **8.0.407** in `global.json`.

## Solution Architecture (5 projects)

| Project | Target | Framework | Purpose |
|---|---|---|---|
| **TLAHStudio.Core** | net8.0 | classlib | All business logic: LLM providers, agent runtime, tools, services, models, security |
| **TLAHStudio.Data** | net8.0 | classlib | EF Core `TlahDbContext` — single-file data layer with 20+ entity sets and lightweight SQLite migrations |
| **TLAHStudio.App** | net8.0-windows | WinUI 3 | Desktop app shell: XAML Views, ViewModels (MVVM with CommunityToolkit.Mvvm), DI via `Microsoft.Extensions.Hosting` |
| **TLAHStudio.Updater** | net8.0-windows | console | Standalone updater (single-file published): waits for main app exit → runs Inno Setup installer silently → relaunches |
| **TLAHStudio.Core.Tests** | net8.0 | xUnit | 295 tests covering LLM, agent runtime, tools, safety, persistence, update, privacy, release, foundation v2, permission modes |

**Platform targets:** x86, x64, ARM64 (App). Updater publishes as single-file with native self-extract + compression.

### Dependency chain
`App` → `Core` + `Data`. No direct `Core` ↔ `Data` coupling: `Core` defines interfaces and models, `Data` hosts the EF context using those models. `Core.Tests` references both `Core` and `Data`.

### Key NuGet packages
- **App**: `Microsoft.WindowsAppSDK` 2.1.3, `CommunityToolkit.Mvvm` 8.4.0, `Microsoft.Extensions.Hosting` 8.0.1
- **Core**: `Microsoft.EntityFrameworkCore.Sqlite` 8.0.28, `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3, `Microsoft.Extensions.Http` 8.0.1, `System.Security.Cryptography.ProtectedData` 8.0.0
- **Tests**: xUnit 2.5.3, `Microsoft.NET.Test.Sdk` 17.8.0, `coverlet.collector` 6.0.0

## Core Layer — Key Namespaces

### `TLAHStudio.Core.Llm`
LLM abstraction over raw `HttpClient`. No official SDKs — every request/response is captured at the HTTP layer.

- **`ILlmProvider`** — single interface: `ChatAsync(messages, systemPrompt, temperature, maxTokens, tools?, stream?, reasoning?, ct)` → `LlmResponse`
- **`OpenAICompatibleProvider`** — covers OpenAI, DeepSeek, and all `/v1/chat/completions` endpoints
- **`AnthropicProvider`** — Messages API with native tool calling
- **`LlmProviderFactory`** — resolves provider by name string
- **`ProviderModelResolver`** — `/v1/models` listing
- **`AssistantContentFormatter`** / **`MessageAttachmentFormatter`** — format assistant content and attachments for display

### `TLAHStudio.Core.Services`
Service layer; interfaces map 1:1 from the original Python codebase.

- **`ILlmService`** — THE central orchestration interface. Methods: `SendMessageAsync`, `RunAgentTaskAsync`, `ResumeAgentTaskAsync`, `CancelAgentRunAsync`, `RegenerateAssistantAsync`, `EditAndResendAsync`, `ContinueFromMessageAsync`, `ReplayTurnAsync`, `TestConnectionAsync`, `ListModelsAsync`
- **`IChatService`** — CRUD for chats: create, archive/pin, soft-delete, export JSON
- **`ISettingsService`** / **`IPrivacyService`** / **`IWorkspaceService`** / **`IUpdateService`**
- **`ISandboxCommandService`** — restricted local command execution
- **`IToolPlatformService`** — manages tool platform settings, MCP configs, credential entries, policy rules
- **`INetworkSecurityService`** — HTTPS allowlist, private/loopback blocking, redirect control
- **`IMcpClientService`** — MCP client (STDIO + Streamable HTTP)

### Permission Modes (`AgentPermissionModes.cs`)
Constants + helpers for the four permission modes selectable from the input bar flyout (M4.8.0/4.9.0):

- **`BypassPermissions`** (`bypass_permissions`) — full access, no prompts (dangerous)
- **`AutoApprove`** (`auto_approve`) — auto-approve every tool call
- **`RequestApproval`** (`request_approval`) — default fallback; prompt per call
- **`Plan`** (`plan`, M4.9.0) — read-only planning mode. Write tools are intercepted in the agent loop and rejected; agent must call `enter_plan_mode`/`exit_plan_mode` to transition. `IsAutoApprove` excludes Plan.

### Agent Autonomy Tools (M4.9.0)
Four new agent tools wired through DI in `App.xaml.cs`:

- **`enter_plan_mode` / `exit_plan_mode`** (`PlanModeAgentTools.cs`) — toggle Plan mode mid-run. State stored on `AgentRunState.IsPlanMode` + `PrePlanMode`; write tools intercepted at the engine tool-execution loop.
- **`ask_user_question`** (`AskUserQuestionAgentTool.cs`) — structured multi-question tool (1-4 questions, 2-4 options each, multi-select). Executes with answers collected from the approval flow; `AgentApprovalRequest.UpdatedArgumentsJson` carries answers back into the tool call.
- **`skill`** (`SkillAgentTool.cs`) — invoke a discovered skill by name. Lazy-loads the skill body on call; uses `ISkillLoader` for discovery.

### Skills System (M4.9.0)
Four-source skill discovery via `ISkillLoader` (stateless, always rescans — no caching). **M4.9.2 priority: project > managed > user > bundled** (first source wins on name collision):

1. **Project** (`<workspace>/.tlah/skills/`) — per-workspace skills; auto-discovered via `SetWorkspaceRoot()` at run start. `WorkspaceRootService.SetRootAsync` auto-creates `.tlah/skills/` and `.tlah/output-styles/`.
2. **Managed** (`SetManagedDir()`, M4.9.2) — policy-level skills; set by `PluginActivationService` to a trusted plugin's directory. Optional, null by default.
3. **User** (`%LOCALAPPDATA%/TLAH Studio/skills/`) — user-installed skills.
4. **Bundled** (`TLAHStudio.App/Assets/bundled-skills/`) — 12 skills: `init`, `code-review`, `verify`, `simplify`, `update-config`, `debug`, `remember`, `skillify`, `security-review`, `deep-research`, `batch`, `loop`. Never truncated by budget.

Skill frontmatter parsed with regex `@"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n"` (handles both CRLF and LF on Windows). List fields (`paths`, `triggers`, etc.) accept both comma-scalar form (`paths: a, b`) and YAML array form (`paths: ["a", "b"]`) — `SplitList` strips brackets/quotes (M4.9.2). Progressive disclosure: skill listings injected into the system prompt under a 1% context budget; `AgentRunState.SentSkillNames` (HashSet, deep-copied in `DeepClone`) tracks which skills have already been sent to avoid re-sending.

### Plugin Activation (M4.9.2)
`IPluginActivationService` (`PluginActivationService.cs`) — closes the M2.12.0 dead-code gap by wiring trusted plugins end-to-end at startup and on trust-toggle:
- **Skills**: the trusted plugin's directory is registered as a managed source on the shared `ISkillLoader` (via `SetManagedDir`), so plugin `skills/*.md` appear in the listing.
- **MCP servers**: each declared `mcp_servers` entry is persisted via `IToolPlatformService.SaveMcpServerAsync` (idempotent by name), picked up by the existing MCP startup flow.
- **Tools** (declared in `plugin.json` `tools`): schema-only in this phase; execution routes through the plugin's MCP server via `mcp_call`. Standalone dynamic registration is supported by `IAgentToolRegistry.Register/Unregister` (built-in names protected from overwrite) but not yet wired for plugins pending registry unification across DI and LlmService self-built paths.
Activated by `ActivateAllAsync()` at app startup (`App.xaml.cs`) and on trust-toggle/rescan in `ToolPlatformDialog`.

### Output Styles (M4.9.0, priority fixed M4.9.2)
`IOutputStyleService` (`OutputStyleService.cs`) — three built-in styles (`default`, `Explanatory`, `Learning`) plus custom `.md` loading. **M4.9.2 priority: project > user > built-in** (project `.tlah/output-styles/` overrides user `%LOCALAPPDATA%/TLAH Studio/output-styles/` overrides built-ins). Selected style appended to the end of the system prompt. Descriptions are agent-agnostic (no "Claude" references). Stored in `GlobalSettings.OutputStyle`.

### Context Management Compaction Chain (M4.8.0)
`ReactiveCompactor` progressive upgrade chain: `TrimToolOutputs` → `Microcompact` → `SummarizeMiddle` → `ModelAssistedSummarize` → `EmergencyTruncate`. Key 4.8.0 fixes:

- **Context-limit retry circuit breaker**: on hitting context limit, marks `AgentRunState.CompactionDisabled` instead of aborting the run.
- **PTL (prompt-too-long) truncation**: drops oldest API-rounds up to 3 times before failing.
- **Post-compact tools summary**: `BuildPostCompactToolsSummary` injects a recap of available tools after compaction so the model remembers its toolset.
- **Session memory throttling**: init/update thresholds prevent writing session-memory every step.
- **Microcompact** references use `ToolCallId` instead of synthetic filenames.

### Agent Runtime (`TLAHStudio.Core.Services.AgentRuntime`)
Extracted v2.7.0+ state machine from `LlmService`:

- **`IAgentRunEngineV2`** — owns the agent while-loop; `RunAsync`/`ResumeAsync` emit `AgentRunFrame` items for UI/SDK
- **`AgentRunState`** — complete runtime state (immutable `record`): messages, step counter, token budget, compaction tracking; supports `DeepClone()`
- **`AgentRunFrame`** — per-step frame emitted during execution (step number, kind, events, tool states)
- **`AgentRunResult`** — final output: state, assistant content, last LLM response, events
- **`IAgentEventStream`** — buffered event stream with `AsyncLocal` scoping for UI progress and SDK replay
- **`IAgentEventSubscriptionService`** — subscribe to events by run ID for SDK consumers

Agent pipeline: **Run → Steps** (model or tool steps) → **Tool Invocations** (individual calls with approval gates) → **Checkpoints** after each step → **Artifacts** (sandbox output files: hash, size, type) → **Events** streamed to UI.

### Tool Architecture V3 (`TLAHStudio.Core.Services.Tools`)
The tool system (v3.0+) uses per-tool safety classification and effect planning:

- **`IAgentToolV3`** — extends `IAgentTool` with `ClassifySafetyAsync`, `PlanEffectsAsync`, `ExecuteWithProgressAsync`, `CreateRollbackPlanAsync`
- **`IToolLifecycleRunner`** — orchestrates `PreviewAsync` (safety classify + effect plan) and `ExecuteAsync` (through the full pipeline)
- **`ToolEffectPlan`** — predicted effects: paths, domains, commands, network targets
- **`ToolRollbackPlan`** — rollback instructions for reversible operations

### Tool Hook Pipeline (`TLAHStudio.Core.Services.Tools`)
- **`IToolHookRegistry`** — register hooks for `BeforeUse`, `AfterUse`, `AfterFailedUse` triggers
- **`ToolHookContext`** — context passed to hooks (chat ID, run ID, tool name, arguments, result, effect plan)
- **`ToolHookResult`** — allow/block + optional modified arguments

### Tool Safety Pipeline
Tool calls go through: `AgentToolParser` (parse from model output) → `IAgentToolV3.ClassifySafetyAsync` (per-tool assessment) → `ToolSafetyKernel` (pre-execution assess) → `ToolProtocolGuard` (approval gate, user prompts) → `IToolLifecycleRunner.ExecuteAsync` → `ISandboxCommandService` (actual execution with resource limits).

Additional safety mechanisms layered on the pipeline:
- **`IFlagLevelValidationService`** (`FlagLevelValidationService`, M4.6.0) — per-flag command allowlist adopted from Claude Code's `readOnlyValidation.ts` / `bashSecurity.ts`. Parses shell commands into tokens, matches against a per-command safe-flag map, and returns `Allow` (auto-approve) / `NotInAllowlist` (defer to other checks) / `Reject`. Also exposes `DestructiveWarnings` (informational, never blocks — e.g. `git reset --hard`, `git push --force`, `rm -rf`) the UI can surface.
- **`ReadFileTracker`** — read-before-write guard: blocks/flags `file_write` / `edit` against paths the agent has not first read in the current run, with stale-mtime detection.
- **`ToolSafetyKernel` bypass-immune paths** — `.git/`, `.env`, and shell config files are checked via `CheckBypassImmunePath()` / `BypassImmunePathRegex()` and cannot be circumvented by flags like `--no-verify` or env-var overrides.

### Built-in Tools (`AgentToolNames` registry)
`terminal_exec` (sandbox), `file_list`, `file_read`, `file_write`, `file_search`, `git`, `http_request`, `web_search`, `browser_read`, `mcp_list_tools`, `mcp_call`, `memory_read`, `memory_write`, `memory_list`, and code tools: `read`, `grep`, `glob`, `edit`, `multi_edit`, `diff`, `apply_patch`, `rollback`, `lsp_diagnostics`. Also: `task_create`, `task_update`, `task_list`, `task_output`, `task_stop`, `task_send_message`, `todo_write`. Agent-autonomy tools (M4.9.0): `enter_plan_mode`, `exit_plan_mode`, `ask_user_question`, `skill`. Metadata wiring in `AgentToolMetadata.For` (switch expression); standalone arms must not be chained with `or` or other arms' cases leak into the wrong branch.

### Execution Backends (`ExecutionBackends.cs`)
- `restricted_local` — always available, subprocess sandbox
- `wsl` — routes through `wsl.exe` bash
- `docker` — `docker run --rm --network none` with resource limits
- `remote` — HTTP POST to remote sandbox endpoint with bearer auth

### Context Management (`TLAHStudio.Core.Services.Context`)
- **`IReactiveCompactor`** — progressive compaction chain (see "Context Management Compaction Chain (M4.8.0)" above for the full upgrade sequence and 4.8.0 fixes). Model-assisted summarization activated via `IModelAssistedCompactor`.
- **`ITokenBudgetService`** — tracks token usage, availability, and budget ceiling
- After compaction, injects a structured runtime context block with project memory, active tasks, recent files, and open questions.

### Session Memory (`TLAHStudio.Core.Services.SessionMemory`)
- **`ISessionMemoryService`** — cross-compaction persistent memory that prevents catastrophic forgetting during long agent runs. After each step, deterministic extraction (zero API cost — unlike Claude Code's LLM-based approach) writes a structured markdown file to `{sandbox}/.tlah_context/session-memory.md` covering files changed, commands run, recent failures, open questions, and next actions. When compaction fires, the file is read and injected at the summary boundary so accumulated context survives multiple compaction cycles.

### Other Subsystems
- **`TLAHStudio.Core.Services.Lsp`** — `ILspManager`: language server diagnostics for C#, TypeScript, Python, Rust
- **`TLAHStudio.Core.Services.Memory`** — `IMemoryDirectoryService` + `MemoryToolsV3` (list, read, write) for per-project persistent memory
- **`TLAHStudio.Core.Services.Observability`** — `IRuntimeMetricsCollector`: first-thinking/text latency, tokens/sec, render backlog
- **`TLAHStudio.Core.Services.Plugins`** — `IPluginManifestService`: plugin discovery, trust levels (Untrusted/Trusted/Partial)
- **`TLAHStudio.Core.Services.Sdk`** — `ILocalSdkHost`: local HTTP server + named pipe for programmatic agent access
- **`TLAHStudio.Core.Services.Workspace`** — `IWorkspaceRootService`: resolve workspace root per chat/sandbox
- **`TLAHStudio.Core.Services.Background`** — `IBackgroundTaskService`: background agent tasks with output files and mailbox
- **`TLAHStudio.Core.Services.RecoveryService`** — crash recovery for interrupted agent runs

### Security
- **`SecretRedactor`** / **`ApiKeyMasker`** — strips secrets from debug logs and stored data
- **`ProtectedSecret`** — DPAPI-based credential encryption (`System.Security.Cryptography.ProtectedData`)
- **`ToolProtocolGuard`** / **`ToolSafetyKernel`** — pre-execution safety assessment
- **`UpdateCrypto`** — ECDSA P-256 (nistP256) + SHA-256 detached signature verification for `latest.json`; signature is Base64-encoded into `latest.json.sig` (keys generated via `tools/generate-keys.ps1`)

## App Layer — MVVM + WinUI 3

The app uses `Microsoft.Extensions.Hosting` for DI, `CommunityToolkit.Mvvm` for MVVM infrastructure. The `Services` helper class in `ViewModels/` acts as a service locator for ViewModel access.

### Views & ViewModels (pairing)
| View (`.xaml`) | ViewModel | Purpose |
|---|---|---|
| `MainWindow` | `MainViewModel` | Shell: navigation, window management, onboarding |
| `SidebarPage` | `SidebarViewModel` | Chat list, projects, pinned/archived, search |
| `ChatPage` | `ChatPageViewModel` | Main chat UI: message list, agent streaming, tool approval prompts, agent mode toggle |
| `MessageInputControl` | — (in `ChatPageViewModel`) | Message compose, file attachments, agent mode toggle |
| `ChatHeaderControl` | — (in `ChatPageViewModel`) | Chat title, config profile selector, agent run controls, context usage display |
| `AgentActivityPanelControl` | — | Historical agent run replay panel (right side) |
| `DebugPanelControl` | `DebugPanelViewModel` | Raw request/response JSON inspection |
| `SettingsContentDialog` | `SettingsDialogViewModel` | LLM provider config, API keys, model selection, OutputStyle picker, Skills management (3 Open buttons: Bundled/User/Project + Reload + scrollable skills list) |
| `ToolPlatformDialog` | `ToolPlatformViewModel` | MCP servers, execution backend, network/policy/credential/limit config, Plugin management (ListView + Open/Rescan, above Permission rules) |
| `FirstRunSetupDialog` | — (in `MainViewModel`) | Onboarding wizard |
| `TeamWorkspaceDialog` | `TeamWorkspaceViewModel` | Project spaces (Workspaces) |
| `PrivacyDataDialog` | `PrivacyDataViewModel` | Audit log, privacy export |
| `AgentFileDialog` | `AgentFileDialogViewModel` | Per-chat agent context file editing |
| `UpdateNotificationDialog` | `UpdateNotificationViewModel` | Update available notification |
| `BackgroundSettingsDialog` | `BackgroundSettingsDialogViewModel` | Background image settings |
| `AboutReleaseDialog` | — | Version info |

### UI Models
- **`ChatMessageBlock`** — structured message block for rendering (text, tool call, tool result, thinking)
- **`ChatRenderer`** — converts raw messages to renderable blocks for the WinUI ListView

## Data Layer

`TlahDbContext` (EF Core with SQLite) at `%LOCALAPPDATA%\TLAH Studio\data\tlah.db`. Uses `Database.EnsureCreated()` on startup + an `ApplyLightweightMigrations()` method that adds missing columns and creates new tables via raw SQL (`ALTER TABLE ADD COLUMN` / `CREATE TABLE IF NOT EXISTS`). No proper EF Core migrations — forward-compatible only, no rollbacks. Entity models are in `TLAHStudio.Core.Models`.

Key entities: `Chat` → `Message` + `Turn` + `AgentRun` + `ProjectSpace` + `ConfigProfile` + `GlobalSettings` (singleton seed) + `ChatSettings` + `AuditLogEntry` + `PromptTemplate` + `ToolPolicyRule` + `McpServerConfig` + `CredentialEntry` + `ToolPlatformSettings` + `AgentTaskItem` + `BackgroundTask`.

## Configuration

- `TLAHStudio.App/appsettings.json` — build-time config (app name, version, update server URL)
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `GlobalSettings` table — runtime config (LLM provider, API key, model, OutputStyle, etc.)
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `ToolPlatformSettings` table — tool platform config
- `%LOCALAPPDATA%\TLAH Studio\config\` — local UI and workspace configuration
- `%LOCALAPPDATA%\TLAH Studio\sandboxes\` — private chat sandboxes when no workspace is selected
- `.tlah_context/session-memory.md` persisted by `ISessionMemoryService` at each step.
- `.tlah_context/tool-results/` — persisted large tool outputs inside a workspace/sandbox
- `%LOCALAPPDATA%\TLAH Studio\skills\` — user-level skills (created on first skill install)
- `<workspace>/.tlah/skills/` and `<workspace>/.tlah/output-styles/` — per-workspace directories, auto-created when `WorkspaceRootService.SetRootAsync` is called.
- `appsettings.Development.json` — gitignored; local overrides only

## Update & Deployment Architecture

Updates are delivered through `https://download.matrixlabs.cn/tlah/windows/`. The chain:

1. **Build**: `dotnet publish` + Inno Setup → `TLAHStudioSetup-x.y.z.exe`
2. **Metadata**: `latest.json` contains version, channel, `installerUrl`, SHA256, `forceUpdate`, `minSupportedVersion`, `rolloutPercent` (0-100 for canary)
3. **Sign**: `latest.json` is ECDSA P-256 + SHA-256 signed → detached Base64 `latest.json.sig` (keys via `tools/generate-keys.ps1`)
4. **Upload**: installer, `latest.json`, `latest.json.sig` to update server
5. **Client**: On startup, fetches `latest.json` → verifies its ECDSA P-256 signature → checks version → downloads installer → SHA256 verify → launches `TLAHStudio.Updater.exe` → main app exits → silent Inno Setup install → relaunches new version

Rollout uses install-ID-based hashing for canary percentages. Force-update when `forceUpdate: true` or `minSupportedVersion` exceeds client version.

## Tools Scripts

| Script | Purpose |
|---|---|
| `tools/ci.ps1` | Unit tests + Release build |
| `tools/verify-release.ps1` | Validate installer: `latest.json` signature, SHA256, Authenticode, optional smoke install |
| `tools/build-release.ps1` | Full release pipeline: build, sign, verify, smoke test, upload |
| `tools/deploy.ps1` | Deploy to update server via SCP |
| `tools/sign-latest.ps1` | Sign `latest.json` with an ECDSA P-256 private key |
| `tools/sign-authenticode.ps1` | Apply Authenticode signature to installer |
| `tools/generate-keys.ps1` | Generate an ECDSA P-256 key pair for update signing |

## Version Conventions

Version is stored in multiple places and must be kept in sync:
- `TLAHStudio.App/appsettings.json` → `App.Version`
- Each `.csproj` → `<Version>`, `<FileVersion>`, `<AssemblyVersion>`, `<InformationalVersion>`
- `TLAHStudio.Installer/version.json` and `TLAHStudio.Installer/latest.json`
- `setup.iss` → `#define MyAppVersion`

Semantic versioning (`Major.Minor.Patch`). Current: **4.12.0**. This release combines Activity lifecycle, theme, scaling, and responsive-pane stability with reduced-motion-aware artistic interactions and long-list scrolling improvements. The 5.0.0 Phase 2 (multi-agent orchestration) and 5.1+ Phase 3 (platform & differentiation) plans are the next milestones — see `docs/TLAH_5_0_PHASE2_ORCHESTRATION.md` and `docs/TLAH_5_1_PHASE3_PLATFORM.md`.

## Key Architectural Patterns

1. **Raw HTTP capture**: The entire LLM debug premise depends on `ILlmProvider` implementations using `HttpClient` directly (not official SDKs). Every request/response is serialized to `RawRequest`/`RawResponse` and stored in the DB.

2. **Streaming + persistence duality**: `ILlmService.SendMessageAsync` takes an optional `IProgress<LlmStreamUpdate>` for real-time UI streaming, while also buffering the full response for DB persistence.

3. **Agent tool safety pipeline**: `AgentToolParser` (parse) → `IAgentToolV3.ClassifySafetyAsync` (per-tool classify) → `ToolSafetyKernel` (assess) → `ToolProtocolGuard` (approval gate) → `IToolLifecycleRunner` (execute with hooks + progress) → `ISandboxCommandService` (resource-limited execution).

4. **Tool lifecycle with hooks**: `ToolHookRegistry` supports `BeforeUse`/`AfterUse`/`AfterFailedUse` triggers. `IToolLifecycleRunner` wraps the full execution lifecycle including preview (safety + effect plan), execution, and optional rollback.

5. **EF Core with lightweight migrations**: No proper EF Core migrations. Schema evolution in `ApplyLightweightMigrations()` via raw SQL — forward-compatible only, no rollbacks.

6. **DI host pattern**: The App project uses `Microsoft.Extensions.Hosting` with `ConfigureServices`. Services registered as singletons or scoped; `Services` helper class in ViewModels acts as service locator.

7. **Agent run engine as extracted state machine**: `AgentRunEngineV2` owns the while-loop, emitting typed `AgentRunFrame` records consumed by both WinUI and the local SDK HTTP server. State is an immutable `AgentRunState` record with explicit `DeepClone()` for safe mutation.

8. **Progressive context compaction**: `ReactiveCompactor` tries `TrimToolOutputs` → `Microcompact` → `SummarizeMiddle` before the deterministic fallback. After compaction, injects structured runtime context (project memory, active tasks, recent files, open questions).

9. **Plan mode interception (M4.9.0)**: In Plan mode, write tools are intercepted at the agent tool-execution loop and rejected; the agent must call `enter_plan_mode`/`exit_plan_mode` to transition. State persists across the run via `AgentRunState.IsPlanMode` + `PrePlanMode`.

10. **Stateless multi-source skill loading (M4.9.0)**: A single shared `SkillLoader` instance is created in `LlmService` and injected into both `AgentRunEngineV2` and `SkillAgentTool` (never three separate instances). It is fully stateless — `ReloadAsync` and every listing rescan the filesystem — so cache staleness cannot block newly added skills. Frontmatter regex handles both CRLF and LF.

11. **Approval flow with structured answers (M4.9.0)**: `ask_user_question` surfaces a custom WinUI multi-select dialog via `OnAgentApprovalRequested` in `MainWindow`; collected answers are passed back into the tool call through `AgentApprovalRequest.UpdatedArgumentsJson` → `SetAgentToolApprovalAsync(updatedArgumentsJson:)`.
