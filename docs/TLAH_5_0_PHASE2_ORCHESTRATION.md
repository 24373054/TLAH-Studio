# Phase 2:多代理编排(5.0.0)

> **阶段目标**:从单代理跃升到多代理。本阶段是能力代差最大的一块——TLAH 的 `AgentRunEngineV2` 是严格单代理状态机,模型无任何"派生另一个 LLM agent 执行子任务"的接口,邮箱 write-only 无消费者,任务依赖字段存而不用。完成后,TLAH 具备子代理派生、多类型任务、代理间通信、协调器编排能力。同时以 C# 强类型 DAG 调度器与零成本 Dream 作为超越 Claude Code 的差异化点。
>
> **版本**:5.0.0(前置 4.9.0)
> **前置依赖**:0.3(压缩链)、0.6(重注入保工具上下文)、0.7(SessionMemory 节流)、2.1 是本阶段多数任务的前置
> **后续影响**:2.1(spawn_agent)是 Phase 3.6(Worktree + 子代理自主执行)的前置。
>
> **路线图**:见 [TLAH_5_0_ROADMAP.md](./TLAH_5_0_ROADMAP.md)

---

## 任务清单总览

| # | 任务 | 优先级 | 工作量 | 类型 | 依赖 |
|---|---|---|---|---|---|
| 2.1 | `spawn_agent` 子代理派生 | P0 | L | 对齐 | 0.3、0.6 |
| 2.2 | 多类型 Task 框架(ITaskImplementation) | P0 | M | 对齐 | 2.1 |
| 2.3 | 邮箱消费者(激活 PendingMessages) | P1 | M | 激活死代码 | 2.1、2.2 |
| 2.4 | `notified` 原子锁 + `outputOffset` 增量投递 | P1 | S | 对齐 | 2.2 |
| 2.5 | 任务依赖边(Blocks/BlockedBy)+ owner | P1 | M | 激活死代码 | 无 |
| 2.6 | 确定性 DAG 协调器 | P1 | L | **超越** | 2.1、2.5 |
| 2.7 | 子代理 token 预算硬上限 | P2 | M | **超越** | 2.1 |
| 2.8 | 零成本 Dream(跨会话记忆巩固) | P2 | M | **超越** | 0.7 |

> 2.5 与 2.1/2.2 独立,可先行。2.6 是本阶段超越重点。

---

## 2.1 `spawn_agent` 子代理派生 [P0][L][对齐]

**背景**:TLAH 的 `AgentRunEngineV2` 是严格单代理 while 循环,一次一个 `AgentRunState`。模型无任何"派生另一个 LLM agent 执行子任务"的接口——`task_create` 的 `background=true`+`prompt` 路径是显式占位符(`TaskAgentTools.cs:153-156`:"Full autonomous subagent execution is reserved for the next worktree-backed iteration")。这是多代理编排的基石,无此则一切无从谈起。

**现状**:
- `TLAHStudio.Core/Services/AgentRuntime/AgentRunEngine.cs:160-567` — 单代理 while 循环,`RunAsync(state, options, frameProgress, ct)` 一次一个 RunId/ChatId
- `TLAHStudio.Core/Services/TaskAgentTools.cs:153-156` — `background=true`+`prompt` 占位符
- `AgentRunState.cs:11-41` — 状态记录,支持 `DeepClone()`

**Claude Code 对标**:
- `tools/AgentTool/AgentTool.tsx:239-1262` — `call()` 三路径:队友派生(`:284-316`)/ fork 子代理(`:318-356`)/ 常规子代理(`:336-356`)
- `tools/AgentTool/runAgent.ts:248-860` — `runAgent` 生成器,子代理获独立 messages(`:373`)、独立工具权限 `filterToolsForAgent`(`agentToolUtils.ts:70-116`,7 层过滤)、独立 `AbortController`(`:524-528`)
- `tools/AgentTool/AgentTool.tsx:567` — `shouldRunAsync` 决定同步阻塞或异步通知
- `coordinatorMode.ts:144-160` — `<task-notification>` XML 作为 user-role 消息注入父上下文

