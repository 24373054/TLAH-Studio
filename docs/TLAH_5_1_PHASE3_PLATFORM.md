# Phase 3:平台与差异化(5.1+)

> **阶段目标**:平台基础设施升级 + 发挥 Windows 原生/GUI 优势形成差异化。本阶段内容多但可分多个小版本(5.1/5.2/5.3)逐步交付,不必一次做完。重点是:SDK 升级到 HTTP+WebSocket 多会话、防休眠与完成通知(Windows 原生体验)、限额感知、遥测框架、ToolSearch 延迟加载、Worktree 隔离、Cron 定时;并以"可观测回放"和"P2P 团队记忆同步"作为超越 Claude Code 的差异化产品能力。
>
> **版本**:5.1+(前置 5.0.0,可拆分为 5.1/5.2/5.3 多个小版本)
> **前置依赖**:2.1(spawn_agent)是 3.6(Worktree + 子代理自主执行)的前置;3.1(SDK 升级)是 3.8(可观测回放)的前置。
> **取舍**:OAuth、远程托管设置、组织策略限制**不列入主线**(见第八节),因其与本地优先定位有张力或需配套后端。
>
> **路线图**:见 [TLAH_5_0_ROADMAP.md](./TLAH_5_0_ROADMAP.md)

---

## 任务清单总览

| # | 任务 | 优先级 | 工作量 | 类型 | 依赖 | 建议版本 |
|---|---|---|---|---|---|---|
| 3.1 | SDK 升级(HTTP + WebSocket + 鉴权 + 多会话恢复) | P0 | M | 对齐 | 无 | 5.1.0 |
| 3.2 | 防休眠 + 完成通知 | P1 | S | 对齐(Windows 化) | 无 | 5.1.0 |
| 3.3 | claude.ai 限额感知 | P1 | M | 对齐 | 无 | 5.1.0 |
| 3.4 | 遥测框架(本地优先) | P1 | M | 对齐 | 无 | 5.1.0 |
| 3.5 | ToolSearch 延迟加载 | P1 | L | 对齐 | 无 | 5.2.0 |
| 3.6 | Worktree 隔离 + 子代理自主执行 | P1 | L | 对齐+超越 | 2.1 | 5.2.0 |
| 3.7 | Cron/定时任务 | P2 | M | 对齐 | 无 | 5.2.0 |
| 3.8 | SDK 可观测回放 | P2 | L | **超越** | 3.1 | 5.3.0 |
| 3.9 | VCR 录制回放(用于测试) | P2 | M | 对齐 | 无 | 5.3.0 |
| 3.10 | LLM 安全分类器(auto mode)+ denialTracking | P2 | L | 对齐 | 无 | 5.3.0 |
| 3.11 | file_read/grep 增强 | P2 | M | 对齐 | 无 | 5.3.0 |
| 3.12 | P2P 团队记忆同步 | P3 | L | **超越** | 无 | 5.4.0+ |

> 3.2、3.3、3.4、3.5、3.7、3.9、3.10、3.11 互不依赖,可按版本主题灵活组合。

---

## 3.1 SDK 升级(HTTP + WebSocket + 鉴权 + 多会话恢复)[P0][M][对齐]

**背景**:TLAH 的 `LocalSdkHost` 仅命名管道,单连接串行处理,无 HTTP、无 WebSocket、无会话多路复用、无鉴权、无会话索引/恢复。本机任意进程可调用 `start_run` 执行命令(无 `authToken` 校验)。无法支撑 IDE 长连接实时推送 agent 事件。Claude Code 用 HTTP 创建会话 + WebSocket 双向流,支持多会话并发与 `maxSessions` 治理。

**现状**:`TLAHStudio.Core/Services/Sdk/LocalSdkHost.cs:29-208`
- `:70-89` — 单 `NamedPipeServerStream` 循环,一次只处理一个连接
- `:74` — 直接 `WaitForConnectionAsync`,无 `authToken` 校验
- `:100-109` — 方法集:`send_message`/`start_run`/`approve_tool`/`list_chats`/`get_run_status`/`get_run_events_jsonl`

**Claude Code 对标**:
- `server/createDirectConnectSession.ts:26-88` — HTTP POST `${serverUrl}/sessions`(可选 `Bearer authToken`),返回 `{session_id, ws_url, work_dir}`
- `server/directConnectManager.ts:40-213` — WebSocket 长连接双向通信,服务器推送 SDK 消息 + 控制请求(`can_use_tool` 权限询问),客户端回送消息/权限响应/中断
- `server/types.ts:13-24` — `ServerConfig`(`idleTimeoutMs`/`maxSessions`/`workspace`/`authToken`)
- `server/types.ts:26-31` — 会话状态机 `starting|running|detached|stopping|stopped`
- `server/types.ts:46-55` — `SessionIndexEntry` 持久化到 `~/.claude/server-sessions.json` 支持跨重启恢复

