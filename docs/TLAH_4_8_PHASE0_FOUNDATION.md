# Phase 0:地基修复与正确性(4.8.0)

> **阶段目标**:在引入任何新能力前,先修好已写错或已写好但没接通的代码。本阶段 7 项任务几乎都是小到中工作量、低风险、高收益,且为 Phase 1/2 扫清地基。主题是"激活死代码 + 修脚枪 + 补长会话稳定性命门"。
>
> **版本**:4.8.0(当前 4.7.0)
> **前置依赖**:无
> **后续影响**:0.3(压缩链)、0.6(重注入)是 Phase 1.3(SkillTool)、Phase 2.1(spawn_agent)的硬依赖;0.7(SessionMemory 节流)是 Phase 2.8(零成本 Dream)的前置。
>
> **路线图**:见 [TLAH_5_0_ROADMAP.md](./TLAH_5_0_ROADMAP.md)

---

## 任务清单总览

| # | 任务 | 优先级 | 工作量 | 类型 | 依赖 |
|---|---|---|---|---|---|
| 0.1 | 权限模式默认回退改向 | P0 | S | 修脚枪 | 无 |
| 0.2 | Microcompact 持久化引用路径修正 | P0 | S | 修独有 bug | 无 |
| 0.3 | 接入 ModelAssistedSummarize + 渐进升级链 | P0 | S | 激活死代码 | 无 |
| 0.4 | PTL(prompt-too-long)重试循环 | P0 | M | 对齐 | 0.3 |
| 0.5 | 断路器改"跳过"而非 abort run | P0 | S | 对齐 | 无 |
| 0.6 | post-compact 重注入补全(工具 + MCP) | P1 | M | 对齐 | 0.3 |
| 0.7 | SessionMemory 节流阈值 | P1 | S | 对齐 | 无 |

> 0.1、0.2、0.3、0.5、0.7 互不依赖,可并行启动。

---

## 0.1 权限模式默认回退改向 [P0][S][修脚枪]

**背景**:权限模式的 `Normalize` 方法在遇到未知/null/空值时默认回退到 `BypassPermissions`(最宽松档)。这是脚枪——任何配置异常或迁移残留都会让 agent 静默落到"全放行"。Claude Code 的 `default` 模式是最严档,未知值落最严才符合最小权限原则。

**现状**:`TLAHStudio.Core/Services/AgentPermissionModes.cs:9-20`
```csharp
// :19 — 默认回退到 BypassPermissions
return AgentPermissionModes.BypassPermissions;
```
方向与 Claude Code 相反:CC 的 `default` 落到最严(`types/permissions.ts:18-22`),TLAH 的未知值落到最宽松。

**Claude Code 对标**:
- `types/permissions.ts:18-22` — `default` 是基线最严档
- `permissions.ts:1158-1319` — `hasPermissionsToUseToolInner` 在 `default` 下无规则匹配则转 `ask`

**实现要点**:
1. `Normalize` 未知/null/空值回退改为 `RequestApproval`(最严)。
2. 检查所有 `Normalize` 调用方,确认显式设置 `BypassPermissions`/`AutoApprove` 的用户不受影响(显式值原样返回)。
3. 检查 `GlobalSettings` 首次种子(Seed)写入的权限模式默认值——若种子写空或写 Bypass,改为写 `RequestApproval` 或留空(由 Normalize 落到 RequestApproval)。
4. 审查 `AgentRunEngine.cs:497-502` 的权限决策路径,确认 `RequestApproval` 作为默认值不会破坏现有 AutoApprove 用户的体验(他们显式设了 AutoApprove)。

**验收标准**:
- [ ] `Normalize(null)` / `Normalize("")` / `Normalize("unknown")` 返回 `RequestApproval`
- [ ] `Normalize("bypass")` / `Normalize("auto")` / `Normalize("ask")` 仍返回对应档(别名保留)
- [ ] 全新安装(空 GlobalSettings)首次启动权限模式为 `RequestApproval`
- [ ] 已存在显式 `BypassPermissions` 配置的用户升级后模式不变
- [ ] 新增单测覆盖以上分支

**依赖**:无

**风险**:低。若有用户依赖"未配置即全放行"的旧行为,升级后会感到变严。缓解:升级说明里明确告知;或在 GlobalSettings 种子里为存量用户保留原值(读旧值非空则不覆盖)。

