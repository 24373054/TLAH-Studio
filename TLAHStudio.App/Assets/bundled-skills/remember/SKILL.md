---
name: remember
description: Review memory entries and propose promotions to CLAUDE.md, project memory, or cleanup of outdated/conflicting entries.
when_to_use: Use when the user wants to review, organize, or clean up their project memory, auto-memory, or CLAUDE.md. Also useful after a long session to capture what was learned.
allowed-tools: Read, file_read, memory_read, memory_write, memory_list
argument-hint: "[what to focus on, e.g. 'promote testing patterns']"
---

# Remember: Memory Review

Review the user's memory landscape and produce a clear report of proposed changes.

## Steps

### 1. Gather all memory layers
Read CLAUDE.md from the project root. Read project memory. Review session memory and auto-memory entries.

**Success criteria**: You have the contents of all memory layers.

### 2. Classify each entry
For each entry, determine the best destination:

| Destination | What belongs there | Examples |
|---|---|---|
| **CLAUDE.md** | Project conventions all contributors follow | "use dotnet format", "naming convention: Async suffix" |
| **Project memory** | Project-specific knowledge | "deploy goes through CI pipeline X" |
| **Session memory** | Working notes, temporary context | "currently debugging the login flow" |

### 3. Identify cleanup opportunities
- **Duplicates**: Entries already captured elsewhere → propose removal
- **Outdated**: Entries contradicted by newer changes → propose update
- **Conflicts**: Contradictions between layers → propose resolution

### 4. Present the report
Group proposals by action type:
1. **Promotions** — entries to move, with destination and rationale
2. **Cleanup** — duplicates, outdated entries, conflicts
3. **Ambiguous** — entries needing user input

Do NOT modify files without explicit user approval.
