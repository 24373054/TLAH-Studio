---
name: simplify
description: Review changed code for reuse, simplification, efficiency, and altitude cleanups, then apply the fixes.
when_to_use: Use when the user wants to clean up their code, simplify complex logic, remove duplication, or improve code quality without changing behavior. Examples: "clean this up", "simplify this code", "review for quality".
allowed-tools: Read, Grep, Glob, edit, multi_edit, skill
argument-hint: "[file or area to focus on]"
---

# Simplify: Code Cleanup

Review all changed files for reuse, quality, and efficiency. Fix any issues found.

## Phase 1: Identify Changes

Run `git diff` to see what changed. If no changes, review recently modified files.

## Phase 2: Three-Pass Review

### Pass 1: Code Reuse
1. Search for existing utilities that could replace newly written code
2. Flag any new function that duplicates existing functionality
3. Flag inline logic that could use existing utilities (string manipulation, path handling, type guards)

### Pass 2: Code Quality
1. Redundant state that duplicates existing state
2. Parameter sprawl — adding new params instead of restructuring
3. Copy-paste with slight variation — unify with shared abstraction
4. Stringly-typed code — use enums, constants, or types
5. Unnecessary comments — explaining WHAT (names already do that), narrating changes

### Pass 3: Efficiency
1. Unnecessary work — redundant computations, repeated file reads
2. Missed concurrency — independent operations run sequentially
3. Hot-path bloat — new blocking work on startup or per-request paths
4. Memory issues — unbounded data structures, missing cleanup
5. Overly broad operations — reading entire files when only a portion needed

## Phase 3: Fix Issues

Aggregate findings and fix each issue directly. If a finding is a false positive, note it and move on.

**Success criteria**: All three passes complete. Code is simpler and cleaner without behavior changes.