---

## 0.2 Microcompact 持久化引用路径修正 [P0][S][修独有 bug]

**背景**:Microcompact 压缩时把旧工具结果替换为合成引用 `tool-{i:D4}.json`,但真实持久化文件名是 `{invocationId:N}-{toolName}.txt`。两者不一致导致 `read_persisted_output` 工具无法读回被压缩的输出——模型在长会话中"看得见引用却读不回内容",等于丢上下文。这是 TLAH 独有 bug,Claude Code 用 `ContentReplacementState` 冻结结果命运保持引用一致可读。

**现状**:
- `TLAHStudio.Core/Services/Context/ReactiveCompactor.cs:115-118` — Microcompact 替换为合成引用 `tool-{i:D4}.json`
- `TLAHStudio.Core/Services/AgentContextServices.cs:315-318` — 真实持久化文件名 `{invocationId:N}-{toolName}.txt`
- `TLAHStudio.Core/Services/TaskAgentTools.cs:256-297` — `read_persisted_output` 按真实文件名定位

**Claude Code 对标**:
- `utils/toolResultStorage.ts:769-909` — `enforceToolResultBudget` + `ContentReplacementState`,已见结果的命运被冻结,持久化引用与真实文件一致,可读回

**实现要点**:
1. 读取 `ReactiveCompactor.cs:88-128` 的 Microcompact 完整实现,确认替换时掌握哪些信息(是否知道 invocationId)。
2. Microcompact 替换旧 tool 结果时,若该结果已持久化(存在真实文件),引用真实文件名 `{invocationId:N}-{toolName}.txt`;若未持久化(原始结果 <6K 未落盘),先触发一次持久化再引用,或在引用元数据中明确标记"不可读回"。
3. 统一引用格式:建议在替换文本中嵌入 `invocationId`,如 `[persisted-output: {invocationId}-{toolName}]`,与 `read_persisted_output` 的定位逻辑对齐。
4. `read_persisted_output` 增加按 invocationId 模糊匹配的兜底(从引用文本提取 invocationId)。

**验收标准**:
- [ ] Microcompact 替换的引用与真实持久化文件名一致
- [ ] 模型调用 `read_persisted_output` 能读回 Microcompact 压缩掉的任意工具结果
- [ ] 未持久化的小结果经 Microcompact 后要么可读回(先落盘)要么明确标记不可读回
- [ ] 新增单测:构造 >6K 工具结果 → 触发 Microcompact → read_persisted_output 读回原内容

**依赖**:无

**风险**:低。需注意 Microcompact 可能替换尚未持久化的结果(<6K),处理"先落盘再引用"会增加少量 IO,可加阈值(仅对 >2K 的结果落盘)。

---

## 0.3 接入 ModelAssistedSummarize + 渐进升级链 [P0][S][激活死代码]

**背景**:`ReactiveCompactor` 声明了 5 级压缩枚举,但 `CompactAsync` 的 switch 让 `ModelAssistedSummarize` 落入默认 no-op 分支。同时 `IModelAssistedCompactor` 接口及实现已存在(`:309-368`)却未接入。结果是长会话从 `SummarizeMiddle`(结构化摘要)直接跳到 `EmergencyTruncate`(暴力截断),跳过了 LLM 摘要这一关键中间档,信息损失过大。且单步只跑一级,无"低级不够再升级"的渐进链。

**现状**:
- `TLAHStudio.Core/Services/Context/ReactiveCompactor.cs:9-16` — 5 级枚举
- `TLAHStudio.Core/Services/Context/ReactiveCompactor.cs:57-64` — `CompactAsync` switch,`ModelAssistedSummarize` 落入 `_` 默认分支(no-op)
- `TLAHStudio.Core/Services/Context/ReactiveCompactor.cs:309-368` — `IModelAssistedCompactor` 接口与实现已存在
- `TLAHStudio.Core/Services/AgentRuntime/AgentRunEngine.cs:695-700` — 触发映射:`Blocking/CompactNow → SummarizeMiddle`,`CompactSoon → Microcompact`,其余 → TrimToolOutputs

