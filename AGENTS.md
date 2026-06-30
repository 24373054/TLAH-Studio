# Repository Guidelines

## Project Structure & Module Organization

TLAH Studio is a .NET 8 Windows desktop solution rooted at `TLAHStudio.sln`.
`TLAHStudio.App/` contains the WinUI 3 shell, XAML views, view models, DI setup, and `Assets/`.
`TLAHStudio.Core/` holds business logic: LLM providers, agent runtime, tool safety, settings, update, privacy, and workspace services.
`TLAHStudio.Data/` contains the EF Core SQLite context and lightweight schema evolution.
`TLAHStudio.Updater/` is the standalone updater, `TLAHStudio.Installer/` holds installer metadata, and `TLAHStudio.Core.Tests/` contains xUnit tests.
Use `tools/` for build, signing, verification, and deployment scripts; keep release outputs in `artifacts/`.

## Build, Test, and Development Commands

- `dotnet restore .\TLAHStudio.sln` restores packages using the SDK pinned in `global.json`.
- `dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Release -p:Platform=x64` builds the desktop app.
- `dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release` runs the xUnit suite.
- `.\tools\ci.ps1` runs the local quality gate: restore, tests, app build, and updater build.
- `dotnet publish .\TLAHStudio.App\TLAHStudio.App.csproj -c Release -r win-x64 --self-contained true` creates a self-contained app publish.
- `.\tools\verify-release.ps1 -Version 3.0.0 -AllowUntrustedAuthenticode` verifies installer metadata, hashes, signatures, and payloads.

For UI work, open `TLAHStudio.sln` in Visual Studio 2022+ with Windows App SDK workloads enabled.

## Coding Style & Naming Conventions

Use C# with nullable reference types and implicit usings enabled. Keep four-space indentation and file-scoped namespaces. Prefer clear service interfaces (`ISettingsService`), async method names ending in `Async`, and immutable DTOs/records where the surrounding code does. Pair WinUI views and view models by name, for example `ChatPage.xaml` with `ChatPageViewModel`. Keep LLM providers on direct `HttpClient` calls so raw HTTP capture remains intact.

## Testing Guidelines

Tests use xUnit and live in `TLAHStudio.Core.Tests/` as `*Tests.cs`. Name tests with the pattern `MethodOrFeature_Condition_ExpectedResult`. Add focused tests for changes to agent runtime, persistence, tool safety, LLM formatting, privacy redaction, and update verification. Guard Windows-only behavior with `OperatingSystem.IsWindows()` as existing tests do.

## Commit & Pull Request Guidelines

Recent history uses short release or feature messages such as `Release 3.0.0 agent GA` and `@ feat(2.10.0-3.0.0): ...`. Keep commits concise, scoped, and version-aware when touching release flow. Pull requests should describe the change, list test results such as `.\tools\ci.ps1`, link related issues, and include screenshots for visible WinUI changes.

## Security & Configuration Tips

Do not commit API keys, user databases, private signing keys, or installer binaries. Runtime user data lives under `%LOCALAPPDATA%\TLAH Studio\data\tlah.db`; app defaults live in `TLAHStudio.App/appsettings.json`. When changing versions, keep project files, installer JSON, `latest.json`, and Inno Setup metadata in sync.
