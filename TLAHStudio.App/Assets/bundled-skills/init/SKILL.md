---
name: init
description: Analyze this codebase and create a CLAUDE.md file for future instances to work productively.
when_to_use: Use when creating or updating project documentation for Claude to follow. Also use after a significant refactor that changes the project structure.
allowed-tools: Read, Grep, Glob, file_list, file_read, file_write, edit
argument-hint: "[path or language hint]"
---

# Init: Codebase Documentation

Analyze the codebase and create or update a CLAUDE.md file with documentation for future Claude instances.

## Steps

### 1. Discover the project type
Check for solution files (.sln), project files (.csproj), package.json, Cargo.toml, go.mod, etc. Determine the primary language and framework.

**Success criteria**: You know the project type, language, framework, and build system.

### 2. Read existing documentation
Check for existing CLAUDE.md, README.md, AGENTS.md, .cursorrules, .github/copilot-instructions.md.

**Success criteria**: You have read all existing guidance files and know what's already documented.

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
2. High-level architecture (not every file)
3. Key patterns and conventions
4. Configuration locations
5. Version conventions

Do NOT include:
- Obvious instructions ("write tests", "be helpful")
- File trees that can be discovered with Glob
- Generic development practices

**Success criteria**: A future Claude instance reading this CLAUDE.md can start working productively immediately.
