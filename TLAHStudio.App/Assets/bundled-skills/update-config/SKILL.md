---
name: update-config
description: Configure TLAH Studio settings — permissions, environment variables, model settings, MCP servers, and hooks. Automated behaviors require hooks in settings.json.
when_to_use: Use when the user wants to change a setting, add a permission rule, configure an MCP server, set an environment variable, change the model, or set up hooks for automated behaviors.
allowed-tools: Read, file_read, edit, file_write
argument-hint: "[setting to change, e.g. 'set model to opus' or 'allow npm commands']"
---

# Update Config

Modify TLAH Studio configuration. 

## Settings Locations

| File | Scope | Use For |
|------|-------|---------|
| `%LOCALAPPDATA%\TLAH Studio\config\settings.json` | Global | Personal preferences |
| `<workspace>\.tlah\settings.json` | Project | Team-wide settings |

## Common Tasks

### Changing the Model
Update the model in Settings dialog or directly in the config.

### Adding Permissions
Allow or deny specific tools:
- `"terminal_exec(git *)"` — allow all git commands
- `"terminal_exec(npm *)"` — allow all npm commands
- `"file_read"` — allow all file reads

### Setting Environment Variables
Set `DEBUG=true` or API keys for tools.

### Configuring MCP Servers
Add STDIO or HTTP MCP servers with their command/endpoint.

### Setting Hooks
Configure PreToolUse/PostToolUse hooks for automated behaviors like:
- Auto-formatting after file writes
- Running tests after code changes
- Logging bash commands

## Workflow
1. **Read existing settings** — always read the current file first
2. **Merge carefully** — preserve existing settings, especially arrays
3. **Validate** — check JSON syntax after changes
4. **Confirm** — tell user what was changed
