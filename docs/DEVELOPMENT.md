# Development Guide

Verified against TLAH Studio 4.13.0.

## Prerequisites

- Windows 10 build 19041+ or Windows 11
- .NET 8 SDK; `global.json` requests `8.0.407` with `latestFeature` roll-forward
- Visual Studio 2022 with Windows App SDK / WinUI tools for XAML Hot Reload
- Git and PowerShell 7
- Inno Setup 6 and Windows SDK SignTool only for release packaging/signing

## Bootstrap

```powershell
git clone https://github.com/24373054/TLAH-Studio.git
cd TLAH-Studio
dotnet restore .\TLAHStudio.sln
dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Debug -p:Platform=x64
```

Launch `TLAHStudio.App` from Visual Studio for a reliable WinUI debug environment. The official release path is x64.

## Quality Commands

| Command | Purpose |
|---|---|
| `dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release` | Run all xUnit tests |
| `dotnet test ... --filter "FullyQualifiedName~UpdateCryptoTests"` | Run a focused test class |
| `dotnet test ... --filter "FullyQualifiedName~ToolAuthorizationPolicyTests"` | Run the centralized permission-matrix tests |
| `dotnet test ... --filter "FullyQualifiedName~AgentToolApprovalTests"` | Run exact-invocation approval and argument-validation tests |
| `.\tools\ci.ps1 -Configuration Release -Platform x64` | Restore, vulnerability audit, tests with Cobertura coverage, App build, Updater build |
| `dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Release -p:Platform=x64 --no-restore` | Compile the release desktop app only |

The CI gate must remain warning-free. Each run writes at least one
`artifacts/test-results/coverage/**/coverage.cobertura.xml` report and fails if
the collector produces no coverable lines. The test build enables portable
symbols and isolates its binaries under `artifacts/test-results/build`, leaving
release-package outputs unchanged. GitHub Actions retains the reports as the
`coverage-cobertura` artifact for 14 days. The gate does not currently enforce
a numeric code-coverage percentage.

## Style

- Four-space C# indentation; file-scoped namespaces; nullable reference types and implicit usings.
- PascalCase for types/public members, camelCase for locals/parameters, `_camelCase` for private fields, `I` prefix for interfaces.
- `Async` suffix for asynchronous methods and cancellation tokens for long-running operations.
- Immutable `record` types for data transfer and state snapshots when practical.
- View, code-behind, and view model names should align by feature.
- Keep UI event subscriptions symmetric across loaded/unloaded or active/inactive lifetimes.

No repository-wide formatter is configured. Match neighboring code and keep diffs focused.

## Test Strategy

The automated suite covers agent state, permission modes, persistence, providers, tools, sandbox rules, privacy/redaction, updates, and release invariants. Add regression tests near the behavior being fixed. Permission changes must test the same operation at preview and execution boundaries, including exact Ask approval, Full access, immutable blocks, contextual restrictions, and edited arguments. Long-run changes must cover provider retry/reset, checkpointed pause/resume, repeated tool failure, explicit recovery questions, and soft-budget extension.

The default command runtime limit is 120 seconds. Tests should use smaller explicit timeouts where possible so failure cases remain fast, while production paths must preserve cancellation and terminate child process trees.

WinUI-only behavior should also receive a manual checklist covering:

- Light and dark themes
- 100%, 125%, 150%, and 200% display scaling where available
- Maximized and narrow window layouts
- Reopen/lifecycle behavior
- Keyboard focus and screen-reader names
- Reduced motion
- Long conversations, Activity runs, and large diffs
- Ask approval followed by actual execution, including optional validated argument edits
- Full access for ordinary host/network work and hard rejection of a catastrophic command fixture
- Provider interruption, visible stream reset, paused state, and resume from the saved checkpoint

## Local Data

Development builds use the same default local paths as installed builds:

```text
%LOCALAPPDATA%\TLAH Studio\data\tlah.db
%LOCALAPPDATA%\TLAH Studio\config\
%LOCALAPPDATA%\TLAH Studio\sandboxes\
%LOCALAPPDATA%\TLAH Studio\logs\
```

Back up data before experimenting with migrations or privacy deletion. Never commit these files.

## Pull Requests

Follow [CONTRIBUTING.md](../CONTRIBUTING.md), use the PR template, and include the full CI result. Visible UI changes require before/after screenshots without credentials, local paths, private prompts, or customer data.