**实现要点**:
1. 在 `LocalSdkHost` 旁加 **localhost-only HTTP server**(Kestrel 或 HttpListener),`POST /sessions` 创建会话返回 `{session_id, ws_url, work_dir}`。
2. **WebSocket 双向流**:会话创建后建 WebSocket,服务器推送 agent 事件(assistant/result/system)与控制请求(`can_use_tool`),客户端回送用户消息/权限响应/中断。
3. **鉴权**:启动时生成 `authToken`(随机),仅本机可读文件存储;`POST /sessions` 与 WebSocket 握手校验 `Bearer authToken`。
4. **多会话并发**:`ConcurrentDictionary<sessionId, SessionState>`,支持 `maxSessions` 上限。
5. **会话状态机**:`starting|running|detached|stopping|stopped`。
6. **会话索引与恢复**:新表 `SdkSessionIndex`(`{chatId, runId, cwd, createdAt, lastActiveAt, status}`),启动时恢复未完成会话(detached→running)。
7. 保留命名管道作为兼容回退(已接入的消费者平滑迁移)。

**验收标准**:
- [ ] `POST /sessions` 创建会话,返回 ws_url
- [ ] WebSocket 双向通信:服务器推送事件,客户端回送消息/权限/中断
- [ ] `authToken` 鉴权生效,无 token 请求被拒
- [ ] 多会话并发(≥2 个 IDE 连接同时活跃)
- [ ] 进程重启后,未完成会话从 `SdkSessionIndex` 恢复
- [ ] 命名管道保留兼容
- [ ] 新增单测:鉴权、多会话、状态机、恢复

**依赖**:无

**风险**:中。localhost HTTP server 的端口安全(绑定 127.0.0.1,不暴露外网);WebSocket 在 WinUI 进程内的生命周期管理。缓解:Kestrel 是 .NET 成熟方案;端口随机分配避免冲突。

---

## 3.2 防休眠 + 完成通知 [P1][S][对齐(Windows 化)]

**背景**:TLAH 作为 Windows 桌面应用,长 agent 运行时系统可能休眠中断;完成后无通知。Claude Code 有 `preventSleep`(macOS caffeinate)与 `notifier`(多终端)。Windows 有对应 API(`SetThreadExecutionState`、`AppNotificationBuilder`)但 TLAH 未用。

**现状**:无防休眠、无完成通知实现。

**Claude Code 对标**:
- `services/preventSleep.ts:36-58, 101-151` — macOS `caffeinate -i -t 300`,引用计数,每 4 分钟重启
- `services/notifier.ts:40-104` — 多通道通知,按终端类型自动选

**实现要点**:
1. **防休眠**:agent 运行时调 `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`,引用计数(多个 run 并发时累加),全部结束后 `SetThreadExecutionState(ES_CONTINUOUS)` 复位。
2. **完成通知**:agent run 完成时,用 `AppNotificationBuilder`(WinApp SDK)弹 toast,标题"TLAH Studio",内容"任务完成: <chat title>",点击跳回主窗口。
3. 仅在窗口非前台或最小化时弹 toast(前台时不打扰)。
4. 设置开关:`GlobalSettings.IsCompletionNotificationEnabled`(默认开)。

**验收标准**:
- [ ] agent 运行时系统不休眠(可电源设置验证)
- [ ] 多 run 并发时引用计数正确,全部结束才复位
- [ ] run 完成且窗口非前台时弹 toast
- [ ] 点击 toast 跳回主窗口
- [ ] 设置开关生效
- [ ] 新增单测:引用计数逻辑(防休眠 API 调用 mock)

**依赖**:无

**风险**:低。`SetThreadExecutionState` 与 `AppNotificationBuilder` 均为 Windows 标准 API。

---

## 3.3 claude.ai 限额感知 [P1][M][对齐]

**背景**:TLAH 用户超额时只能看到 429 错误,无预警。Claude Code 从 API 响应头 `anthropic-ratelimit-unified-*` 实时提取利用率,双重预警(服务器头 + 客户端时间相对阈值),状态栏展示。

**现状**:无;`AnthropicProvider` 用裸 `HttpClient`,429 直接暴露原始错误。

**Claude Code 对标**:
- `services/claudeAiLimits.ts:454-485` — `extractQuotaStatusFromHeaders`,5 小时/7 天窗口利用率、reset 时间、overage
- `services/claudeAiLimits.ts:255-294` — `getHeaderBasedEarlyWarning`(服务器主动 `surpassed-threshold` 头)
- `services/claudeAiLimits.ts:301-340` — `getTimeRelativeEarlyWarning`(客户端时间相对阈值兜底)
- `services/claudeAiLimits.ts:487-515` — `extractQuotaStatusFromError`,429 强制 `rejected`

