---
name: debug
description: Diagnose TLAH Studio issues by reading the startup log and session debug information.
when_to_use: Use when the user reports a crash, startup failure, blank screen, or unexpected behavior. Also use after a crash to check what went wrong.
allowed-tools: Read, Grep, Glob, file_read
argument-hint: "[issue description]"
---

# Debug

Diagnose issues in TLAH Studio.

## Log Locations

| Log | Path | Purpose |
|-----|------|---------|
| Startup log | `%LOCALAPPDATA%\TLAH Studio\logs\startup.log` | App initialization, DI, first window creation |
| Session debug | `%LOCALAPPDATA%\TLAH Studio\logs\` | Runtime errors and warnings |
| Crash dumps | `%LOCALAPPDATA%\TLAH Studio\crash\` | Crash information |

## Steps

### 1. Read the startup log
The startup log shows the complete initialization sequence. Look for:
- `FATAL:` entries — app-terminating errors
- `ERROR:` entries — critical failures
- Missing DLL or assembly load errors
- XAML parsing errors
- DI resolution failures

**Success criteria**: You have identified any startup failures and their root causes.

### 2. Check recent crash dumps
If there were crashes, check the crash directory for recent dump files with timestamps.

### 3. Diagnose common issues

**Blank/white screen on launch**: Usually a XAML error during window creation. Check startup log for XAML parse errors, missing resources, or control template issues.

**Crash on specific action**: Check the session debug log for errors logged just before the crash.

**Sidebar issues**: ListView with ControlTemplate must have ListViewItemPresenter as first child. Check for x:Bind failures (x:Bind fails when ViewModel is null at XAML load time).

**Compilation errors**: Check for CS8601/CS8604 nullable warnings, CS0103 missing references, CS0104 ambiguity.

### 4. Report
Summarize what you found, the root cause, and concrete fix steps.
