# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

TLAH Studio (Talk Like A Human) is a Windows-native Prompt debugging framework and agent runtime, built on C# + WinUI 3 + Windows App SDK (.NET 8). It captures the complete raw HTTP request/response for every LLM API call and provides a persistent agent runtime with tool execution, safety policies, and sandboxing.

## Build & Development Commands

```bash
# Restore dependencies
dotnet restore

# Build (Release)
dotnet build -c Release

# Self-contained publish (produce standalone .exe)
dotnet publish TLAHStudio.App/TLAHStudio.App.csproj -c Release -r win-x64 --self-contained true

# Run unit tests
dotnet test -c Release
```

**CI quality gate:**
```powershell
.\tools\ci.ps1                    # runs tests + Release build
.\tools\verify-release.ps1 -Version 2.6.0 -AllowUntrustedAuthenticode
```

**Installer packaging:**
```bash
cd TLAHStudio.Installer && iscc setup.iss
```

Requires Visual Studio 2022+ with Windows App SDK workloads. Open `TLAHStudio.sln` in VS for XAML Hot Reload. The SDK version is pinned to **8.0.407** in `global.json`.

## Solution Architecture (5 projects)

| Project | Type | Purpose |
|---|---|---|
| **TLAHStudio.Core** | net8.0 classlib | All business logic: LLM providers, agent runtime, tools, services, models, security |
| **TLAHStudio.Data** | net8.0 classlib | EF Core `TlahDbContext` — single-file data layer with 20+ entity sets and lightweight SQLite migrations |
| **TLAHStudio.App** | net8.0-windows WinUI 3 | Desktop app shell: XAML Views, ViewModels (MVVM with CommunityToolkit.Mvvm), dependency injection via `Microsoft.Extensions.Hosting` |
| **TLAHStudio.Updater** | net8.0-windows console | Standalone updater: waits for main app exit → runs Inno Setup installer silently → relaunches |
| **TLAHStudio.Core.Tests** | net8.0 xUnit | ~20 test classes covering LLM, agent runtime, tools, security, persistence |

### Dependency chain
`App` → `Core` + `Data` (no direct `Core` ↔ `Data` coupling; `Core` defines the interfaces and models, `Data` hosts the EF context using those models)

## Core Layer — Key namespaces

### `TLAHStudio.Core.Llm`
LLM abstraction over raw `HttpClient`. No official SDKs are used — every request/response is captured at the HTTP layer.

- **`ILlmProvider`** — single interface: `ChatAsync(messages, systemPrompt, temperature, maxTokens, tools?, stream?, reasoning?, ct)` → `LlmResponse`
- **`OpenAICompatibleProvider`** — covers OpenAI, DeepSeek, and all `/v1/chat/completions` endpoints
- **`AnthropicProvider`** — Messages API with native tool calling
- **`LlmProviderFactory`** — resolves provider by name string
- **`ProviderModelResolver`** — `/v1/models` listing

### `TLAHStudio.Core.Services`
Service layer; all interfaces map 1:1 from the original Python codebase (`services/*.py`).

- **`ILlmService`** — THE central orchestration interface. Methods: `SendMessageAsync`, `RunAgentTaskAsync`, `ResumeAgentTaskAsync`, `CancelAgentRunAsync`, `RegenerateAssistantAsync`, `EditAndResendAsync`, `ContinueFromMessageAsync`, `ReplayTurnAsync`, `TestConnectionAsync`, `ListModelsAsync`
- **`IChatService`** — CRUD for chats: create, archive/pin, soft-delete, export JSON
- **`ISettingsService`** / **`IPrivacyService`** / **`IWorkspaceService`** / **`IUpdateService`**
- **`ISandboxCommandService`** — restricted local command execution
- **`IToolPlatformService`** — manages tool platform settings, MCP configs, credential entries, policy rules
- **`INetworkSecurityService`** — HTTPS allowlist, private/loopback blocking, redirect control
- **`IMcpClientService`** — MCP client (STDIO + Streamable HTTP)

### Agent Runtime (Core Layer)
The agent runtime (introduced v1.3, upgraded v1.4) is a multi-step LLM-in-the-loop system:

- **Agent Run** → **Steps** (model steps or tool steps) → **Tool Invocations** (individual tool calls with approval gates)
- **Checkpoints** persist state after each step for pause/resume
- **Artifacts** register sandbox output files (hash, size, type) for privacy export/audit
- **Events** stream (buffered, with `AsyncLocal` scoping via `IAgentEventStream`) for UI progress
- **`ToolExecutionScheduler`** plans batches: concurrent for read-only/idempotent tools, sequential for mutating tools
- **`ToolSafetyKernel`** assesses every tool call before execution (block/allow + safety level)

Key types: `AgentRun`, `AgentStep`, `ToolInvocation`, `AgentEvent`, `AgentCheckpoint`, `AgentArtifact`, `ToolPermission` — all in `AgentRuntimeModels.cs`.

### Built-in Tools (`AgentToolNames` registry)
`terminal_exec` (sandbox), `file_list`, `file_read`, `file_write`, `file_search`, `git`, `http_request`, `web_search`, `browser_read`, `mcp_list_tools`, `mcp_call`, `memory_read`, `memory_write`, and code tools: `read`, `grep`, `glob`, `edit`, `multi_edit`, `diff`, `apply_patch`, `rollback`, `lsp_diagnostics`.

### Execution Backends (`ExecutionBackends.cs`)
- `restricted_local` — always available, subprocess sandbox
- `wsl` — routes through `wsl.exe` bash
- `docker` — `docker run --rm --network none` with resource limits
- `remote` — HTTP POST to remote sandbox endpoint with bearer auth

