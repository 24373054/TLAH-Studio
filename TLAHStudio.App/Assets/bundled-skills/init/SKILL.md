---
name: init
description: Analyze this codebase and create project documentation (CLAUDE.md) so future AI coding assistants can work productively.
when_to_use: Use when setting up a new project, onboarding to an existing codebase, or after a significant refactor that changes the project structure. CLAUDE.md is the industry-standard filename used by many coding assistants (TLAH Studio, Claude Code, Cursor, Continue, and others).
allowed-tools: Read, Grep, Glob, file_list, file_read, file_write, edit
argument-hint: "[path or language hint]"
---

# Init: Codebase Documentation

Analyze the codebase and create or update a CLAUDE.md file. CLAUDE.md is the standard project documentation format recognized by most AI coding assistants — it is NOT specific to any one tool. The file will be used by any future AI agent working in this repository, including TLAH Studio, Claude Code, Cursor, and others.

## Steps

### 1. Discover the project type
Check for solution files (.sln), project files (.csproj), package.json, Cargo.toml, go.mod, etc. Determine the primary language and framework.

**Success criteria**: You know the project type, language, framework, and build system.

### 2. Read existing documentation
Check for existing CLAUDE.md, README.md, AGENTS.md, .cursorrules, .github/copilot-instructions.md.

**Success criteria**: You have read all existing guidance files and know what is already documented. Never overwrite content the user has written — merge new findings with existing documentation.

### 3. Analyze the build system
Read build scripts, CI configs, and package manifests. Document the correct commands for:
- Restoring dependencies
- Building in debug and release
- Running tests (all and single)
- Linting/formatting
- Publishing/deploying

**Success criteria**: You can reproduce the build and test commands from the source of truth.

### 4. Map the architecture
Understand the high-level architecture:
- Project/module boundaries and dependencies
- Key namespaces and their responsibilities
- Design patterns used (DI, MVVM, repository, etc.)
- Data flow through the system

**Success criteria**: You can explain the architecture without referencing specific line numbers.

### 5. Write CLAUDE.md
Create or update CLAUDE.md with:
1. Build & development commands
2. High-level architecture (not every file — focus on the "big picture" that requires reading multiple files to understand)
3. Key patterns and conventions
4. Configuration locations
5. Version conventions

Do NOT include:
- Obvious instructions ("write tests", "be helpful")
- File trees that can be discovered with Glob
- Generic development practices
- Content already in README.md unless it is architecturally essential

**Success criteria**: A future AI agent reading this CLAUDE.md can start working productively immediately, regardless of which tool it is using.
