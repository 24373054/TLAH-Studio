---
name: deep-research
description: Conduct deep, multi-source research on any topic — search the web, fetch sources, adversarially verify claims, and synthesize a cited report.
when_to_use: Use when the user wants a thorough, fact-checked research report. Examples: "research the best approach for X", "compare Y vs Z", "what are the latest developments in A".
allowed-tools: web_search, browser_read, file_read, file_write, ask_user_question
argument-hint: "[research question]"
---

# Deep Research

Conduct thorough research and produce a cited report.

## Steps

### 1. Understand the Question
If the question is underspecified, use ask_user_question to clarify:
- Scope: What specifically to research?
- Depth: How thorough? Quick overview or exhaustive?
- Format: Report, comparison table, pros/cons?
- Sources: Any preferred sources or source types?

### 2. Search Broadly
Run multiple web searches with different angles:
- Technical search: "[topic] best practices 2026"
- Comparative: "[topic] vs [alternative]"
- Latest: "[topic] latest developments"
- Community: "[topic] github discussion"

### 3. Fetch and Read Sources
For promising search results, use browser_read to fetch the full content. Prioritize:
- Official documentation
- Primary sources (papers, specs, RFCs)
- Authoritative community sources (established blogs, well-maintained repos)

### 4. Adversarially Verify
For each key claim:
- Can you find a second independent source confirming it?
- Is there a contradictory source? If so, what's the consensus?
- Is the source recent enough to be relevant?

### 5. Synthesize Report
Produce a structured report:
1. **Executive Summary** — 2-3 sentence answer
2. **Key Findings** — 3-5 main points with citations
3. **Detailed Analysis** — organized by subtopic
4. **Contradictory Views** — if applicable
5. **Sources** — numbered list with URLs

**Success criteria**: All key claims have at least one cited source. Contradictory evidence is acknowledged.