**实现要点**:
1. 在 `AnthropicProvider` 的 `HttpClient` 响应拦截器(`DelegatingHandler`)解析 `anthropic-ratelimit-unified-*` 头。
2. 写入 `IRuntimeMetricsCollector`(已有),新增 `QuotaStatus` 字段(5h/7d 利用率、reset、overage)。
3. 状态栏(ChatHeaderControl 或主窗口状态栏)展示利用率 + reset 倒计时。
4. 429 时 `extractQuotaStatusFromError` 强制 `rejected`,UI 友好提示(替代原始 429)。
5. 双重预警:服务器 `surpassed-threshold` 头 + 客户端阈值(利用率 >80% 且窗口剩余 <20%)。
6. 仅 Anthropic provider 生效(其他 provider 无此头)。

**验收标准**:
- [ ] Anthropic 响应头被解析,状态栏展示利用率 + reset
- [ ] 429 时友好提示(非原始错误)
- [ ] 利用率超阈值时预警
- [ ] 非 Anthropic provider 不受影响
- [ ] 新增单测:头解析、阈值判定、429 处理

**依赖**:无

**风险**:中。`DelegatingHandler` 注入需确保不破坏原始 HTTP 捕获(底牌 5.3)。缓解:拦截器只读响应头,不修改请求/响应体。

---

## 3.4 遥测框架(本地优先)[P1][M][对齐]

**背景**:TLAH Core 层零遥测,无产品改进数据、无崩溃上报。Claude Code 有分层(Datadog + 1P OTel)、采样、killswitch、PII 保护。TLAH 本地优先定位下,默认不上报,但需框架就位以支持未来产品迭代与崩溃诊断。

**现状**:Core 层 `Grep telemetry|analytics` 零命中。

**Claude Code 对标**:
- `services/analytics/index.ts:81-164` — 启动前事件入队,`attachAnalyticsSink` 后 `queueMicrotask` 排空
- `services/analytics/sink.ts:48-72` — 路由到 Datadog + 1P event logger
- `services/analytics/config.ts:19-27` — test/Bedrock/Vertex/`isTelemetryDisabled` 时禁用
- `services/analytics/sinkKillswitch.ts` — 按 sink 级紧急关停
- `index.ts:19` — `AnalyticsMetadata_I_VERIFIED_THIS_IS_NOT_CODE_OR_FILEPATHS` 幻影类型强制 PII 检查

**实现要点**:
1. 新增 `ITelemetryService` + 内存事件队列(`ConcurrentQueue<TelemetryEvent>`)+ 异步 flush。
2. **默认仅写本地** `%LOCALAPPDATA%\TLAH Studio\logs\telemetry.jsonl`,用户显式同意后才上报。
3. 事件 schema 强类型(`TelemetryEvent`:name/timestamp/context),PII 字段标记(参考 CC 幻影类型思路,用 `[TelemetrySafe]` attribute 标注安全字段)。
4. `TelemetrySink` 抽象:本地 sink(默认)+ 可选远程 sink(用户同意后);`SinkKillswitch` 紧急关停。
5. 禁用条件:test 环境、`GlobalSettings.IsTelemetryDisabled`。
6. 采集项:启动耗时、agent run 时长、工具调用频率、压缩触发次数、崩溃堆栈(脱敏)。
7. **不采集**:API key、消息内容、文件路径(PII 红线)。

**验收标准**:
- [ ] 事件入队异步 flush 到本地 `telemetry.jsonl`
- [ ] 默认不上报,用户同意后才启用远程 sink
- [ ] PII 字段(API key/消息/路径)不被采集
- [ ] test 环境与显式禁用时完全不工作
- [ ] sink killswitch 可紧急关停
- [ ] 新增单测:入队/flush、PII 过滤、禁用条件

**依赖**:无

**风险**:中。隐私合规——必须明确同意,默认关。缓解:首次启动弹同意对话框;本地 jsonl 可随时查看/删除。

---

## 3.5 ToolSearch 延迟加载 [P1][L][高风险][对齐]

**背景**:TLAH 所有工具启动即注册(`App.xaml.cs:97-137`),`tool_search` 仅目录搜索(`TaskAgentTools.cs:299-413`)。~40 个工具的完整 schema 全塞进每个请求,token 浪费且 prompt cache 易失效。Claude Code 用 `shouldDefer`/`alwaysLoad` + ToolSearch 按需加载 schema,只把工具名进 system prompt。

**现状**:
- `TLAHStudio.App/App.xaml.cs:97-137` — ~40 工具全注册
- `TLAHStudio.Core/Services/TaskAgentTools.cs:299-413` — `tool_search` 仅返回 name/display/category,不动态注册

**Claude Code 对标**:
- `Tool.ts:438-449` — `shouldDefer`/`alwaysLoad` 标志
- `tools/ToolSearchTool/ToolSearchTool.ts:186-302, 444-470` — 关键词评分匹配,返回 `tool_reference` blocks(仅工具名),由 API/客户端展开为完整 schema
- `tools/ToolSearchTool/prompt.ts:31-42` — 延迟工具仅名进 system prompt(`<available-deferred-tools>`)
- `tools/ToolSearchTool/prompt.ts:62-108` — `isDeferredTool` 规则:`alwaysLoad` 优先 → MCP 默认延迟 → ToolSearch 自身不延迟

