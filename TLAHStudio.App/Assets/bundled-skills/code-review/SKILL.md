---
name: code-review
description: Review changed code for correctness bugs and reuse/simplification/efficiency cleanups. Use for pull requests, working diffs, or any code changes.
when_to_use: Use when the user asks for a code review, PR review, or wants to check their changes before committing. Examples: "review my changes", "code review this PR", "check this diff for bugs".
allowed-tools: Read, Grep, Glob, file_read, git, skill
argument-hint: "[focus area or file pattern]"
---

# Code Review

Review all changed files for correctness bugs and quality issues.

## Phase 1: Identify Changes

Run `git diff` (or `git diff HEAD` if staged) to see what changed. If no git changes, review the files the user mentioned or that were edited earlier.

## Phase 2: Bug Review

For each change, check:

1. **Null safety**: Are nullable reference types handled? Any potential NullReferenceExceptions?
2. **Async correctness**: Are async methods properly awaited? Any fire-and-forget that should be tracked?
3. **Resource management**: Are IDisposable objects disposed? File handles closed?
4. **Thread safety**: Any shared state accessed from multiple threads without synchronization?
5. **Error handling**: Are exceptions caught appropriately? No empty catch blocks?
6. **Input validation**: Are external inputs (user input, file content, API responses) validated?
7. **Logic errors**: Off-by-one, inverted conditions, missing edge cases?
8. **C#-specific**: Pattern matching exhaustiveness, switch expression completeness, LINQ deferred execution pitfalls?

**Success criteria**: Every potential bug is documented with file path, line, and explanation.

## Phase 3: Quality Review

1. **Reuse opportunities**: Could new code use existing utilities instead?
2. **Naming**: Descriptive, consistent with project conventions?
3. **Complexity**: Methods under 50 lines? Cyclomatic complexity reasonable?
4. **Comments**: Non-obvious WHY explained? Comments not explaining WHAT code does?
5. **Tests**: New logic covered by tests? Edge cases tested?

**Success criteria**: Quality issues documented with actionable suggestions.

## Phase 4: Report

Present findings grouped by severity:
1. **Critical** — bugs that could cause crashes, data loss, or security issues
2. **Important** — logic errors that produce wrong results
3. **Minor** — style, naming, organization improvements

For each finding: file:line, what's wrong, why, and how to fix.