**Claude Code 对标**:
- `services/compact/autoCompact.ts:288-351` — 多级回退链:SessionMemory 压缩(优先,零成本)→ Microcompact → 全量摘要(Model-assisted)+ PTL 重试

**实现要点**:
1. `CompactAsync` switch 增加 `ModelAssistedSummarize` 分支,调用 `IModelAssistedCompactor.GenerateSummaryAsync`。
2. 在 `SummarizeMiddle` 之后、`EmergencyTruncate` 之前插入该档。
3. `AgentRunEngine` 增加渐进升级逻辑:执行某级压缩后,若 savings 不足(仍超阈值),自动升级到下一级重试,直到 `EmergencyTruncate` 兜底。
4. 保留 `SummarizeMiddle` 保留所有 user 消息的特性(`ReactiveCompactor.cs:141-148`),`ModelAssistedSummarize` 在其基础上用 LLM 生成更完整摘要。

**验收标准**:
- [ ] 触发 `ModelAssistedSummarize` 级别时实际调用 LLM 生成摘要(可在日志验证)
- [ ] Microcompact 后仍超阈值 → 自动升级到 SummarizeMiddle → 仍超 → ModelAssistedSummarize → 仍超 → EmergencyTruncate
- [ ] 每级压缩后记录 savings(压缩前后 token 数)到 run 事件
- [ ] 新增单测:mock IModelAssistedCompactor,验证升级链按序触发

**依赖**:无(0.4 依赖本项的压缩链就位)

**风险**:低。`IModelAssistedCompactor` 实现已存在,主要是接线。注意渐进升级可能增加单次压缩的 LLM 调用,需设上限(如最多升级 2 级)防失控。

---

## 0.4 PTL(prompt-too-long)重试循环 [P0][M][对齐]

**背景**:Provider 返回 context-limit 错误时,TLAH 只 forceCompact 一次重试;若压缩后仍超长则直接失败,长会话不可恢复。Claude Code 在压缩 API 自身超长时,按 API-round 分组丢弃最旧组后重试最多 3 次。

**现状**:`TLAHStudio.Core/Services/AgentRuntime/AgentRunEngine.cs:260-328` — `IsContextLimitError` 触发 forceCompact 后只重试一次

**Claude Code 对标**:
- `services/compact/compact.ts:243-291` — `truncateHeadForPTLRetry`,按 API-round 分组丢弃最旧组
- `services/compact/compact.ts:464-491` — PTL 重试循环,最多 3 次

**实现要点**:
1. `IsContextLimitError` 分支:forceCompact 后若仍 PTL,进入 `truncateHeadForPTLRetry` 等价逻辑。
2. 按 API-round(一轮 model 请求 + 其工具结果)分组,从最旧开始丢弃一组,重试发送。
3. 最多重试 3 次;每次丢弃后记录丢弃的 round 数与 token 估算。
4. 保留 head(系统消息 + 首条 user)与 tail(最近 N 条)不丢,与 `SummarizeMiddle` 的 head/tail 保留策略一致。
5. 结合 0.3 的渐进升级链:PTL 时先尝试升级压缩,升级仍失败再走 truncateHead。

**验收标准**:
- [ ] 构造超长上下文(模拟 provider 返回 context-limit),压缩后仍超长时,自动丢最旧 API-round 重试
- [ ] 最多重试 3 次,3 次后仍失败才报错
- [ ] head/tail 在 truncate 过程中保留
- [ ] 日志记录每次丢弃的 round 数与重试次数
- [ ] 新增单测:mock provider 首次返回 context-limit,验证重试链

**依赖**:0.3(渐进升级链就位后,PTL 路径更清晰)

**风险**:中。需准确定义"API-round"边界(model 请求 → 工具调用 → 工具结果 → 下个 model 请求为一组),边界判断错误会丢错消息。缓解:参考 CC 的分组逻辑,先单测覆盖。

---

## 0.5 断路器改"跳过"而非 abort run [P0][S][对齐]

**背景**:连续压缩失败超阈值时,TLAH 直接 `state.Status = Failed` 终止整个 run。Claude Code 连续失败 3 次后跳过后续 autocompact 尝试、会话继续(可能返回部分结果)。压缩失败不应让用户失去整个会话。CC 的数据(1279 会话出现 50+ 次连续失败)证明"跳过"更稳健。