**实现要点**:
1. `AgentToolNames` 新增 `spawn_agent` 工具,schema:`{prompt: string, subagent_type?: string, run_in_background?: bool, token_budget?: int, allowed_tools?: string[]}`。
2. 复用 `IAgentRunEngineV2.RunAsync` 跑子 run,但**独立 `AgentRunState`**:
   - 独立 `Messages`(子代理只看到一个 prompt 用户消息,看不到父代理历史;fork 模式可选克隆父对话,本阶段先做常规路径)。
   - 独立 token 预算(配合 2.7)。
   - 独立权限模式(默认继承父,可由 `subagent_type` 覆盖)。
   - 独立 `CancellationToken`(异步模式给子代理全新 token,父 ESC 不影响)。
3. **工具权限隔离**:子代理的 `allowed_tools` 是**替换**非合并(参考 CC `filterToolsForAgent`);子代理默认禁用 `spawn_agent`(防无限嵌套)、`task_stop`(防杀兄弟)、`spawn_agent` 自身。
4. **同步模式**:`run_in_background=false`(默认),内联迭代子 run 的 `RunAsync`,阻塞父代理直到子代理完成;带自动后台化计时器(默认 120s,超时转异步,参考 CC `AgentTool.tsx:72-77`)。
5. **异步模式**:`run_in_background=true`,立即返回 `agentId` + `outputFile`,父不阻塞;子代理完成后,结果以 `<task-notification>` XML 注入父代理下一轮上下文(参考 `coordinatorMode.ts:144-160`)。
6. **结果回传**:`finalizeAgent` 等效逻辑——提取子代理最后一条 assistant 文本 + 工具调用计数 + token 用量,作为 `<result>` 注入。
7. **帧发射**:子 run 的 `AgentRunFrame` 经 `IAgentEventStream` 流式回传 UI(UI 树状展示父子关系)。

**验收标准**:
- [ ] 模型可调用 `spawn_agent` 派生子代理执行子任务
- [ ] 子代理有独立 messages,看不到父代理历史
- [ ] 子代理工具权限隔离(默认禁用 spawn_agent/task_stop)
- [ ] 同步模式阻塞父代理,异步模式立即返回
- [ ] 异步子代理完成后,结果以 `<task-notification>` 注入父上下文
- [ ] 子 run 帧流式回传 UI,树状展示
- [ ] 子代理 ESC(异步模式)不影响父代理
- [ ] 新增单测:子代理上下文隔离、权限隔离、结果回传、同步/异步切换

**依赖**:0.3(压缩链,子代理也需压缩)、0.6(重注入保工具上下文)

**风险**:中。子 run 的 context 隔离与父 run 状态污染是难点;并发 run 对 `AgentEventSubscriptionService`(每 runId 一个 channel,`AgentEventSubscriptionService.cs:44-75`)的压力需测试。缓解:子 run 用独立 runId,事件流隔离;先做同步模式(简单),异步模式后置。

---

## 2.2 多类型 Task 框架(ITaskImplementation)[P0][M][对齐]

**背景**:Claude Code 有 6+1 种 Task 类型(local_bash/local_agent/remote_agent/in_process_teammate/local_workflow/dream + main-session 复用),各自有不同隔离(子进程/in-process/worktree/remote/fork)和通信(stdout/邮箱/HTTP/onMessage)。TLAH 仅 `BackgroundTask.Kind` 的 `task`/`shell`/`agent`(运行时分类,非编排角色),无 `subagent_type` 概念。单一 Kind 无法承载不同隔离/通信语义。

**现状**:
- `TLAHStudio.Core/Services/Background/BackgroundTaskService.cs:10-13` — `BackgroundTaskRecord`,单一 Kind
- `:51-118` — `CreateAsync` 跑 `Func<CancellationToken, Task>`

