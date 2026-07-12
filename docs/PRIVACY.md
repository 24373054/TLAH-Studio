# Privacy and Data Flows

Verified against TLAH Studio 4.12.0.

TLAH Studio is local-first: its database, settings, run history, sandboxes, logs, and diagnostic exports are stored on the user's Windows profile by default. Local-first does not mean that all processing stays offline.

## Local Storage

| Path | Data |
|---|---|
| `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` | Chats, messages, settings, provider traces, runs, events, tasks, MCP configuration, and audit records |
| `%LOCALAPPDATA%\TLAH Studio\config\` | UI and workspace configuration |
| `%LOCALAPPDATA%\TLAH Studio\sandboxes\` | Private chat sandboxes when no workspace is selected |
| `%LOCALAPPDATA%\TLAH Studio\logs\` | Startup and application diagnostics |
| `<workspace>\.tlah_context\tool-results\` | Persisted large tool outputs |

API keys and protected credentials use Windows DPAPI-backed storage. Secrets are redacted from provider debugging and export paths where implemented. This protects data at rest from casual disclosure; it does not protect against compromise of the current Windows user session.

## External Data Flows

| Operation | Possible destination | Data involved |
|---|---|---|
| Model request | Configured Anthropic or OpenAI-compatible endpoint | Prompt, selected conversation context, tool definitions/results, attachments |
| MCP | Configured STDIO process or Streamable HTTP server | Tool arguments/results and requested resources |
| Web / HTTP tools | Requested or configured web endpoint | Query, URL, headers allowed by policy, request/response content |
| Remote execution | Configured backend | Command/task payload and returned output |
| Update check/download | Official update host | App version, update request, installer download; staged rollout uses a local install identifier |

TLAH Studio does not currently include a general product telemetry SDK. External providers and endpoints have their own privacy terms and logging behavior.

## Permissions and Workspace Scope

- The selected workspace or private sandbox determines the default filesystem scope.
- Tool permission mode determines when reads, writes, commands, and risky operations require approval.
- `Full access` intentionally allows broad host and network access and should be used only with trusted prompts and workspaces.
- Restricted backends enforce application policies; they are not equivalent to VM isolation.

## Export, Diagnostics, and Deletion

The **Privacy & Data** surface supports diagnostic/export workflows and local-data management. Review exports before sharing them. Redaction reduces accidental secret exposure but cannot guarantee that arbitrary prompt, file, or tool content contains no sensitive information.

Before deleting local data, close active runs and back up anything that must be retained. Workspace files outside the app's local data directory are governed by the tool operations that created or modified them and may not be removed by clearing app data.

## Reporting a Privacy or Security Issue

Use [GitHub Private Vulnerability Reporting](https://github.com/24373054/TLAH-Studio/security/advisories/new) for unintended disclosure, redaction failure, credential exposure, or permission bypass. Do not attach real secrets or private user data.

---

## 中文摘要

TLAH Studio 采用本地优先存储，会话、设置、运行记录、沙箱和日志默认保存在当前 Windows 用户目录中。但模型请求会把提示词和所选上下文发送到用户配置的模型端点；MCP、网页/HTTP、远程执行和更新功能在使用时也会连接外部服务。

API Key 使用 Windows DPAPI 支持的保护机制，调试和导出路径会在已实现的位置进行脱敏，但这不能抵御当前 Windows 用户会话已经被攻破的情况。`完全访问` 会按设计允许广泛的宿主机和网络访问。分享诊断导出前必须人工检查，隐私或安全问题请通过私密漏洞报告提交。
