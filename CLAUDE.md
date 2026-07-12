# CLAUDE.md

Guidance for coding agents working in this repository. Read [AGENTS.md](./AGENTS.md) first; it is the concise contributor contract. Use this file for TLAH-specific architectural invariants.

## Authoritative Sources

When documents conflict, prefer this order:

1. Current code and automated tests
2. `TLAHStudio.Installer/latest.json` and synchronized project/manifests for release facts
3. [docs/ARCHITECTURE.md](./docs/ARCHITECTURE.md), [docs/DEVELOPMENT.md](./docs/DEVELOPMENT.md), and [docs/RELEASING.md](./docs/RELEASING.md)
4. README files
5. Historical milestone plans under `docs/`

Current stable version: **4.12.0**. Official release automation produces a self-contained Windows x64 installer.

## Commands

```powershell
dotnet restore .\TLAHStudio.sln
dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Debug -p:Platform=x64
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release
.\tools\ci.ps1 -Configuration Release -Platform x64
```

Use Visual Studio 2022 with the Windows App SDK / WinUI workload for XAML debugging and Hot Reload. `global.json` requests .NET SDK 8.0.407 with latest-feature roll-forward.

## Solution Map

| Project | Responsibility |
|---|---|
| `TLAHStudio.App` | WinUI shell, views/view models, DI, motion, and assets |
| `TLAHStudio.Core` | Providers, orchestration, agent runtime, tools, context, MCP, security, privacy, and updates |
| `TLAHStudio.Data` | EF Core configuration, SQLite initialization, and forward migrations |
| `TLAHStudio.Updater` | Standalone update helper |
| `TLAHStudio.Core.Tests` | Runtime, persistence, tool, privacy, update, and release regression tests |

Dependency direction: `App → Core + Data`, `Data → Core`, `Tests → Core + Data`. Core does not reference the Data project, but Core services use EF Core abstractions and a configured `DbContext`.

## Non-Negotiable Invariants

### Provider and diagnostics

- Provider adapters use direct `HttpClient` calls so protocol payloads can be captured and debugged.
- Persisted/debug payloads must pass through secret redaction. Never log API keys or authorization headers.
- TLAH is local-first, not offline-only. Do not claim data never leaves the device; configured providers, MCP, web/HTTP, remote execution, and updates communicate externally.

### Agent and tools

- `AgentRunEngineV2` owns the multi-step state loop and typed frames.
- Tool requests must continue through schema/protocol validation, safety classification, permission gating, lifecycle hooks, and backend routing.
- Tool permissions and reasoning effort are independent settings. Do not conflate them in UI or persistence.
- `Full access` is host/network access by design. Restricted local execution is policy-based and is not a hardened VM boundary.
- Preserve cancellation, pause/resume, checkpoint, artifact, task, stopped-run, and Activity replay behavior.

### Persistence and privacy

- SQLite changes use the repository's lightweight forward-migration pattern unless the persistence strategy is deliberately redesigned.
- Never commit local databases, logs, sandboxes, workspaces, API keys, signing keys, installers, or private diagnostics.
- Export and privacy flows must remain defensive even after redaction; arbitrary prompt/file content may still be sensitive.

### WinUI

- Pair views and view models by feature; keep Loaded/Unloaded and active/inactive subscriptions symmetric.
- Test light/dark theme, maximized/narrow windows, DPI scaling, keyboard focus, screen-reader names, and reduced motion.
- Use compositor opacity/scale for decorative motion. Avoid animating layout properties in scrolling or resize paths.
- Keep long conversations, Activity, sidebar, and Changes virtualized or incremental; avoid O(n) per-item work during layout.

## Coding Conventions

- Four-space indentation, file-scoped namespaces, nullable reference types, implicit usings.
- PascalCase types/public members, camelCase locals/parameters, `_camelCase` private fields, `I` interfaces.
- Suffix asynchronous methods with `Async`; accept cancellation tokens for long work.
- Prefer immutable records for DTOs and state snapshots.
- Test names should express feature, condition, and expected result.

## Scope Boundaries

Do not describe the following as production capabilities without code changes and tests:

- Official x86/ARM64 downloads
- Complete LSP/code-intelligence support
- Cloud synchronization or real-time collaboration
- A general plugin marketplace or arbitrary managed plugin execution
- Hardened VM isolation for restricted local commands
- Publicly trusted Authenticode identity
- A running local HTTP SDK/API

MCP tools/resources, local skills, lightweight diagnostics/symbol discovery, and trusted plugin-manifest activation are supported within their documented limits.

## Releases

Follow [docs/RELEASING.md](./docs/RELEASING.md). A release must synchronize every version location, pass the CI gate, publish x64 App and Updater outputs, smoke-test startup, sign binaries/installer, sign and verify update metadata, and deploy the exact verified artifacts.

`-ForceSmokeTest` can replace an installed copy and must run only on a disposable Windows VM. Never expose private keys, certificate passwords, SSH credentials, or production host details in commits or logs.

## Documentation Maintenance

- Update `README.md` and `README-CN.md` together with identical section order and facts.
- Put current operational facts in `docs/ARCHITECTURE.md`, `docs/DEVELOPMENT.md`, `docs/RELEASING.md`, or `docs/PRIVACY.md`.
- Treat versioned plan files as historical design records unless explicitly marked as roadmap.
- Use repository-relative examples; never add personal absolute paths.
- Update [CHANGELOG.md](./CHANGELOG.md) for user-visible releases and [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md) for new external assets or dependencies.