### Security
- **`SecretRedactor`** / **`ApiKeyMasker`** — strips secrets from debug logs and stored data
- **`ProtectedSecret`** — DPAPI-based credential encryption (`System.Security.Cryptography.ProtectedData`)
- **`ToolProtocolGuard`** / **`ToolSafetyKernel`** — pre-execution safety assessment
- **`UpdateCrypto`** — RSA signature verification for `latest.json` (keys generated via `tools/generate-keys.ps1`)

## App Layer — MVVM + WinUI 3

The app uses `Microsoft.Extensions.Hosting` for DI, `CommunityToolkit.Mvvm` for MVVM infrastructure.

### Views & ViewModels (pairing)
| View (`.xaml`) | ViewModel | Purpose |
|---|---|---|
| `MainWindow` | `MainViewModel` | Shell: navigation, window management |
| `SidebarPage` | `SidebarViewModel` | Chat list, projects, pinned/archived, search |
| `ChatPage` | `ChatPageViewModel` | Main chat UI: message list, agent streaming, tool approval prompts |
| `MessageInputControl` | — (in `ChatPageViewModel`) | Message compose, file attachments, agent mode toggle |
| `ChatHeaderControl` | — (in `ChatPageViewModel`) | Chat title, config profile selector, agent run controls |
| `DebugPanelControl` | `DebugPanelViewModel` | Raw request/response JSON inspection |
| `SettingsContentDialog` | `SettingsDialogViewModel` | LLM provider config, API keys, model selection |
| `ToolPlatformDialog` | `ToolPlatformViewModel` | MCP servers, execution backend, network/policy/credential/limit config |
| `FirstRunSetupDialog` | — (in `MainViewModel`) | Onboarding wizard |
| `TeamWorkspaceDialog` | `TeamWorkspaceViewModel` | Project spaces (Workspaces) |
| `PrivacyDataDialog` | `PrivacyDataViewModel` | Audit log, privacy export |
| `AgentFileDialog` | `AgentFileDialogViewModel` | Per-chat agent file (context file) editing |
| `UpdateNotificationDialog` | `UpdateNotificationViewModel` | Update available notification |
| `BackgroundSettingsDialog` | `BackgroundSettingsDialogViewModel` | Background image settings |
| `AboutReleaseDialog` | — | Version info |

The `Services` class in `ViewModels/` provides app-layer service access (wrapping Core services).

## Data Layer

`TlahDbContext` (EF Core with SQLite) at `%LOCALAPPDATA%\TLAH Studio\data\tlah.db`. Uses `Database.EnsureCreated()` on startup + a `ApplyLightweightMigrations()` method that adds missing columns and creates new tables via raw SQL. Entity models are in `TLAHStudio.Core.Models`.

Key entities: `Chat` → `Message` + `Turn` + `AgentRun` + `ProjectSpace` + `ConfigProfile` + `GlobalSettings` (singleton seed) + `ChatSettings` + `AuditLogEntry` + `PromptTemplate` + `ToolPolicyRule` + `McpServerConfig` + `CredentialEntry`.

## Configuration

- `appsettings.json` — build-time config (app name, version, update server URL)
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `GlobalSettings` table — runtime config (LLM provider, API key, model, etc.)
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` / `ToolPlatformSettings` table — tool platform config

## Tools Scripts

| Script | Purpose |
|---|---|
| `tools/ci.ps1` | Unit tests + Release build |
| `tools/verify-release.ps1` | Validate installer: `latest.json` signature, SHA256, Authenticode, optional smoke install |
| `tools/build-release.ps1` | Full release pipeline |
| `tools/deploy.ps1` | Deploy to update server |
| `tools/sign-latest.ps1` | Sign `latest.json` with RSA private key |
| `tools/sign-authenticode.ps1` | Apply Authenticode signature to installer |
| `tools/generate-keys.ps1` | Generate RSA key pair for update signing |

## Version Conventions

Version is stored in multiple places and must be kept in sync:
- `appsettings.json` → `App.Version`
- Each `.csproj` → `<Version>`, `<FileVersion>`, `<AssemblyVersion>`
- `TLAHStudio.Installer/version.json` and `TLAHStudio.Installer/latest.json`
- `setup.iss` → `#define MyAppVersion`

## Key Architectural Patterns

1. **Raw HTTP capture**: The entire LLM debug premise depends on `ILlmProvider` implementations using `HttpClient` directly (not official SDKs). Every request/response is serialized to `RawRequest`/`RawResponse` and stored in the DB.

2. **Streaming + persistence duality**: `ILlmService.SendMessageAsync` takes an optional `IProgress<LlmStreamUpdate>` for real-time UI streaming, while also buffering the full response for DB persistence.

3. **Agent tool safety pipeline**: Tool calls go through `AgentToolParser` (parse from model output) → `ToolSafetyKernel` (pre-execution assess) → `ToolProtocolGuard` (approval gate, user prompts) → `ToolExecutionScheduler` (batch/sequence) → `ISandboxCommandService` (actual execution with resource limits).

4. **EF Core with lightweight migrations**: No proper EF Core migrations. Schema evolution happens in `TlahDbContext.ApplyLightweightMigrations()` via `ALTER TABLE ADD COLUMN` and `CREATE TABLE IF NOT EXISTS` — the intent is forward-compatible only, no rollbacks.

5. **DI host pattern**: The App project uses `Microsoft.Extensions.Hosting` with a `ConfigureServices` pattern. Services are registered as singletons or scoped; the `Services` helper class in ViewModels acts as a service locator for ViewModel access.
