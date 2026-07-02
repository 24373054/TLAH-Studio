# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TLAH Studio (Talk Like A Human) is a Windows-native AI agent workspace built on C# + WinUI 3 + Windows App SDK (.NET 8). It captures the complete raw HTTP request/response for every LLM API call and provides a persistent agent runtime with tool execution, safety policies, and sandboxing. Every run is stored locally — raw provider requests/responses, agent steps, tool calls, approvals, artifacts, checkpoints, and update metadata.

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
.\tools\build-release.ps1 -Version 4.4.0 -ReleaseNotes "<notes>" -CertificateThumbprint <thumbprint> -AllowUntrustedCertificate -ForceSmokeTest -Upload

# Verify an existing release
.\tools\verify-release.ps1 -Version 4.4.0 -AllowUntrustedAuthenticode
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
| **TLAHStudio.Core.Tests** | net8.0 | xUnit | ~30 test classes covering LLM, agent runtime, tools, safety, persistence, update, privacy, release |

**Platform targets:** x86, x64, ARM64 (App). Updater publishes as single-file with native self-extract + compression.

### Dependency chain
`App` → `Core` + `Data`. No direct `Core` ↔ `Data` coupling: `Core` defines interfaces and models, `Data` hosts the EF context using those models. `Core.Tests` references both `Core` and `Data`.

### Key NuGet packages
- **App**: `Microsoft.WindowsAppSDK` 2.1.3, `CommunityToolkit.Mvvm` 8.4.0, `Microsoft.Extensions.Hosting` 8.0.1
- **Core**: `Microsoft.EntityFrameworkCore.Sqlite` 8.0.14, `Microsoft.Extensions.Http` 8.0.1, `System.Security.Cryptography.ProtectedData` 8.0.0
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

### Built-in Tools (`AgentToolNames` registry)
`terminal_exec` (sandbox), `file_list`, `file_read`, `file_write`, `file_search`, `git`, `http_request`, `web_search`, `browser_read`, `mcp_list_tools`, `mcp_call`, `memory_read`, `memory_write`, `memory_list`, and code tools: `read`, `grep`, `glob`, `edit`, `multi_edit`, `diff`, `apply_patch`, `rollback`, `lsp_diagnostics`. Also: `task_create`, `task_update`, `task_list`, `task_output`, `task_stop`, `task_send_message`, `todo_write`.

### Execution Backends (`ExecutionBackends.cs`)
- `restricted_local` — always available, subprocess sandbox
- `wsl` — routes through `wsl.exe` bash
- `docker` — `docker run --rm --network none` with resource limits
- `remote` — HTTP POST to remote sandbox endpoint with bearer auth

### Context Management (`TLAHStudio.Core.Services.Context`)
- **`IReactiveCompactor`** — progressive compaction: `TrimToolOutputs` → `Microcompact` → `SummarizeMiddle` → `ModelAssistedSummarize` → `EmergencyTruncate`
- **`ITokenBudgetService`** — tracks token usage, availability, and budget ceiling
- After compaction, injects a structured runtime context block with project memory, active tasks, recent files, and open questions.

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
- **`UpdateCrypto`** — RSA signature verification for `latest.json` (keys generated via `tools/generate-keys.ps1`)

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
| `SettingsContentDialog` | `SettingsDialogViewModel` | LLM provider config, API keys, model selection |
| `ToolPlatformDialog` | `ToolPlatformViewModel` | MCP servers, execution backend, network/policy/credential/limit config |
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
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `GlobalSettings` table — runtime config (LLM provider, API key, model, etc.)
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `ToolPlatformSettings` table — tool platform config
- `%LOCALAPPDATA%\TLAH Studio\config\` — local UI and workspace configuration
- `%LOCALAPPDATA%\TLAH Studio\sandboxes\` — private chat sandboxes when no workspace is selected
- `.tlah_context/tool-results/` — persisted large tool outputs inside a workspace/sandbox
- `appsettings.Development.json` — gitignored; local overrides only

## Update & Deployment Architecture

Updates are delivered through `https://download.matrixlabs.cn/tlah/windows/`. The chain:

