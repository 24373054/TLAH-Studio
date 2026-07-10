# Repository Guidelines

## Project Structure & Module Organization

`TLAHStudio.sln` is a .NET 8 Windows solution. `TLAHStudio.App/` contains the WinUI 3 UI, with views, view models, and runtime assets under `Assets/`. `TLAHStudio.Core/` owns agent orchestration, LLM providers, tools, security, updates, and workspace logic; `TLAHStudio.Data/` contains the EF Core SQLite context. Tests live in `TLAHStudio.Core.Tests/`. The standalone updater and Inno Setup files are in `TLAHStudio.Updater/` and `TLAHStudio.Installer/`. Use `tools/` for CI and release scripts, `docs/` for architecture plans, and `artifacts/` only for generated output.

## Build, Test, and Development Commands

- `dotnet restore .\TLAHStudio.sln` restores packages with the SDK selected by `global.json`.
- `dotnet build .\TLAHStudio.sln -c Debug` performs a normal development build.
- `dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release` runs all xUnit tests.
- `.\tools\ci.ps1 -Configuration Release -Platform x64` runs the full gate: restore, dependency audit, tests, and App/Updater builds.

For UI debugging and XAML Hot Reload, open `TLAHStudio.sln` in Visual Studio 2022 with the Windows App SDK/WinUI workload and launch `TLAHStudio.App`.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, nullable reference types, and implicit usings. Name types and public members in PascalCase, locals and parameters in camelCase, private fields `_camelCase`, and interfaces with an `I` prefix. Suffix asynchronous methods with `Async`; prefer records for immutable DTOs. Pair UI types by feature, such as `ChatPage.xaml`, `ChatPage.xaml.cs`, and `ChatPageViewModel.cs`. No repository-wide formatter or linter is configured, so match neighboring code and keep builds warning-free.

## Testing Guidelines

Tests use xUnit and coverlet. Add focused `*Tests.cs` files and descriptive underscore-separated methods such as `VerifySignature_TamperedData_ReturnsFalse`. Run a subset with `dotnet test ... --filter "FullyQualifiedName~UpdateCryptoTests"`. There is no numeric coverage threshold; cover new branches and regressions, especially in agent runtime, persistence, privacy, tool safety, and update verification.

## Commit & Pull Request Guidelines

History favors concise, scoped subjects such as `4.9.6 P1-c: Core-layer regression tests`, `fix(build-release): ...`, and `release 4.9.6`. Use version/phase prefixes for release work and a clear scope for focused fixes. Pull requests should summarize behavior changes, link relevant issues, report `.\tools\ci.ps1` results, identify known gaps, and include before/after screenshots for visible WinUI changes.

## Security & Configuration Tips

Never commit API keys, private signing keys, local `.db` files, or generated installers. Keep defaults in `TLAHStudio.App/appsettings.json`; user data belongs under `%LOCALAPPDATA%\TLAH Studio\`. Version changes must keep project files, manifests, installer metadata, and `latest.json` synchronized.