**现状**:`TLAHStudio.Core/Services/AgentRuntime/AgentRunEngine.cs:264-278` — `consecutiveCompactionFailures > maxCompactionFailures` → `state.Status = Failed`,abort run

**Claude Code 对标**:
- `services/compact/autoCompact.ts:260-265` — `MAX_CONSECUTIVE_AUTOCOMPACT_FAILURES = 3`,连续失败 3 次后跳过后续 autocompact,会话继续
- `services/compact/autoCompact.ts:70` — 常量定义,注释提及 1279 会话 50+ 次失败的教训

**实现要点**:
1. `consecutiveCompactionFailures > maxCompactionFailures` 时,不置 `Failed`,改为:跳过本次压缩,继续当前步(用未压缩上下文尝试发送,若 provider 仍报 PTL 则走 0.4 的 truncateHead)。
2. 标记 `state.CompactionDisabled = true`(新增字段),后续步骤不再尝试自动压缩,直到用户手动 `/compact` 或会话重置。
3. 发出 `CompactionSkipped` 事件通知 UI,提示用户"自动压缩已禁用,建议手动清理或开新会话"。
4. 保留单次压缩失败的内部重试(现有逻辑),仅改"连续超阈值"的终结行为。

**验收标准**:
- [ ] 连续压缩失败 3 次后,会话不终止,继续响应(可能降级)
- [ ] 后续步骤不再自动触发压缩(CompactionDisabled 生效)
- [ ] UI 收到 CompactionSkipped 提示
- [ ] 用户手动触发压缩可重置 CompactionDisabled
- [ ] 新增单测:mock 连续压缩失败,验证 run 不 Failed

**依赖**:无

**风险**:低。跳过后用未压缩上下文发送可能再次 PTL,由 0.4 的 truncateHead 兜底。

---

## 0.6 post-compact 重注入补全(工具 + MCP)[P1][M][对齐]

**背景**:压缩后 TLAH 只重注入 session memory + 最近读取文件。Claude Code 还重注入可用工具列表 delta、MCP 指令 delta、skill/plan/deferred-tools 状态。导致 TLAH 压缩后模型可能"忘记"可用工具或 MCP 指令,重复调用不存在的工具或丢失 MCP 上下文。

**现状**:`TLAHStudio.Core/Services/AgentRuntime/AgentRunEngine.cs:187-197, 287-296` — 仅重注入 session memory + `BuildPostCompactFileContextAsync`

**Claude Code 对标**:
- `services/compact/compact.ts:532-585` — 重注入最近 5 文件(50K budget)、skill 内容(每 skill 5K,总 25K)、plan、plan-mode、deferred-tools delta、MCP-instructions delta、async-agent 状态
- `services/compact/compact.ts:1415-1599` — delta 计算逻辑

**实现要点**:
1. **本阶段(4.8.0)只做工具 + MCP 两项**(skill/plan 重注入依赖 Phase 1 的 Plan Mode/Skills,留到 4.9.0 补)。
2. 压缩后在 compacted 消息尾部插入"可用工具列表"摘要(工具名 + 一句话描述),设 token 上限(如 2K)。
3. 插入"MCP 指令 delta"——当前活跃 MCP server 的指令摘要,设 token 上限。
4. 复用 `BuildPostCompactFileContextAsync` 的注入位置与格式(`[runtime context]` 块)。
5. 4.9.0 完成 Phase 1 后,补 skill 内容(每 skill 5K,总 25K)与 plan 状态重注入。

**验收标准**(4.8.0 部分):
- [ ] 压缩后系统提示含可用工具列表摘要
- [ ] 压缩后 MCP 指令摘要不丢
- [ ] 重注入总 token 有上限,不超预算
- [ ] 新增单测:触发压缩,验证 compacted 消息含工具 + MCP 摘要
- [ ] (4.9.0 补)skill 内容 + plan 状态重注入

**依赖**:0.3(压缩链就位,重注入插入点稳定)

**风险**:中。重注入会增加压缩后上下文体积,需严格控制每项 token 上限,避免压缩 savings 被重注入吃掉。

---

## 0.7 SessionMemory 节流阈值 [P1][S][对齐]

