---
name: skillify
description: Capture this session's repeatable process as a reusable skill file.
when_to_use: Call at the end of a process you want to capture. Examples: "turn this workflow into a skill", "save this process for next time", "create a skill from what we just did".
allowed-tools: Read, file_read, file_write, ask_user_question, terminal_exec(mkdir:*)
argument-hint: "[description of the process you want to capture]"
---

# Skillify: Capture Process as Skill

Capture this session's repeatable process as a reusable skill.

## Steps

### Step 1: Analyze the Session
Identify:
- What repeatable process was performed
- What the inputs/parameters were
- The distinct steps in order
- What tools and permissions were needed
- Where the user corrected or steered you

### Step 2: Interview the User
Use ask_user_question for ALL questions:

**Round 1: High level**
- Suggest a name and description
- Confirm the goal and success criteria

**Round 2: Details**
- Present the steps you identified
- Suggest arguments if the process takes parameters
- Ask where to save: project (`.tlah/skills/`) or personal (`%LOCALAPPDATA%\TLAH Studio\skills\`)

**Round 3: Step breakdown**
For each major step, confirm:
- What this step produces that later steps need
- What proves this step succeeded
- Should user confirm before proceeding?

### Step 3: Write SKILL.md
Use this format:
```markdown
---
name: skill-name
description: one-line description
allowed-tools: Read, file_read, ...
when_to_use: When to auto-invoke, with trigger phrases
argument-hint: "[hint]"
---

# Skill Title

## Goal
Clear goal with success criteria.

## Steps
### 1. Step Name
What to do.

**Success criteria**: How to know this step is done.
```

### Step 4: Save and Confirm
Write the file and tell the user where it was saved.