**实现要点**:
1. `AgentToolMetadata` 加 `ShouldDefer`(bool)与 `AlwaysLoad`(bool)标志。
2. 系统提示分两段:`<available-tools>`(always-load 工具完整 schema)+ `<available-deferred-tools>`(延迟工具仅名 + 一句话描述)。
3. `tool_search` 升级:输入 `query`(关键词或 `select:name1,name2`),返回匹配工具的完整 schema(动态注入下一轮的 `available-tools`)。
4. 评分匹配(参考 CC `ToolSearchTool.ts:186-302`):工具名 CamelCase 拆分、MCP `mcp__server__action` 拆分,精确 part 高分、部分中分、description 低分。
5. 默认延迟的工具:MCP 工具、低频工具(code_lsp/multi_edit/apply_patch 等);always-load:核心工具(file_read/file_write/edit/grep/glob/task_*/todo_write)。
6. **prompt cache 稳定**:延迟工具名清单按名排序(参考 CC `assembleToolPool` 的 `localeCompare`),避免顺序抖动。

**验收标准**:
- [ ] 系统提示含 `<available-tools>`(完整 schema)+ `<available-deferred-tools>`(仅名)
- [ ] `tool_search` 返回匹配工具完整 schema,注入下一轮
- [ ] 延迟工具未 search 时 schema 不进上下文
- [ ] always-load 工具始终完整 schema
- [ ] prompt cache 命中率不降(工具名排序稳定)
- [ ] 新增单测:延迟判定、评分匹配、schema 注入

**依赖**:无

**风险**:高。涉及工具注册体系重构,可能影响 prompt cache 命中与现有工具调用。缓解:分批延迟(先延迟 MCP 工具与低频 code 工具,核心工具不动);充分回归测试现有工具调用流。

---

## 3.6 Worktree 隔离 + 子代理自主执行 [P1][L][对齐+超越]

**背景**:TLAH 仅 per-chat sandbox(`SandboxCommandService.cs:66-73`),子任务与父任务共享 sandbox,无法并行修改同一仓库的不同分支。`TaskAgentTools.cs:95, 155` 自承 worktree "reserved for later"。Claude Code 用 `EnterWorktree` 为子任务创建 git worktree 隔离环境。结合 2.1 的 spawn_agent,让子代理在独立 worktree 自主执行。

**现状**:
- `TLAHStudio.Core/Services/SandboxCommandService.cs:66-73` — per-chat sandbox
- `TLAHStudio.Core/Services/TaskAgentTools.cs:95, 155` — `workspace_isolation` 参数接受但 "reserved for later"

**Claude Code 对标**:
- `utils/worktree.ts:702-778` — `createWorktreeForSession`,`git worktree add -B <branch> <path> <baseBranch>`,路径 `.claude/worktrees/<flattened-slug>`
- `utils/worktree.ts:277-303` — baseRef:检查 `origin/<defaultBranch>` 是否本地存在,否则 fetch,失败回退 HEAD
- `tools/EnterWorktreeTool/EnterWorktreeTool.ts:77-119` — 进入:`process.chdir` + 清 system prompt/memory 缓存
- `tools/ExitWorktreeTool/ExitWorktreeTool.ts:174-224` — 退出:`git status --porcelain` + `git rev-list --count` 数变更,有变更须 `discard_changes:true`

**实现要点**:
1. 新增 `enter_worktree` / `exit_worktree` 工具(注册到 `AgentToolNames`)。
2. `enter_worktree`:`git worktree add -B <branch> .tlah/worktrees/<slug> <baseBranch>`,baseRef 检查 `origin/<defaultBranch>`(本地存在则用,否则 fetch,失败回退 HEAD)。
3. 切 CWD:子 run 的 `AgentRunState.WorkspaceRoot` 指向 worktree 路径,清 system prompt/memory 缓存。
4. 结合 `spawn_agent`:`spawn_agent` schema 的 `workspace_isolation: "worktree"` 让子代理在独立 worktree 跑(激活 `TaskAgentTools.cs:155` 的 reserved 路径)。
5. `exit_worktree`:`action="remove"` 时 `git status --porcelain` + `git rev-list --count` 数变更,有变更须 `discard_changes:true` 才清理;`action="keep"` 仅切回原 CWD。
6. 并行隔离:多个子代理用不同 worktree 并行修改同仓不同分支(配合 2.6 DAG 协调器)。

**验收标准**:
- [ ] `enter_worktree` 创建 git worktree,子 run CWD 切到 worktree
- [ ] `spawn_agent(workspace_isolation: "worktree")` 子代理在独立 worktree 跑
- [ ] 多个子代理用不同 worktree 并行修改同仓不冲突
- [ ] `exit_worktree` 有变更时拒绝清理,须 `discard_changes:true`
- [ ] baseRef 回退逻辑正确(origin 不存在 → fetch → HEAD)
- [ ] 新增单测:worktree 创建/切换/清理、变更检测

