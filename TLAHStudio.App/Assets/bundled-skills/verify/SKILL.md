---
name: verify
description: Verify that a code change actually works by running the app and observing behavior.
when_to_use: Use when the user asks to verify a PR, confirm a fix works, test a change manually, check that a feature works, or validate local changes before pushing.
allowed-tools: Read, Grep, Glob, terminal_exec, file_read
argument-hint: "[what to verify]"
---

# Verify

Verify that a code change does what it's supposed to by running the app and observing behavior.

## Steps

### 1. Understand the change
Read the diff or changed files. Identify:
- What was changed and why
- What behavior should be affected
- What the expected outcome is

**Success criteria**: You can state in one sentence what the change is supposed to do.

### 2. Build the project
Run the project's build command. If it fails, report the build error and stop — the verification failed at the build stage.

**Success criteria**: Build succeeds with zero errors.

### 3. Run tests
Run the project's test suite. If tests fail:
- Check if failures are pre-existing or introduced by this change
- Report which tests fail and why

**Success criteria**: Tests pass, or failures are identified as pre-existing.

### 4. Manual verification (if applicable)
For UI changes:
- Start the app and navigate to the affected screen
- Exercise the changed behavior
- Take a screenshot if possible

For API/backend changes:
- Start the service and hit the affected endpoint
- Verify the response matches expectations

For CLI changes:
- Run the command with test inputs
- Check the output

**Success criteria**: The changed behavior works as expected when exercised manually.

### 5. Report
Summarize what was verified, what passed, what failed, and any concerns.
