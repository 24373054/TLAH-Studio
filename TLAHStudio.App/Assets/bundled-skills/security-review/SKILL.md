---
name: security-review
description: Complete a security review of pending changes — check for injection risks, credential leaks, unsafe deserialization, and privilege escalation.
when_to_use: Use when reviewing security-sensitive changes, before merging PRs that touch auth, data access, or external input handling.
allowed-tools: Read, Grep, Glob, file_read, git, skill
argument-hint: "[focus area, e.g. 'auth changes' or 'API endpoints']"
---

# Security Review

Review pending changes for security vulnerabilities.

## Review Areas

### 1. Injection Risks
- SQL injection: Any string concatenation in queries instead of parameterized queries?
- Command injection: Any user input passed to terminal_exec without sanitization?
- Path traversal: Any file paths built from user input without validation?

### 2. Authentication & Authorization
- Are permission checks bypassed in any code path? (Check for `bypass_permissions` flags)
- Are bypass-immune paths respected? (.git/, .env, shell configs)
- Are API keys or tokens logged or stored in plaintext?

### 3. Data Protection
- Sensitive data encrypted at rest? (DPAPI for credentials)
- Secrets redacted in debug logs? (Check SecretRedactor usage)
- Any new storage of PII or credentials?

### 4. Input Validation
- All external inputs validated before use?
- JSON deserialization safe? (No TypeNameHandling risks in Newtonsoft)
- File upload paths validated?

### 5. Dependency Security
- Any new NuGet packages with known vulnerabilities?
- Any use of deprecated APIs?

### 6. Windows-Specific
- Registry access properly scoped?
- Process creation with appropriate privileges?
- Named pipe security (check LocalSdkHost authentication)?

## Report
Present findings grouped by severity: Critical / High / Medium / Low. For each: what, where (file:line), why it's a risk, and how to fix.