**依赖**:2.1(spawn_agent)

**风险**:中。git worktree 在 Windows 的路径长度限制;CWD 切换对 `AgentRunState` 的影响。缓解:worktree 路径用短 slug;CWD 在 `AgentRunState` 而非进程级(避免影响主 run)。

**超越价值**:TLAH 的 per-chat sandbox 是单仓隔离,worktree 让子任务真正并行修改同仓不同分支——配合 DAG 协调器,可做 CC 子代理未深入的并行实现场景。

---

## 3.7 Cron/定时任务 [P2][M][对齐]

**背景**:TLAH 完全无定时任务能力,无法支撑"每小时检查 PR"、"每天跑测试"等场景。Claude Code 的 `ScheduleCron` 支持会话内/持久化、recurring/one-shot、jitter、7 天过期。TLAH 作为 Windows 原生工作台,定时任务是高频需求。

**现状**:全仓搜 `cron`/`ScheduleCron` 零命中。

**Claude Code 对标**:
- `tools/ScheduleCronTool/CronCreateTool.ts:27-42` — `cron`(5 字段本地时区)/`prompt`/`recurring`/`durable`
- `tools/ScheduleCronTool/CronCreateTool.ts:82-116` — 校验:`parseCronExpression`、`nextCronRunMs` 须在一年内、最多 50 个任务
- `utils/cron/cronTasks.ts:336-445` — recurring 7 天过期,one-shot 触发即删,jitter 防 :00 雷群
- `utils/cron/cronScheduler.ts` — 1s 轮询 + 文件监听 + scheduler lock

**实现要点**:
1. 新增 `schedule_cron` 工具(注册到 `AgentToolNames`),schema:`{cron, prompt, recurring?, durable?}`。
2. cron 解析:5 字段(本地时区),校验 `nextRun` 在一年内,最多 50 个任务。
3. durable 任务持久化到 `.tlah/scheduled_tasks.json`(会话内任务存内存)。
4. 调度器:1s 轮询 + 文件监听( durable 文件变更)+ scheduler lock(单实例触发)。
5. jitter:recurring 按间隔比例前向延迟(防 :00 雷群);one-shot 在 :00/:30 整点反向提前。
6. 触发:enqueue `prompt` 为新 user turn(到当前会话或指定会话)。
7. 生命周期:recurring 7 天自动过期,one-shot 触发即删。
8. 管理:新增 `cron_list`/`cron_delete` 工具(或复用 task_list)。

**验收标准**:
- [ ] `schedule_cron` 创建定时任务,durable 持久化
- [ ] 1s 轮询触发,enqueue prompt 为新 user turn
- [ ] recurring 7 天过期,one-shot 触发即删
- [ ] jitter 防 :00 雷群(多个任务错开)
- [ ] 进程重启后 durable 任务恢复
- [ ] 新增单测:cron 解析、触发、过期、jitter

**依赖**:无

**风险**:中。调度器在 WinUI 进程内的后台线程;进程退出时 durable 任务的恢复。缓解:`IHostedService` 后台服务;启动时从 json 恢复。

---

## 3.8 SDK 可观测回放 [P2][L][超越]

**背景**:TLAH 已有 `AgentRunFrame` 事件流 + 原始 HTTP 捕获(底牌 5.3),这是 Claude Code 没有的独有优势。提供 SDK 方法 `replay_run`,让外部程序能"录像回放"任意 agent run(含每步工具调用、审批、原始 LLM 请求/响应)。Claude Code 的 VCR 仅服务测试,TLAH 可做成调试/教学/审计利器。

**Claude Code 对标**:
- `services/vcr.ts` — 仅 test/CI 确定性回放,非调试产品
- TLAH 独有:`AgentRunFrame` 事件流 + RawRequest/RawResponse 入库

**实现要点**:
1. SDK 新增方法 `replay_run(runId, {speed?, from_step?})`(经 3.1 的 HTTP/WebSocket)。
2. 从 DB 读 `AgentRun` + `AgentRunFrame` + `RawRequest`/`RawResponse` + `ToolInvocation` + `Approval`,按 `SequenceNum` 排序。
3. 经 WebSocket 流式推送:每步的 model 请求/响应(原始 HTTP)、工具调用、审批、状态变更。
4. `speed` 控制回放速度(0.5x/1x/2x/即时);`from_step` 从指定步开始。
5. 客户端(IDE/SDK)可暂停/步进/跳转任意步。
6. WinUI 侧:DebugPanelControl 已有原始 JSON 检视,本任务扩展为"逐帧回放"模式(时间轴 + 步进控件)。
7. 审计场景:回放可导出为完整报告(每步的输入/输出/决策)。

**验收标准**:
- [ ] SDK `replay_run` 流式推送任意 run 的逐帧事件
- [ ] 每步含原始 LLM 请求/响应、工具调用、审批
- [ ] `speed`/`from_step` 参数生效
- [ ] 客户端可暂停/步进/跳转
- [ ] WinUI DebugPanel 逐帧回放模式可用
- [ ] 新增单测:回放顺序、速度控制、步过滤