1. **Build**: `dotnet publish` + Inno Setup → `TLAHStudioSetup-x.y.z.exe`
2. **Metadata**: `latest.json` contains version, channel, `installerUrl`, SHA256, `forceUpdate`, `minSupportedVersion`, `rolloutPercent` (0-100 for canary)
3. **Sign**: `latest.json` is RSA-signed → `latest.json.sig` (keys via `tools/generate-keys.ps1`)
4. **Upload**: installer, `latest.json`, `latest.json.sig` to update server
5. **Client**: On startup (3s delay), fetches `latest.json` → verifies RSA signature → checks version → downloads installer → SHA256 verify → launches `TLAHStudio.Updater.exe` → main app exits → silent Inno Setup install → relaunches new version

Rollout uses install-ID-based hashing for canary percentages. Force-update when `forceUpdate: true` or `minSupportedVersion` exceeds client version.

## Tools Scripts

| Script | Purpose |
|---|---|
| `tools/ci.ps1` | Unit tests + Release build |
| `tools/verify-release.ps1` | Validate installer: `latest.json` signature, SHA256, Authenticode, optional smoke install |
| `tools/build-release.ps1` | Full release pipeline: build, sign, verify, smoke test, upload |
| `tools/deploy.ps1` | Deploy to update server via SCP |
| `tools/sign-latest.ps1` | Sign `latest.json` with RSA private key |
| `tools/sign-authenticode.ps1` | Apply Authenticode signature to installer |
| `tools/generate-keys.ps1` | Generate RSA key pair for update signing |

## Version Conventions

Version is stored in multiple places and must be kept in sync:
- `TLAHStudio.App/appsettings.json` → `App.Version`
- Each `.csproj` → `<Version>`, `<FileVersion>`, `<AssemblyVersion>`, `<InformationalVersion>`
- `TLAHStudio.Installer/version.json` and `TLAHStudio.Installer/latest.json`
- `setup.iss` → `#define MyAppVersion`

Semantic versioning (`Major.Minor.Patch`). Current: **4.4.0**.

## Key Architectural Patterns

1. **Raw HTTP capture**: The entire LLM debug premise depends on `ILlmProvider` implementations using `HttpClient` directly (not official SDKs). Every request/response is serialized to `RawRequest`/`RawResponse` and stored in the DB.

2. **Streaming + persistence duality**: `ILlmService.SendMessageAsync` takes an optional `IProgress<LlmStreamUpdate>` for real-time UI streaming, while also buffering the full response for DB persistence.

3. **Agent tool safety pipeline**: `AgentToolParser` (parse) → `IAgentToolV3.ClassifySafetyAsync` (per-tool classify) → `ToolSafetyKernel` (assess) → `ToolProtocolGuard` (approval gate) → `IToolLifecycleRunner` (execute with hooks + progress) → `ISandboxCommandService` (resource-limited execution).

4. **Tool lifecycle with hooks**: `ToolHookRegistry` supports `BeforeUse`/`AfterUse`/`AfterFailedUse` triggers. `IToolLifecycleRunner` wraps the full execution lifecycle including preview (safety + effect plan), execution, and optional rollback.

5. **EF Core with lightweight migrations**: No proper EF Core migrations. Schema evolution in `ApplyLightweightMigrations()` via raw SQL — forward-compatible only, no rollbacks.

6. **DI host pattern**: The App project uses `Microsoft.Extensions.Hosting` with `ConfigureServices`. Services registered as singletons or scoped; `Services` helper class in ViewModels acts as service locator.

7. **Agent run engine as extracted state machine**: `AgentRunEngineV2` owns the while-loop, emitting typed `AgentRunFrame` records consumed by both WinUI and the local SDK HTTP server. State is an immutable `AgentRunState` record with explicit `DeepClone()` for safe mutation.

8. **Progressive context compaction**: `ReactiveCompactor` tries `TrimToolOutputs` → `Microcompact` → `SummarizeMiddle` before the deterministic fallback. After compaction, injects structured runtime context (project memory, active tasks, recent files, open questions).
