# Contributing to TLAH Studio

Thank you for helping improve TLAH Studio. This guide keeps changes reviewable, reproducible, and aligned with the Windows desktop product.

中文说明见 [CONTRIBUTING-CN.md](./CONTRIBUTING-CN.md).

## Before You Start

- Search existing [issues](https://github.com/24373054/TLAH-Studio/issues) before opening a duplicate.
- Use a public issue for bugs and feature proposals. Use [private vulnerability reporting](https://github.com/24373054/TLAH-Studio/security/advisories/new) for security findings.
- Keep pull requests focused. Architecture changes should explain alternatives and migration impact before implementation.
- Read [LICENSE](./LICENSE). This is a proprietary, source-visible repository; contributions do not convert it to an open-source project.

## Development Environment

- Windows 10 build 19041+ or Windows 11
- .NET 8 SDK selected by `global.json`
- Visual Studio 2022 with the Windows App SDK / WinUI workload
- PowerShell 7 recommended for repository scripts

```powershell
dotnet restore .\TLAHStudio.sln
dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Debug -p:Platform=x64
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release
.\tools\ci.ps1 -Configuration Release -Platform x64
```

## Workflow

1. Fork the repository or create a branch named for the change, such as `fix/activity-theme`.
2. Add a regression test for behavior changes whenever the logic is testable outside WinUI.
3. Match the neighboring architecture and style; avoid unrelated formatting or generated files.
4. Run the full CI gate and manually exercise affected WinUI flows.
5. Open a pull request using the repository template.

## Code and UI Standards

- Use four-space C# indentation, file-scoped namespaces, nullable reference types, and `Async` suffixes for asynchronous methods.
- Name types/public members in PascalCase, locals/parameters in camelCase, private fields `_camelCase`, and interfaces with an `I` prefix.
- Pair views and view models by feature. Keep XAML event lifetimes explicit and unsubscribe long-lived handlers.
- Preserve the tool safety pipeline and permission semantics. Never silently broaden file, command, network, or credential access.
- Respect light, dark, compact, narrow-window, keyboard, screen-reader, and reduced-motion behavior.
- Do not commit API keys, local databases, signing keys, installers, logs, or user workspace data.

## Tests

Tests use xUnit. Name files `*Tests.cs` and prefer behavior-oriented names such as `VerifySignature_TamperedData_ReturnsFalse`.

```powershell
# All tests
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release

# Focused tests
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release `
  --filter "FullyQualifiedName~UpdateCryptoTests"
```

There is no numeric coverage threshold. New runtime, persistence, privacy, update, and tool-safety branches should be covered proportionally to risk.

## Pull Request Checklist

- Explain the user-visible behavior and root cause.
- Link the relevant issue or design discussion.
- Report commands and results used for verification.
- Include before/after screenshots for visible WinUI changes.
- Document configuration, storage, security, or release changes.
- Keep version bumps and generated release signatures out of normal feature PRs.

By submitting a contribution, you confirm that you have the right to submit it and agree to the contribution grant in [LICENSE](./LICENSE).
