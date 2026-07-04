---
name: batch
description: Research and plan a large-scale change, then execute it in parallel across multiple isolated work units that each deliver independently.
when_to_use: Use when the user wants to make a sweeping change across many files. Examples: "migrate all controllers to use the new base class", "update all NuGet packages", "rename X to Y across the entire solution".
allowed-tools: Read, Grep, Glob, file_read, enter_plan_mode, exit_plan_mode, skill, task_create, task_list, ask_user_question
argument-hint: "<instruction describing the batch change>"
---

# Batch: Parallel Work Orchestration

Orchestrate a large, parallelizable change across the codebase.

## Phase 1: Research and Plan (Plan Mode)

Enter plan mode, then:

### 1. Understand scope
Launch subagents to deeply research what this touches. Find all files, patterns, and call sites.

### 2. Decompose into independent units
Break the work into 5-30 self-contained units. Each unit must:
- Be independently implementable
- Touch files that don't conflict with other units
- Be roughly uniform in size

### 3. Write the plan
Include:
- Summary of research findings
- Numbered list of work units (each: title, files, one-line description)
- Worker instructions template
- Success criteria for the overall batch

### 4. Exit plan mode
Present the plan for user approval.

## Phase 2: Execute

For each work unit:
1. Create a background task with clear instructions
2. Include: overall goal, this unit's specific task, file list, conventions to follow

## Phase 3: Track Progress

Maintain a status table. When all tasks report, render the final table with results.