**背景**:TLAH 的 SessionMemory 每步全量重写,既浪费 IO 又会在会话初期(信息很少时)写入空内容。Claude Code 有 init 阈值(10K token 才初始化)和更新间隔(5K token 增量 + 3 次工具调用才更新),避免无谓写入。

**现状**:
- `TLAHStudio.Core/Services/SessionMemory/SessionMemoryService.cs:75-109` — `ExtractAsync` 每次全量重写
- 调用方 `AgentRunEngine` 每步调 `ExtractAsync`(`SessionMemoryService.cs:35-45` 接口签名含 filesChanged/commandsRun 等,由调用方聚合)

**Claude Code 对标**:
- `services/SessionMemory/sessionMemoryUtils.ts:32-36` — init 阈值 10K token,更新间隔 5K token + 3 次工具调用
- `services/SessionMemory/sessionMemory.ts:134-181` — 双阈值触发逻辑

**实现要点**:
1. `SessionMemoryService` 增加节流状态(累计 token 估算、距上次写入的 token 增量、距上次写入的工具调用数)。
2. `ExtractAsync` 入口判断:若累计 token < init 阈值(10K)→ 跳过(会话初期不写);若距上次写入 < 5K token 增量 且 < 3 次工具调用 → 跳过。
3. 需 `ITokenBudgetService` 提供累计 token 估算(已有 `TokenBudgetService.cs`)。
4. 保留"压缩前强制刷新"路径——`ReadForCompactAsync` 调用前,`AgentRunEngine` 应先调用一次无节流的 `ExtractAsync`(确保压缩时 memory 是最新的)。可加 `ExtractAsync(force: true)` 重载。
5. 原子写(temp + rename,`SessionMemoryService.cs:100-102`)保留。

**验收标准**:
- [ ] 会话初期(累计 <10K token)不写 session-memory.md
- [ ] 达到 init 阈值后,按 5K token 增量或 3 次工具调用间隔更新
- [ ] 压缩前强制刷新一次(force 模式),确保 memory 最新
- [ ] 原子写保留(temp + rename)
- [ ] 新增单测:mock token 估算,验证 init/更新间隔/force 刷新三个分支

**依赖**:无

**风险**:低。节流状态需随 run 持久化(否则 resume 后重置),存入 `AgentRunState` 或 sidecar。注意 `SessionMemoryConfig` 已有 `MinMessageTokensToInit = 10_000`(`SessionMemoryService.cs:13`)——确认该配置是否已被使用,若未使用则激活它。

---

## 版本交付定义(4.8.0 Definition of Done)

1. **代码**:0.1-0.7 全部验收标准勾选;`dotnet build TLAHStudio.sln -c Release` 无错误无警告。
2. **测试**:
   - 新增覆盖:权限模式 Normalize 分支、Microcompact 路径一致性、压缩升级链、PTL 重试、断路器跳过、重注入内容、SessionMemory 节流。
   - `dotnet test TLAHStudio.Core.Tests -c Release` 全绿。
3. **CI**:`.\tools\ci.ps1 -Configuration Release -Platform x64` 通过。
4. **版本同步**:`appsettings.json`、各 `.csproj`、`TLAHStudio.Installer/version.json`、`setup.iss` 版本号 → `4.8.0`。
5. **文档**:CLAUDE.md 的 Context Management 与 Tool Safety Pipeline 章节更新压缩链/断路器/重注入描述;本文件 0.1-0.7 标记完成。
6. **发布**:tag `v4.8.0` → `build-release.ps1` → `verify-release.ps1` → SCP 上传 `download.matrixlabs.cn`。

---

## 测试策略要点

- **压缩链测试**:用 mock `IModelAssistedCompactor` 与 mock provider(可编程返回 context-limit),覆盖升级链与 PTL 重试,不依赖真实 LLM。
- **持久化路径测试**:构造已知 invocationId 的工具结果,触发 Microcompact,断言引用文本含真实文件名,且 `read_persisted_output` 能读回。
- **断路器测试**:mock `ReactiveCompactor` 连续抛异常,断言 run 不进入 Failed 且 `CompactionDisabled` 生效。
- **权限模式测试**:纯单元测试 `Normalize`,无需 Windows 环境。

---

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-07-04 | 初版,定义 4.8.0 七项地基修复任务 |
