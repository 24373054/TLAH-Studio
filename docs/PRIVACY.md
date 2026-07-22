# Privacy and Data Flows

Verified against TLAH Studio 4.15.0.

TLAH Studio is local-first: its database, settings, run history, sandboxes, logs, and diagnostic exports are stored on the user's Windows profile by default. Local-first does not mean that all processing stays offline.

## Local Storage

| Path | Data |
|---|---|
| `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` | Chats, messages, settings, provider traces, runs, events, tasks, MCP configuration, and audit records |
| `%LOCALAPPDATA%\TLAH Studio\config\` | UI and workspace configuration |
| `%LOCALAPPDATA%\TLAH Studio\sandboxes\` | Private chat sandboxes when no workspace is selected |
| `%LOCALAPPDATA%\TLAH Studio\logs\` | Startup and application diagnostics |
| `<workspace>\.tlah_context\tool-results\` | Persisted large tool outputs |
| `<workspace>\` or `%LOCALAPPDATA%\TLAH Studio\sandboxes\<chat>\` | User-requested research reports, spreadsheets, documents, diagrams, and chart previews |

API keys and protected credentials use Windows DPAPI-backed storage. Secrets are redacted from provider debugging and export paths where implemented. This protects data at rest from casual disclosure; it does not protect against compromise of the current Windows user session.

## External Data Flows

| Operation | Possible destination | Data involved |
|---|---|---|
| Model request | Configured Anthropic or OpenAI-compatible endpoint | Prompt, selected conversation context, tool definitions/results, attachments |
| MCP | Configured STDIO process or Streamable HTTP server | Tool arguments/results and requested resources |
| Web / HTTP tools | Requested or configured web endpoint | Query, URL, headers allowed by policy, request/response content |
| Create & Research | DuckDuckGo HTML; [GDELT Project DOC API](https://www.gdeltproject.org/) for non-language-constrained news; language-matched Wikipedia Action API (`en`, `zh`, `ja`, `ko`, `de`, `fr`) for undated/entity lookup; DuckDuckGo Lite; selected public HTTPS pages | The query and applicable filters are sent only to providers actually attempted. Explicit language filters omit GDELT, and non-`Any` recency filters omit undated Wikipedia results. Selected page/PDF URLs and content are sent only to their source sites. Wikipedia snippets and article links carry [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) attribution. |
| Remote execution | Configured backend | Command/task payload and returned output |
| Update check/download | Official update host | App version, update request, installer download; staged rollout uses a local install identifier |

TLAH Studio does not currently include a general product telemetry SDK. The Tool Quality page computes local aggregates from tool names, statuses, and timestamps already stored in SQLite; its query does not select prompts, tool arguments/results, or file contents. External providers and endpoints have their own privacy terms and logging behavior.

## Permissions and Workspace Scope

- The selected workspace or private sandbox determines the default filesystem scope.
- One authorization policy determines when reads, writes, commands, network access, and risky operations run, ask, or remain blocked at both preview and execution time.
- Ask approval is scoped to the exact persisted tool invocation. Once approved, ordinary contextual policies cannot silently reject that invocation during resume or execution.
- Approval arguments are read-only by default. Advanced edits require explicit opt-in, a valid JSON object, and the selected tool's input validation before persistence; never paste credentials into tool arguments unless the destination explicitly requires them.
- `Full access` intentionally bypasses ordinary policy, host-path, network-allowlist, and sensitive-file restrictions. Only immutable catastrophic-operation guards and functional waits for user input remain. Use it only with trusted prompts and workspaces.
- Restricted backends enforce application policies; they are not equivalent to VM isolation.
- Research retrieval accepts public HTTPS destinations only and rejects loopback, private, and link-local addresses even in Full access to prevent the research surface from becoming an internal-network probe.

## Export, Diagnostics, and Deletion

The **Privacy & Data** surface supports diagnostic/export workflows and local-data management. Review exports before sharing them. Redaction reduces accidental secret exposure but cannot guarantee that arbitrary prompt, file, or tool content contains no sensitive information.

Before deleting local data, close active runs and back up anything that must be retained. Workspace files outside the app's local data directory are governed by the tool operations that created or modified them and may not be removed by clearing app data.

## Reporting a Privacy or Security Issue

Use [GitHub Private Vulnerability Reporting](https://github.com/24373054/TLAH-Studio/security/advisories/new) for unintended disclosure, redaction failure, credential exposure, or permission bypass. Do not attach real secrets or private user data.

---

## 中文摘要

TLAH Studio 采用本地优先存储，会话、设置、运行记录、沙箱和日志默认保存在当前 Windows 用户目录中。但模型请求会把提示词和所选上下文发送到用户配置的模型端点；MCP、网页/HTTP、远程执行和更新功能在使用时也会连接外部服务。

API Key 使用 Windows DPAPI 支持的保护机制，调试和导出路径会在已实现的位置进行脱敏，但这不能抵御当前 Windows 用户会话已经被攻破的情况。Create & Research 首先尝试 DuckDuckGo HTML；随后按查询和筛选条件选择 GDELT Project DOC API（无显式语言约束的新闻查询）或对应语言的 Wikipedia Action API（`en`、`zh`、`ja`、`ko`、`de`、`fr`，仅用于不要求时效日期的实体检索），最后才尝试 DuckDuckGo Lite。显式语言筛选不会调用 GDELT，非 `Any` 时效筛选不会接纳无日期的 Wikipedia 结果。查询与筛选提示只会发送给实际尝试的提供方；Wikipedia 摘要和文章链接附带 [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) 归因。页面内容仅从用户选择的公开 HTTPS 来源下载，研究网络始终拒绝环回、私有和链路本地地址。Tool Quality 只读取本地工具名、状态和时间戳，不读取提示词、参数、结果或文件内容。Ask 批准只授权已持久化的精确工具调用；审批参数默认只读，显式编辑后必须通过 JSON 与工具输入校验。`完全访问` 会按设计绕过普通的策略、宿主机路径、网络允许列表与敏感文件限制，仅保留灾难级操作硬阻断、研究网络边界和必要的用户交互等待。分享诊断导出前必须人工检查，隐私或安全问题请通过私密漏洞报告提交。