**Claude Code 对标**:
- `Task.ts:6-13` — `TaskType` 联合类型
- `Task.ts:15-20` — `pending→running→{completed|failed|killed}` 状态机
- `Task.ts:72-76` — 极简 `Task` 接口(仅 `name`/`type`/`kill`)
- `tasks.ts:22-32` — `getAllTasks`/`getTaskByType` 注册表

**实现要点**:
1. 在 `BackgroundTaskService` 之上抽象 `ITaskImplementation` 接口(等效 CC `Task` 接口 + `getTaskByType`):
   ```csharp
   public interface ITaskImplementation {
       string Type { get; }
       Task RunAsync(CancellationToken ct);
       Task KillAsync();
   }
   ```
2. `BackgroundTaskRecord` 加 `Type` 字段(枚举:`local_shell`/`local_agent`/`remote_agent`/`in_process_teammate`/`dream`,本阶段先落地前两个,其余预留)。
3. `local_shell`:已有 sandbox 命令执行(迁移现有 `BackgroundTaskService` 的 shell 路径)。
4. `local_agent`:新增,用 2.1 的 `spawn_agent` 引擎跑后台 agent(异步模式)。
5. `ITaskRegistry` 注册表:`GetByType(string type) → ITaskImplementation`(等效 `getTaskByType`)。
6. `BackgroundTaskService.CreateAsync` 按 `Type` 选 `ITaskImplementation` 委派执行。
7. 状态机统一为 `pending→running→{completed|failed|killed}`(对齐 CC,TLAH 现有 `completed/cancelled/failed` 调整为 `completed/killed/failed`)。

**验收标准**:
- [ ] `ITaskImplementation` 接口与 `ITaskRegistry` 就位
- [ ] `local_shell` 类型迁移现有 shell 执行,行为不变
- [ ] `local_agent` 类型用 spawn_agent 引擎跑后台 agent
- [ ] `remote_agent`/`in_process_teammate`/`dream` 类型预留(接口存在,实现抛 NotImplementedException 或返回未启用)
- [ ] 状态机统一为 pending/running/completed/failed/killed
- [ ] 新增单测:类型路由、状态机转换

**依赖**:2.1(local_agent 用 spawn_agent)

**风险**:中。状态机从 `cancelled` 改 `killed` 需迁移现有数据(`ApplyLightweightMigrations` 加列)。缓解:DB 迁移时 `cancelled`→`killed` 映射。

---

## 2.3 邮箱消费者(激活 PendingMessages)[P1][M][激活死代码]

**背景**:TLAH 的 `task_send_message` 只把消息 append 到输出 markdown 文件,**没有 worker 消费这个邮箱**——是 write-only 死代码。Claude Code 的 `pendingMessages` 由 `drainPendingMessages` 在工具轮边界排空,停止的代理可被 `resumeAgentBackground` 唤醒。激活邮箱消费者即激活代理间通信。

**现状**:
- `TLAHStudio.Core/Services/Background/BackgroundTaskService.cs:171-200` — `SendMessageAsync` 仅 append `"[message <timestamp>] <text>"` 到输出文件
- 无 worker 轮询/排空

**Claude Code 对标**:
- `tasks/LocalAgentTask/LocalAgentTask.tsx:181-192` — `drainPendingMessages`,工具轮边界排空 `pendingMessages`
- `tasks/LocalAgentTask/LocalAgentTask.tsx:197-262` — 完成通知
- `resumeAgentBackground` — 停止的代理收消息时自动 resume
- `tools/SendMessageTool/SendMessageTool.ts:802-874` — 直接队列(进程内子代理),`queuePendingMessage`

