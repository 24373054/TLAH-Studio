# TLAH Studio

Talk Like A Human — a native Windows AI agent workspace built with C#, WinUI 3, and Windows App SDK.

TLAH Studio combines chat, tool execution, MCP integrations, prompt/debug inspection, and a persistent activity timeline in one desktop app. Every run is stored locally, including raw provider requests and responses, agent steps, tool calls, approvals, artifacts, checkpoints, and update metadata.

## Current Capabilities

- Agent mode is available directly from the input bar and is enabled by default.
- Permission modes mirror coding-agent workflows: Full access, Auto approve, and Ask.
- Each chat can run in a selected workspace folder, with a private sandbox fallback.
- Built-in tools cover files, code reading/editing, Git, terminal execution, web search, browser/page reads, HTTP requests, memory, todos, background tasks, and MCP calls.
- MCP supports STDIO and Streamable HTTP servers with tool/resource discovery.
- Long agent runs persist checkpoints, progress events, artifacts, rollback metadata, and stopped-run records.
- The right-side Agent Activity panel replays historical runs after completion or cancellation.
- Chat headers show context usage, including conversation, tools, MCP, execution results, files, and total budget.
- Debug tooling captures raw HTTP request/response data for provider troubleshooting.
- Updates are delivered through signed `latest.json` metadata and Windows installer packages.

## Project Structure

- `TLAHStudio.App/` — WinUI desktop app, views, dialogs, and view models.
- `TLAHStudio.Core/` — LLM orchestration, agent runtime, tools, MCP, security, update, and workspace services.
- `TLAHStudio.Data/` — EF Core data model and SQLite context.
- `TLAHStudio.Updater/` — standalone updater executable.
- `TLAHStudio.Installer/` — Inno Setup installer script, release metadata, and generated output.
- `TLAHStudio.Core.Tests/` — core runtime, tool, release, and regression tests.
- `tools/` — CI, release build, signing, verification, and deployment scripts.
- `docs/` — development plans and architecture notes.

## Development

Requirements:

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 with Windows App SDK / WinUI workload
- Inno Setup 6 for installer builds

Common commands:

```powershell
dotnet restore
dotnet build TLAHStudio.sln -c Debug
dotnet test TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Debug
.\tools\ci.ps1 -Configuration Release -Platform x64
```

Open `TLAHStudio.sln` in Visual Studio for XAML Hot Reload and desktop debugging.

## Release

Build, sign, verify, and upload a release:

```powershell
.\tools\build-release.ps1 `
  -Version 4.3.1 `
  -ReleaseNotes "<release notes>" `
  -CertificateThumbprint F6DC173C746447A05FF83B9F7162121344CC09F0 `
  -AllowUntrustedCertificate `
  -ForceSmokeTest `
  -Upload
```

The current self-signed Authenticode certificate is:

```text
CN="Beijing Ke Entropy Technology Co., Ltd., O=Beijing Ke Entropy Technology Co., Ltd., C=CN"
Thumbprint: F6DC173C746447A05FF83B9F7162121344CC09F0
```

Verify an existing release:

```powershell
.\tools\verify-release.ps1 -Version 4.3.1 -AllowUntrustedAuthenticode
```

Generated files:

- `TLAHStudio.Installer/output/TLAHStudioSetup-x.y.z.exe`
- `TLAHStudio.Installer/latest.json`
- `TLAHStudio.Installer/latest.json.sig`

## Local Data

- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` — SQLite app data.
- `%LOCALAPPDATA%\TLAH Studio\config\` — local UI and workspace configuration.
- `%LOCALAPPDATA%\TLAH Studio\sandboxes\` — private chat sandboxes when no workspace is selected.
- `.tlah_context/tool-results/` — persisted large tool outputs inside a workspace/sandbox.

API keys are protected locally with Windows facilities and should never be committed or exported.