**依赖**:3.1(SDK HTTP/WebSocket 就位)

**风险**:中。大 run 的回放数据量(原始 HTTP 可能很大)需分页/流式。缓解:WebSocket 流式推送 + 客户端按需请求某步详情;原始 HTTP 按需加载(不一次性全推)。

**超越价值**:这是 TLAH 底牌 5.3(原始 HTTP 捕获)的产品化,CC 无对应能力。调试/教学/审计场景价值高。

---

## 3.9 VCR 录制回放(用于测试)[P2][M][对齐]

**背景**:TLAH 的 `TLAHStudio.Core.Tests` 直接打真实或 mock HTTP,无 fixture 录制机制,测试脆弱(依赖网络或手写 mock)。Claude Code 的 `vcr.ts` 让 CI 无网络依赖且确定性高。

**现状**:`TLAHStudio.Core.Tests` 直接 mock 或真实 HTTP。

**Claude Code 对标**:
- `services/vcr.ts:23-86` — `shouldUseVCR` 仅 test/`FORCE_VCR`
- `services/vcr.ts:88-161` — 录制到 `fixtures/*.json`,按 dehydrated 输入 SHA1 命名
- `services/vcr.ts:291-347` — `dehydrateValue`(规范路径/计数/时间戳)/`hydrateValue`(回放还原)
- `services/vcr.ts:349-406` — `withStreamingVCR`/`withTokenCountVCR`

**实现要点**:
1. 新增 `VcrRecorder` 包装 `HttpClient`(`DelegatingHandler`),录制请求/响应到 `fixtures/<sha1>.json`。
2. fixture 命名:dehydrated 请求体的 SHA1(dehydrate 规范化路径/时间戳/计数,使 fixture 跨平台稳定)。
3. 回放:请求匹配 fixture 时短路网络,返回录制响应;流式响应支持回放。
4. CI 缺 fixture 时报错,提示 `VCR_RECORD=1` 重录。
5. `ILlmProvider` 用裸 `HttpClient`(底牌 5.3),正好可拦截。
6. test 环境启用,生产不启用。

**验收标准**:
- [ ] test 环境录制 LLM 请求/响应到 fixture
- [ ] 回放时短路网络,返回 fixture 响应
- [ ] dehydrate 使 fixture 跨平台稳定
- [ ] CI 缺 fixture 报错提示录制命令
- [ ] 流式响应可回放
- [ ] 新增单测:录制/回放/dehydrate

**依赖**:无

**风险**:中。流式响应的回放需正确还原 SSE chunk;dehydrate 规则需覆盖路径/时间戳/随机数。缓解:参考 CC `dehydrateValue` 实现;先做非流式,流式后置。

---

## 3.10 LLM 安全分类器(auto mode)+ denialTracking [P2][L][高风险][对齐]

**背景**:TLAH 的 `AutoApprove` 是"全放行"(仅 bypass-immune 拦截),风险显著高于 Claude Code 的 auto。CC 的 `auto` 模式用 LLM 分类器裁决 `ask` 工具,有 acceptEdits 快速放行、safe-tool allowlist、PowerShell 门控三快速路径,配合 denialTracking(3 consecutive/20 total)回退到交互。

**现状**:
- `TLAHStudio.Core/Services/AgentPermissionModes.cs:25-26` — `IsAutoApprove`,`AgentRunEngine.cs:497-502` 仅 bypass-immune 拦截

**Claude Code 对标**:
- `utils/permissions/yoloClassifier.ts` — LLM 分类器裁决 ask 工具
- `utils/permissions/classifierDecision.ts:56-94` — safe-tool allowlist 快速放行
- `utils/permissions/permissions.ts:572-702` — acceptEdits 快速放行 + PowerShell 门控
- `utils/permissions/denialTracking.ts:12-15` — `maxConsecutive=3`/`maxTotal=20`,超限回退交互

**实现要点**:
1. `auto_approve` 模式升级:ask 工具(非 bypass-immune、非 safe-tool)经 LLM 分类器裁决。
2. 分类器输入:per-tool `ToAutoClassifierInput` 生成紧凑表示(工具名 + 参数摘要)。
3. 三个快速路径(不走分类器):
   - safe-tool allowlist(file_read/grep/glob 等只读工具)直接放行。
   - acceptEdits(CWD 内 file/code 编辑)直接放行——需先实现 acceptEdits 模式(可并入本任务)。
   - PowerShell/terminal 只读命令(`IsReadOnly`)直接放行。
4. 分类器裁决:side query 调 small/fast 模型判定 allow/block,带工具名 + 参数。
5. **denialTracking**:`DenialTrackingState`(3 consecutive/20 total),分类器 block 累加,allow 重置 consecutive;超限 `handleDenialLimitExceeded` 回退到交互审批。
6. 分类器失败(超时/错误)回退到交互审批(fail-safe)。