**实现要点**:
1. `BackgroundTaskRecord` 加 `PendingMessages` 队列(`ConcurrentQueue<AgentMessage>`,线程安全)。
2. `SendMessageAsync` 改为入队 `PendingMessages`(而非 append 文件),同时保留输出文件记录(可追溯)。
3. 后台 agent(`local_agent` 类型)在**工具轮边界**(每轮 model 响应后、下轮工具执行前)调 `DrainPendingMessages`,把队列消息作为 user-role turn 注入子 run 上下文。
4. **停止的 agent 收消息自动 resume**:若 `SendMessageAsync` 时目标 agent 已停止(completed/killed),自动 `resumeAgentBackground` 等效——新建 run 续跑,首条消息为收到的消息。
5. 路由:按 `agentId`(本阶段)而非 name;预留 name 路由(配合未来 Team)。
6. 结构化消息:支持 `shutdown_request`/`shutdown_response`/`plan_approval_response`(discriminated union,参考 `SendMessageTool.ts:46-65`),本阶段先做纯文本 + shutdown。

**验收标准**:
- [ ] `task_send_message` 消息入 `PendingMessages` 队列(非仅 append 文件)
- [ ] 后台 agent 工具轮边界排空队列,消息作为 user turn 注入
- [ ] 停止的 agent 收消息自动 resume 续跑
- [ ] 输出文件仍记录消息历史(可追溯)
- [ ] 新增单测:队列入队/排空、停止 agent resume

**依赖**:2.1、2.2(local_agent 类型)

**风险**:中。工具轮边界注入消息需与 `AgentRunEngine` 的循环结构协调(在哪一步插入 drain)。缓解:在 `PrepareContextAsync` 后、`ProviderStreamAdapter.ChatAsync` 前插入 drain 点。

---

## 2.4 `notified` 原子锁 + `outputOffset` 增量投递 [P1][S][对齐]

**背景**:TLAH 的 `BackgroundTaskService.StopAsync` 不更新 DB 状态(靠 `Task.Run` catch 块),存在 stop 与自然完成的竞态(重复通知/状态不一致)。且 `task_output` 每次读全文件,长输出重复占用上下文 token。Claude Code 用 `notified` 原子锁(check-and-set)防重复终结,用 `outputOffset` 只投递增量。

**现状**:
- `TLAHStudio.Core/Services/Background/BackgroundTaskService.cs:164-169` — `StopAsync` 不更新 DB
- `TLAHStudio.Core/Services/TaskAgentTools.cs:415-452` — `task_output` 读全文件

**Claude Code 对标**:
- `Task.ts:50` — `notified` 原子锁,每条终结路径 check-and-set
- `Task.ts:51` — `outputOffset` 游标,只向模型投递增量
- `utils/task/framework.ts:190` — `getTaskOutputDelta(taskId, offset)`
- `tasks/stopTask.ts:38-100` — shell 任务抑制退出码通知、agent 任务带 partial result

**实现要点**:
1. `BackgroundTaskRecord` 加 `Notified` 布尔 + `OutputOffset` long。
2. **`notified` 锁**:`StopAsync` 与 `Task.Run` 完成回调都用 check-and-set `Notified`(Interlocked.CompareExchange),只有第一个终结路径发通知,防重复。
3. **stopTask 区分**:shell 任务(`local_shell`)stop 时抑制 "exit code 137" 噪音通知但发 SDK `task_terminated`;agent 任务(`local_agent`)stop 时带 partial result(提取最后 assistant 文本)。
4. **`outputOffset` 增量**:`task_output` 默认只读 `OutputOffset` 之后的新增内容,返回新 offset;显式 `full=true` 读全量。
5. `task_output` schema 加 `full?: bool` 参数。
6. `BackgroundTaskService` 写输出文件时维护 `OutputOffset`(文件长度)。

**验收标准**:
- [ ] `Notified` 原子锁生效:stop 与自然完成竞态时只发一次通知
- [ ] shell 任务 stop 抑制退出码噪音,agent 任务 stop 带 partial result
- [ ] `task_output` 默认读增量,返回新 offset
- [ ] `task_output` 显式 `full=true` 读全量
- [ ] 新增单测:竞态 check-and-set、增量读取

