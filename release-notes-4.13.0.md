# TLAH Studio 4.13.0

Version 4.13.0 focuses on dependable permissions and long-running agent work.

## Permissions that match the selected mode

- **Full access** now runs ordinary host, network, workspace, and sensitive-file operations without approval prompts. A narrow immutable guard still rejects catastrophic disk, boot, account, and root-recursive deletion commands.
- **Ask** approvals authorize the exact persisted invocation through resume and execution, so an operation no longer fails immediately after the user approves it.
- Approval arguments are read-only by default. Advanced edits require an explicit opt-in and must pass JSON and tool-schema validation before they are saved.
- Plan and Auto modes use the same centralized authorization matrix, eliminating contradictory decisions between preview and execution.
- Persisted allow rules now reach the final file, network, Git, MCP, typed, and V3 execution gates. SDK approval can approve and resume atomically while preserving the checkpoint's permission mode.
- The immutable guard recognizes PowerShell delete aliases and nested script blocks as well as Windows and WSL/POSIX system roots, while retaining dry-run and workspace-safe cases.

## Longer, recoverable runs

- Transient provider failures, rate limits, timeouts, incomplete streams, and server errors receive up to three bounded attempts. The visible stream resets between attempts so partial duplicate text is not retained.
- If the provider remains unavailable, the run saves a checkpoint and pauses with a clear resume path instead of collapsing into an unrecoverable failure.
- Tool failures trigger a materially different recovery attempt. If no safe route remains, the agent asks whether to try another approach or stop and summarize rather than claiming success.
- A mutating tool that times out or loses its response is fenced as an unknown outcome. Resume acknowledges it, preserves the audit trail, and continues without replaying the possibly completed side effect.
- Productive runs can extend their 48-step soft budget in 24-step increments up to the 192-step automatic ceiling. An explicit Resume can add up to 96 more steps at a time; approval callbacks do not silently grant extra budget.
- Background commands resolve a fresh execution scope for their full lifetime, avoiding disposed database/backend dependencies after the foreground run moves on.
- The local SDK and long-lived chat, sidebar, debug, and window services use isolated dependency scopes, preventing concurrent refreshes from sharing one EF database context.
- The default command timeout is now 120 seconds for realistic builds, installs, and development commands.

The official artifact is a self-contained Windows x64 installer for Windows 10 build 19041+ and Windows 11. Update metadata is ECDSA-signed and the installer is protected by SHA-256 and Authenticode signing; the current Authenticode certificate remains self-signed, so Windows may display an untrusted-publisher warning.
