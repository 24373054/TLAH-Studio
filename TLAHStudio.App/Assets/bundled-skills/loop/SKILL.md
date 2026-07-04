---
name: loop
description: Run a prompt or slash command on a recurring interval.
when_to_use: Use when the user wants to set up a recurring task. Examples: "loop 5m check the deploy", "loop 30m run the tests", "loop check every 2h if the PR is merged".
allowed-tools: Read, task_create, skill
argument-hint: "[interval] <prompt>"
---

# Loop: Recurring Prompt

Run a prompt on a recurring interval.

## Parsing the Input

1. **Leading token**: If the first word matches `[0-9]+[smhd]` (e.g. `5m`, `2h`), that's the interval; the rest is the prompt.
2. **Trailing "every" clause**: Otherwise, if the input ends with `every <N><unit>`, extract that as the interval.
3. **Default**: If no interval is found, use `10m` and the entire input as the prompt.

Examples:
- `5m /babysit-prs` → interval `5m`, prompt `/babysit-prs`
- `check the deploy every 20m` → interval `20m`, prompt `check the deploy`
- `check the deploy` → interval `10m`, prompt `check the deploy`

## Scheduling

Create a durable background task that:
1. Waits the specified interval
2. Runs the prompt as a new user turn
3. Repeats until stopped

Use task_create with appropriate settings for durable scheduling.

## Lifecycle
- Recurring tasks auto-expire after 7 days
- Use task_stop to cancel a running loop
- Use task_list to see active loops