**依赖**:2.2(状态机统一后,Notified 字段语义清晰)

**风险**:低。注意 `OutputOffset` 在文件被截断(超 `MAX_TASK_OUTPUT_BYTES`)时的处理——截断后 offset 重置。

---

## 2.5 任务依赖边(Blocks/BlockedBy)+ owner [P1][M][激活死代码]

**背景**:TLAH 的 `AgentTaskItem.ParentTaskId` 在 `AgentTaskService` 中仅赋值从不查询——无依赖边、无 owner。Claude Code 的 `TaskUpdate` 支持 `addBlocks`/`addBlockedBy` 建方向边,`task_list` 渲染 `blocked by #x`;改 owner 时往新 owner 邮箱写 `task_assignment`。激活依赖边 + owner 是多代理任务分配的前提。

**现状**:
- `TLAHStudio.Core/Services/AgentTaskService.cs:222-223` — `ParentTaskId` 仅赋值,从不查询
- `AgentTaskItem` 无 owner 字段

**Claude Code 对标**:
- `tools/TaskUpdateTool/TaskUpdateTool.ts:277-298` — `addBlocks`/`addBlockedBy` 调 `blockTask` 建方向边
- `tools/TaskUpdateTool/TaskUpdateTool.ts:188-199` — 改 owner 时自动认领,往新 owner 邮箱写 `task_assignment`
- `tools/TaskListTool` — 渲染 `blocked by #x`

**实现要点**:
1. `AgentTaskItem` 加 `Owner`(string?)、`Blocks`(List<int>)、`BlockedBy`(List<int>)字段。
2. DB 迁移(`ApplyLightweightMigrations`):加列 + 依赖关系表 `AgentTaskDependency(TaskId, BlocksTaskId)`(或 JSON 列存数组)。
3. `task_update` schema 加 `add_blocks`/`add_blocked_by`/`owner` 参数。
4. `AgentTaskService.UpdateAsync` 实现依赖边维护:加边时双向更新(当前任务的 `Blocks` + 目标任务的 `BlockedBy`)。
5. `task_list` 渲染依赖:`blocked by #x` 标注;可选过滤被阻塞任务(`include_blocked` 参数)。
6. **owner 分配**:改 owner 时,若 owner 是活跃 agentId,经 2.3 邮箱发 `task_assignment` 消息(本阶段先记录 owner,邮箱通知待 2.3 完成后接)。
7. **调度阻塞**:被阻塞任务(`BlockedBy` 非空且有未完成任务)不可被 agent 认领为 in_progress(配合 2.6 DAG 调度器)。

**验收标准**:
- [ ] `task_update` 可建 `add_blocks`/`add_blocked_by` 方向边
- [ ] `task_list` 渲染 `blocked by #x`
- [ ] owner 字段可设置,改 owner 时(若 owner 活跃)发邮箱通知
- [ ] 被阻塞任务不可认领为 in_progress
- [ ] DB 迁移不丢现有任务数据
- [ ] 新增单测:依赖边双向更新、阻塞判定、owner 分配

**依赖**:无(可与 2.1/2.2 并行;邮箱通知部分依赖 2.3)

**风险**:中。DB schema 迁移需谨慎(现有 `ParentTaskId` 数据保留)。缓解:依赖关系用新表,不动 `ParentTaskId` 列。

---

## 2.6 确定性 DAG 协调器 [P1][L][超越]

**背景**:Claude Code 的 coordinatorMode 让顶层 LLM 仅做编排,在单条消息里发多个 `AgentTool` 调用并行扇出,但**工作分配由协调器 LLM 自行决定**——靠判断高/低上下文重叠决定 continue/spawn(`coordinatorMode.ts:282-293`),易出错。TLAH 可用 C# 强类型 DAG(任务依赖图)做**确定性编排**——这是超越点:声明式 DAG 由代码调度并行/串行,LLM 仅写子任务 prompt,调度正确性由类型系统保证而非 LLM 判断。