**验收标准**:
- [ ] safe-tool allowlist 工具 auto 模式直接放行(不走分类器)
- [ ] ask 工具经分类器裁决
- [ ] denialTracking 累加,超限回退交互
- [ ] 分类器失败回退交互(fail-safe)
- [ ] 新增单测:safe-tool 放行、分类器裁决、denialTracking 累加/重置/回退

**依赖**:无

**风险**:高。LLM 安全裁决误判后果严重(误放行危险操作)。缓解:fail-safe(分类器失败必回退交互);bypass-immune 路径(.git/.env)仍强制审批(不经过分类器);denialTracking 兜底;充分测试分类器 prompt。

---

## 3.11 file_read/grep 增强 [P2][M][对齐]

**背景**:TLAH `file_read` 无行号、无 offset/limit、无 image/PDF 支持;`code_grep` 用纯 .NET `Directory.EnumerateFiles` + `string.Contains`,无 `files_with_matches`/`count` 输出模式,大仓库性能差。Claude Code 的 `FileReadTool` 有 cat -n 行号/offset/limit/image/PDF/notebook;`GrepTool` 完整包装 ripgrep。

**现状**:
- `TLAHStudio.Core/Services/BuiltInAgentTools.cs:274-327` — `file_read` 原始文本,无行号/offset/image/PDF
- `TLAHStudio.Core/Services/WorkspaceCodeTools.cs:511-591` — `code_grep` 纯 .NET,无多输出模式
- `TLAHStudio.Core/Services/CodeToolsV3.cs:213-316` — V3 尝试 rg.exe 但回退 .NET

**Claude Code 对标**:
- `tools/FileReadTool/FileReadTool.ts:725-727, 866-1017` — cat -n 行号、offset/limit、image base64、PDF 页提取、notebook cell、maxTokens=25K
- `tools/FileReadTool/limits.ts` — maxTokens=25_000、maxSizeBytes=256KB
- `tools/GrepTool/GrepTool.ts:33-90, 441` — 完整 ripgrep,output_mode(content/files_with_matches/count)、-A/-B/-C context、-i、--type、multiline、head_limit=250

**实现要点**:
1. **file_read 增强**:
   - 加 `offset`/`limit`/`pages` 参数,输出 cat -n 行号。
   - image(png/jpg/webp)读为 base64 块(若 provider 支持 vision)。
   - PDF 用 PDF 提取库(如 PdfPig)提取页文本(或页图像)。
   - maxTokens=25K 限制,超限提示用 offset 翻页。
2. **grep 增强**:
   - `code_grep` 强制用 rg.exe(移除 .NET 回退,或仅在 rg 不可用时回退)。
   - 加 `output_mode`(content/files_with_matches/count)、`-A`/`-B`/`-C` context、`-i`、`--type`、`multiline`、`head_limit=250`。
   - rg.exe 路径:打包到 Assets 或运行时下载。

**验收标准**:
- [ ] `file_read` 输出 cat -n 行号,支持 offset/limit 翻页
- [ ] image 读为 base64,PDF 提取页文本
- [ ] maxTokens=25K 限制生效
- [ ] `code_grep` 用 rg.exe,output_mode 三态工作
- [ ] context/-i/--type/multiline/head_limit 生效
- [ ] 新增单测:行号/翻页/image/PDF、grep 三态/context

**依赖**:无

**风险**:中。PDF 提取库的依赖体积;rg.exe 的打包/路径。缓解:PdfPig 纯托管轻量;rg.exe 作为 Assets 打包。

---

## 3.12 P2P 团队记忆同步 [P3][L][高风险][超越]

**背景**:TLAH 的 `IMemoryDirectoryService` 是单机项目记忆,团队协作时无法共享上下文。Claude Code 的 `teamMemorySync` 是中心化 API + server-wins,强绑定 anthropic 后端。TLAH 本地优先 + git 工作流天然适合 P2P——用 git remote 本身作为传输层 + age/NaCl 加密,git 三方合并优于乐观锁。契合开发者习惯。

**现状**:`IMemoryDirectoryService` 单机,无同步。

**Claude Code 对标**:
- `services/teamMemorySync/index.ts:770-867` — pull(server 覆盖本地)
- `services/teamMemorySync/index.ts:889-1146` — push(delta 上传,仅 hashContent 不同)
- `services/teamMemorySync/secretScanner.ts` — gitleaks 规则扫描凭据,命中跳过(只上报 ruleId)
- 中心化 API + 乐观锁(`If-Match` checksum,412 冲突重试)

**实现要点**:
1. **传输层**:用 git bare repo 作为同步后端——`.tlah_team_memory/` 目录提交到私有 git repo(用户自选托管:GitHub/GitLab/自建)。
2. **加密**:用 age 或 NaCl 对记忆文件加密后提交,git 仓库只见密文(零信任)。
3. **合并**:用 git 三方合并(本地/远程/base)而非 CC 的 last-write-wins/乐观锁——冲突由 git merge 处理,保留双方修改。
4. **secret 扫描**:上传前(commit 前)用 gitleaks 规则扫描,命中凭据的文件跳过提交(只记录 ruleId)。
5. **去抖**:目录变更后 2 秒去抖再 commit/push(参考 CC `watcher.ts`)。
6. **失败抑制**:不可自愈错误(如无权限)后 `pushSuppressedReason` 抑制重试,防无限循环。
7. UI:SettingsDialog 配置同步 repo URL + 加密密钥;TeamWorkspaceDialog 展示同步状态。

**验收标准**:
- [ ] 记忆文件加密后提交到 git repo
- [ ] 多设备 pull/push 同步,git 三方合并冲突
- [ ] secret 扫描命中凭据的文件不提交
- [ ] 去抖与失败抑制生效
- [ ] 用户自选 git 托管(不绑定特定后端)
- [ ] 新增单测:加密/解密、三方合并、secret 扫描、去抖

**依赖**:无

**风险**:高。加密密钥管理(丢失则记忆不可恢复);git 合并冲突的手动解决;gitleaks 规则的准确性。缓解:密钥用 DPAPI 加密存储 + 用户可导出备份;冲突时保留双方版本标记待手动处理;secret 扫描宽松(宁可漏过不误报阻断)。

**超越价值**:CC 的 teamMemorySync 是中心化 + server-wins,绑定 anthropic 后端;TLAH 用 P2P git + 加密 + 三方合并,本地优先且契合开发者 git 习惯,加密后零信任。

---

## 八、刻意不列入主线的能力(取舍记录)

以下 Claude Code 能力**暂不列入主线**,详见 [路线图第六节](./TLAH_5_0_ROADMAP.md#六风险与取舍记录):

| 能力 | CC 实现 | 不列入理由 | 何时重新评估 |
|---|---|---|---|
| OAuth 登录(claude.ai) | `services/oauth/` | 强绑定云端账号;TLAH 本地优先,API key + DPAPI 已满足 | 有 claude.ai 订阅集成需求 |
| 远程托管设置 | `services/remoteManagedSettings/` | 需企业后端 + 组织模型 | 企业版立项 |
| 组织策略限制 | `services/policyLimits/` | 同上,需后端 | 企业版立项;可先做本地策略 schema |
| 云端设置同步 | `services/settingsSync/` | CC 绑定 anthropic API | 用 3.12 P2P git 同步替代 |
| 内部日志 | `services/internalLogging.ts` | Anthropic 内部 fleet 专用 | 不做 |

**取舍原则**:当"对齐 Claude Code"与"守护本地优先差异化"冲突时,优先本地优先。OAuth/策略限制若未来有明确需求,可在 5.4+ 评估,但需配套后端工程,不属于本路线图范围。

---

## 版本交付定义(5.1+ 分版本)

本阶段建议拆分为多个小版本,每个版本一个主题:

### 5.1.0 — 平台基础(3.1 + 3.2 + 3.3 + 3.4)
- SDK 升级 + 防休眠通知 + 限额感知 + 遥测框架
- 平台地基就位,后续 3.8 可观测回放依赖 3.1

### 5.2.0 — 工具与隔离(3.5 + 3.6 + 3.7)
- ToolSearch 延迟加载 + Worktree + Cron
- 工具体系优化与子代理隔离增强

### 5.3.0 — 测试与安全(3.8 + 3.9 + 3.10 + 3.11)
- 可观测回放 + VCR + auto 分类器 + file_read/grep 增强
- 测试基础设施 + 安全裁决 + 工具能力补全

### 5.4.0+ — 协作(3.12)
- P2P 团队记忆同步(高风险,单独评估)

### 各版本通用 Done 标准
1. 对应任务验收标准全部勾选;`dotnet build` 无错误无警告。
2. `dotnet test TLAHStudio.Core.Tests -c Release` 全绿。
3. `.\tools\ci.ps1 -Configuration Release -Platform x64` 通过。
4. 版本同步:`appsettings.json`、各 `.csproj`、`TLAHStudio.Installer/version.json`、`setup.iss` → 对应版本号。
5. CLAUDE.md 更新新增架构性子系统。
6. tag → `build-release.ps1` → `verify-release.ps1` → SCP 上传。

---

## 测试策略要点

- **SDK 升级测试**:多会话并发、鉴权拒绝、状态机转换、崩溃恢复。
- **ToolSearch 测试**:延迟判定、评分匹配、schema 注入、prompt cache 命中率对比(延迟前后)。
- **Worktree 测试**:创建/切换/清理、变更检测、多 worktree 并行。
- **可观测回放测试**:回放顺序、大 run 分页、speed 控制。
- **auto 分类器测试**:safe-tool 放行、分类器裁决、denialTracking、fail-safe(分类器失败回退)。
- **P2P 同步测试**:加密/解密、三方合并冲突、secret 扫描、去抖。

---

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-07-04 | 初版,定义 5.1+ 十二项平台与差异化任务(含两项超越:可观测回放、P2P 同步) |