**Claude Code 对标**:
- `coordinator/coordinatorMode.ts:36-41` — 双重门控(feature flag + env)
- `coordinator/coordinatorMode.ts:128-133` — 协调器工具集限制为 AgentTool/SendMessage/TaskStop/SyntheticOutput
- `coordinator/coordinatorMode.ts:144-160` — `<task-notification>` 结果注入
- `coordinator/coordinatorMode.ts:170-176` — 单消息多 AgentTool 并行
- `coordinator/coordinatorMode.ts:200-218` — 分阶段指导(研究并行扇出、实现按文件集串行、验证可并行)
- `coordinator/coordinatorMode.ts:282-293` — continue vs spawn(LLM 判断)

**实现要点**:
1. **DAG 模型**:`TaskDag` 由节点(`DagNode`:子任务 prompt + subagent_type + token_budget + allowed_tools)与边(依赖关系,复用 2.5 的 Blocks/BlockedBy)组成。
2. **内置调度模式**(声明式):
   - `fan_out_fan_in`:N 个独立子任务并行,全部完成后协调器综合。
   - `pipeline`:子任务串行,前一个输出作为后一个输入。
   - `scatter_gather`:scatter 分片并行 + gather 汇总。
3. **协调器 agent**:顶层 agent 工具集限制为 `spawn_agent`/`task_send_message`/`task_stop`/`task_list`(自身不做文件/bash),仅编排。
4. **调度器**(C# 强类型):读 DAG,按依赖就绪度调度——无依赖的节点并行 `spawn_agent`(用 2.1),完成的节点结果作为 `<task-notification>` 注入协调器,协调器决定是否触发下游节点(或调度器按 DAG 自动触发)。
5. **两种模式**:
   - **自动模式**(确定性):调度器按 DAG 自动触发下游节点,LLM 仅写各节点 prompt;协调器只在 gather 节点综合。
   - **半自动模式**(对齐 CC):协调器 LLM 看完上游结果后决定触发下游,调度器只保证"不违反依赖"。
6. **DAG 可视化**(GUI 优势):WinUI 展示 DAG 图(节点状态 pending/running/completed + 依赖边),实时更新。
7. `spawn_agent` 增加 `dag_id` 参数,子代理注册到 DAG 节点。

**验收标准**:
- [ ] 可声明 DAG(节点 + 依赖边),调度器按依赖就绪度并行/串行执行
- [ ] `fan_out_fan_in`/`pipeline`/`scatter_gather` 三种内置模式可运行
- [ ] 协调器工具集受限(不做文件/bash)
- [ ] 节点结果以 `<task-notification>` 注入
- [ ] 自动模式下,下游节点在上游完成后自动触发(确定性)
- [ ] WinUI DAG 可视化展示节点状态与依赖
- [ ] 不违反依赖(被阻塞节点不提前执行)
- [ ] 新增单测:DAG 拓扑排序、并行调度、依赖违反检测、三种模式

**依赖**:2.1(spawn_agent)、2.5(依赖边)

**风险**:高。DAG 调度器正确性(拓扑排序、循环检测、死锁)难保证。缓解:先做无环 DAG(强制 DAG 校验,有环报错);调度器用成熟的拓扑排序算法;充分单测;自动模式先做 `fan_out_fan_in` 一种,逐步扩展。

**超越价值**:Claude Code 全靠 LLM 判断 continue/spawn,易在上下文重叠判断上出错;TLAH 用 C# 强类型 DAG 保证调度正确性,且提供 GUI 可视化——这是"超越"的核心差异化。

---

## 2.7 子代理 token 预算硬上限 [P2][M][超越]

**背景**:Claude Code 无显式 per-agent token 预算(仅 `maxTurns`,`runAgent.ts:756`),失控子代理可烧光配额。TLAH 已有 `TokenBudgetService`,可给子代理设硬上限,超限即终结返回 partial——这是超越点。

**Claude Code 对标**:
- `tools/AgentTool/runAgent.ts:756` — 仅 `maxTurns` 限制,无 token 硬上限
- `getTokenCountFromUsage` — 跟踪但不断流

**实现要点**:
1. `spawn_agent` schema 加 `token_budget?: int`(可选,默认继承父代理剩余预算的一定比例)。
2. 子 run 的 `AgentRunState` 记 `TokenBudgetCeiling`(`TokenBudgetUsed` 已有,`AgentRunState.cs:11-41`)。
3. `AgentRunEngine` 每轮检查:`TokenBudgetUsed >= TokenBudgetCeiling` 时,终结子 run,状态 `completed`(带 `budget_exceeded` 标记),返回 partial result(最后 assistant 文本 + 已完成工具调用)。
4. 父代理可见子代理预算消耗:`<task-notification>` 含 `tokens_used`/`token_budget`/`budget_exceeded`。
5. 协调器(2.6)可按预算分配 DAG 节点预算。

**验收标准**:
- [ ] `spawn_agent` 接受 `token_budget`,子 run 超限即终结
- [ ] 终结返回 partial result(非崩溃)
- [ ] 父代理可见子代理 token 消耗与是否超限
- [ ] 默认预算(未显式指定)合理(如父剩余的 25%)
- [ ] 新增单测:超限终结、partial result 提取、默认预算计算

**依赖**:2.1

**风险**:中。token 估算用 TLAH 的 CJK 感知算法,需确保子 run 的 `TokenBudgetUsed` 准确累加(含工具结果)。缓解:每轮用 `ITokenBudgetService` 重算累计。

**超越价值**:CC 无此能力;TLAH 用硬上限防失控子代理烧配额,适合长任务编排场景。

---

## 2.8 零成本 Dream(跨会话记忆巩固)[P2][M][超越]

**背景**:Claude Code 的 dream 是唯一自动触发的任务类型——空闲时跨会话巩固记忆,但用 fork-agent(LLM)跑 4 阶段巩固 prompt,有 API 成本。TLAH 已有确定性零成本 `SessionMemoryService`,可直接复用做 Dream,无需 LLM fork——比 CC 更省 token,是天然优势。

**Claude Code 对标**:
- `services/autoDream/autoDream.ts:95-190` — 5 道门控(功能开关/时间≥24h/扫描节流/会话≥5/锁)
- `tasks/DreamTask/DreamTask.ts:52-74` — dream 任务注册
- `services/autoDream/consolidationPrompt.ts:15-64` — 4 阶段 prompt(Orient/Gather/Consolidate/Prune-and-index)
- `services/autoDream/autoDream.ts:281-313` — progress watcher
- 触发:`stopHooks` 在每轮后触发 `executeAutoDream`(`autoDream.ts:319-324`)

**实现要点**:
1. **触发**:空闲时(无活跃 run + 距上次 Dream ≥N 轮/会话)触发,由 `AgentRunEngine` 的 run 结束钩子或后台计时器检查。
2. **5 道门控**(从低到高成本):
   - 功能开关(Dream 默认关,用户可开)。
   - 时间门:距上次 Dream ≥24h。
   - 扫描节流:每 10 分钟最多一次扫描。
   - 会话门:自上次 Dream 以来 ≥5 个会话(排除当前)。
   - 锁:`tryAcquireDreamLock`(防并发 Dream)。
3. **确定性巩固(超越点)**:读所有会话的 `.tlah_context/session-memory.md`(零 LLM 成本,复用 `SessionMemoryService` 的提取结果),合并/去重/精炼,写入 `memory_directory`(已有 `IMemoryDirectoryService`)。
   - 合并策略:按主题(files/commands/errors/learnings/open-questions)聚合,去重相同条目,保留最新。
   - 无需 LLM fork(CC 用 fork-agent,TLAH 用确定性合并)。
4. **可选 LLM 精炼**(开关):关键节点可选叠加一次轻量 LLM 精炼(用 small/fast 模型),生成高层 learnings——默认关,保留确定性为主。
5. **进度回传**:Dream 进度经 `IAgentEventStream` 流式回传 UI(Dream 面板),UI-only 无模型通知(参考 `DreamTask.ts:110-112`)。
6. **锁回滚**:kill 时 `rollbackDreamLock(priorMtime)` 让时间门重过(参考 `autoDream.ts:150-155`)。
7. **会话门数据**:需记录"上次 Dream 时的会话数/时间",存 `GlobalSettings` 或 sidecar。

**验收标准**:
- [ ] 空闲时(无活跃 run)满足 5 道门控后触发 Dream
- [ ] Dream 读各会话 session-memory.md,确定性合并写入 memory_directory
- [ ] 无 LLM 调用(零成本,日志验证)
- [ ] 可选 LLM 精炼开关,默认关
- [ ] 进度流式回传 UI
- [ ] 锁防并发 Dream,kill 时回滚
- [ ] 新增单测:5 道门控判定、确定性合并去重、锁

**依赖**:0.7(SessionMemory 节流,确保 session-memory.md 有效)

**风险**:中。跨会话读多个 session-memory.md 需定位各会话的 sandbox 路径(会话可能用不同 workspace)。缓解:从 DB 的 `Chat` 表查各会话 sandbox 路径;无 sandbox 的会话跳过。

**超越价值**:CC 的 Dream 用 fork-agent(LLM)有成本;TLAH 复用确定性 SessionMemory 零成本巩固,且 TLAH 的确定性提取本就优于 CC 的 LLM 提取——Dream 是这一优势的跨会话延伸。

---

## 版本交付定义(5.0.0 Definition of Done)

1. **代码**:2.1-2.8 全部验收标准勾选;`dotnet build TLAHStudio.sln -c Release` 无错误无警告。
2. **测试**:
   - 新增覆盖:子代理上下文/权限隔离、多类型 Task 路由、邮箱排空/resume、notified 锁/outputOffset、依赖边/owner、DAG 拓扑排序/三种模式/依赖违反、token 预算超限、Dream 5 道门控/确定性合并。
   - **重点**:2.1(子代理隔离)与 2.6(DAG 调度器)需充分测试,建议增加集成测试(端到端 spawn_agent → 结果回传)。
   - `dotnet test TLAHStudio.Core.Tests -c Release` 全绿。
3. **CI**:`.\tools\ci.ps1 -Configuration Release -Platform x64` 通过。
4. **版本同步**:`appsettings.json`、各 `.csproj`、`TLAHStudio.Installer/version.json`、`setup.iss` → `5.0.0`。
5. **文档**:CLAUDE.md 新增"子代理编排"章节(spawn_agent、多类型 Task、DAG 协调器、Dream);本文件 2.1-2.8 标记完成。
6. **发布**:tag `v5.0.0` → `build-release.ps1` → `verify-release.ps1` → SCP 上传。
7. **回归**:5.0.0 是大版本,发布前需在真实长任务(多步工具调用 + 压缩 + 子代理)下回归测试,确认无 4.x 的稳定性退化。

---

## 测试策略要点

- **子代理隔离测试**:构造父 run,spawn 子 agent,断言子 agent 的 messages 不含父历史、工具权限被限制、结果以 XML 注入父。
- **DAG 调度器测试**:构造含并行/串行/分支的 DAG,断言拓扑序正确、无依赖违反、三种模式输出符合预期;构造含环的 DAG,断言报错。
- **Dream 测试**:mock 5 道门控的输入(时间/会话数/锁),断言触发判定;构造多个 session-memory.md,断言合并去重正确。
- **集成测试**:端到端——用户发任务 → 协调器 DAG fan-out → 3 个子代理并行 → gather → 综合回复,全流程无崩溃。

---

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-07-04 | 初版,定义 5.0.0 八项多代理编排任务(含三项超越:DAG 协调器、token 预算、零成本 Dream) |
