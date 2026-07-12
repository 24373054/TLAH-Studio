# TLAH Studio 5.0 演进路线图

> **文档定位**:本文是 TLAH Studio 从 4.7.0 向 5.x 演进的总路线图与文档索引。它定义战略方向、阶段划分、版本映射、依赖关系与取舍记录,是后续四个阶段计划文档的入口。每个任务的实现细节(现状代码引用、Claude Code 对标、实现要点、验收标准)在对应阶段文档中展开。
>
> **读者**:TLAH Studio 维护者、贡献者,以及执行各阶段实现的 Claude Code 实例。
>
> **来源**:本路线图基于对 Claude Code 源码(`_analysis/claude-code-src/`)五个子系统的深度对比研究——上下文与记忆、工具系统与权限模式、子代理编排、Skills/Plugins/OutputStyles、平台基础设施。所有代码引用均经过实际读取验证。

---

## 一、战略定位

### 1.1 TLAH Studio 是什么

TLAH Studio(Talk Like A Human)是 Windows 原生 AI 智能体工作台,基于 C# + WinUI 3 + Windows App SDK(.NET 8)。它捕获每次 LLM API 调用的完整原始 HTTP 请求/响应,提供带工具执行、安全策略、沙箱化的持久智能体运行时。

### 1.2 四张差异化底牌

在与 Claude Code 的对标中,TLAH 有四张不可放弃的底牌。所有"对齐"工作都必须以不损害这四张底牌为前提,所有"超越"工作都应从这四张底牌延伸:

| 底牌 | 内涵 | 对比 Claude Code | 守护原则 |
|---|---|---|---|
| **Windows 原生 GUI 工作台** | WinUI 3 桌面体验,非 CLI | CC 是终端 Ink 应用 | 新能力优先提供 GUI 可视化(技能仪表盘、DAG 编排图、run 回放) |
| **本地优先** | 无云端账号绑定,数据全在 `%LOCALAPPDATA%` | CC 绑定 claude.ai/OAuth | 云端能力(OAuth/同步/策略)设为可选增强,默认关闭 |
| **原始 HTTP 全量捕获** | 每次 LLM 调用的 RawRequest/RawResponse 入库 | CC 无此能力 | 延伸为"可观测回放",做成 CC 没有的调试/审计产品 |
| **CJK 感知 + 确定性零成本记忆** | 中文 token 估算 + 确定性 SessionMemory | CC 用 `length/4`(中文低估 3-6 倍)+ LLM fork 提取记忆 | 保留确定性提取为基线,关键点可选叠加轻量 LLM,不全盘改 LLM 提取 |

### 1.3 研究方法

本路线图最初的差距判断基于对一份本地、独立授权的参考源码快照与 TLAH Studio 4.7.0 的历史对照。参考源码不属于本仓库；所有相关结论仅作为历史设计背景，当前能力应以代码、测试和现行架构文档为准。

---

## 二、现状评估(4.7.0 vs Claude Code)

### 2.1 已领先或对齐的维度

| 维度 | TLAH 实现 | 评估 |
|---|---|---|
| CJK token 估算 | `TokenBudgetService.cs:97-108`(CJK ×1.5, Latin /3.2) | ✅ 领先 |
| 确定性 SessionMemory | `SessionMemoryService.cs`(零 LLM 成本,9 段模板) | ✅ 领先 |
| 安全内核 | `ToolSafetyKernel.cs` + `FlagLevelValidationService.cs`(bypass-immune 路径 + flag 白名单) | ✅ 基本对齐 |
| 工具生命周期 | `IAgentToolV3`(ClassifySafety/PlanEffects/ExecuteWithProgress/Rollback) + `ToolHookRegistry` | ✅ 对齐 |
| 压缩持久化引用 | `AgentContextServices.cs:301-344`(超 6K 持久化到 `.tlah_context/tool-results/`) | ✅ 对齐(但有路径 bug,见 Phase 0) |
| 更新签名 | `UpdateCrypto.cs`(ECDSA P-256 + SHA-256) | ✅ 对齐 |

### 2.2 缺口与死代码

| 维度 | TLAH 状态 | 严重度 |
|---|---|---|
| 压缩策略链 | 5 级枚举但 `ModelAssistedSummarize` 未接入 switch(`ReactiveCompactor.cs:57-64`) | P0 |
| 权限模式 | 3 档,默认回退到最宽松 `BypassPermissions`(`AgentPermissionModes.cs:19`) | P0 脚枪 |
| Microcompact 引用路径 | 合成 `tool-{i:D4}.json` 与真实文件名不一致(`ReactiveCompactor.cs:115-118`) | P0 独有 bug |
| Plan Mode / AskUserQuestion | 完全缺失 | P0 |
| Skills/Plugins | M2.12.0 脚手架,DI 注册但**无消费者**(死代码) | P0 |
| 子代理编排 | 单代理引擎,邮箱 write-only(`BackgroundTaskService.cs:171-200`) | P0 |
| ToolSearch 延迟加载 | 40 工具全 schema 塞每请求 | P1 |
| 任务依赖字段 | `ParentTaskId` 存而不用(`AgentTaskService.cs:222-223`) | P1 死代码 |
| SDK | 命名管道,单连接,无鉴权(`LocalSdkHost.cs:70-89`) | P0 |

### 2.3 核心洞察

**TLAH 有大量"基础设施已就位、只差接通"的死代码**:Skills/Plugins/McpConnectionManager 脚手架、`ParentTaskId`、邮箱、`IModelAssistedCompactor` 接口。激活这些死代码 + 修脚枪是最高 ROI,远比新建子系统划算。这是 Phase 0 排在首位的原因。

---

## 三、阶段总览与版本映射

### 3.1 四阶段路线

| 阶段 | 版本 | 主题 | 核心交付 | 任务数 | 详细文档 |
|---|---|---|---|---|---|
| **Phase 0** | 4.8.0 | 地基修复与正确性 | 修脚枪、修独有 bug、激活压缩中档、PTL 重试、断路器改跳过、重注入补全、SessionMemory 节流 | 7 | [TLAH_4_8_PHASE0_FOUNDATION.md](./TLAH_4_8_PHASE0_FOUNDATION.md) |
| **Phase 1** | 4.9.0 | 智能体自主性 | Plan Mode、AskUserQuestion、SkillTool 接通、渐进式披露、OutputStyles、多源条件技能、Plugin 端到端 | 7 | [TLAH_4_9_PHASE1_AUTONOMY.md](./TLAH_4_9_PHASE1_AUTONOMY.md) |
| **Phase 2** | 5.0.0 | 多代理编排 | spawn_agent、多类型 Task、邮箱消费者、notified 锁、依赖边+owner、确定性 DAG 协调器、token 预算、零成本 Dream | 8 | [TLAH_5_0_PHASE2_ORCHESTRATION.md](./TLAH_5_0_PHASE2_ORCHESTRATION.md) |
| **Phase 3** | 5.1+ | 平台与差异化 | SDK 升级、防休眠通知、限额感知、遥测、ToolSearch、Worktree、Cron、可观测回放、VCR、auto 分类器、file_read/grep、P2P 同步 | 12 | [TLAH_5_1_PHASE3_PLATFORM.md](./TLAH_5_1_PHASE3_PLATFORM.md) |

### 3.2 版本主题原则

延续 TLAH 现有的版本工作流(每版本一个主题,CI → commit → tag → build-release → SCP 上传)。每个 Phase 对应一个主版本,版本交付定义(Definition of Done)在各阶段文档末尾给出。

### 3.3 节奏建议

- **Phase 0**(4.8.0):紧凑修复版,7 项多为小工作量低风险,建议快速迭代。
- **Phase 1**(4.9.0):新功能版,Plan Mode + AskUserQuestion + Skills 三大支柱,可并行启动 1.1/1.2/1.3。
- **Phase 2**(5.0.0):能力跃升大版本,子代理编排从无到有,DAG 协调器是超越重点,需充分测试。
- **Phase 3**(5.1+):平台化,内容多但可分多个小版本(5.1/5.2/5.3)逐步交付,不必一次做完。

---

## 四、依赖关系

### 4.1 跨阶段硬依赖

```
Phase 0.3(接入 ModelAssistedSummarize) ─┐
Phase 0.6(post-compact 重注入补全) ────┼─→ Phase 1.3(SkillTool,依赖重注入保技能上下文)
                                          ├─→ Phase 2.1(spawn_agent,依赖重注入保工具上下文)
                                          └─→ Phase 2.6(DAG 协调器,依赖 2.1)

Phase 0.7(SessionMemory 节流) ─→ Phase 2.8(零成本 Dream,复用 SessionMemory)

Phase 2.1(spawn_agent) ─→ Phase 3.6(Worktree + 子代理自主执行)

Phase 3.1(SDK 升级) ─→ Phase 3.8(可观测回放,经 SDK 暴露)
```

### 4.2 阶段内依赖

各阶段文档内每项任务标注 `依赖` 字段。关键链:

- **Phase 1**:1.3(SkillTool)→ 1.4(渐进式披露,依附 1.3 的工具入口);1.6(多源技能)→ 1.7(Plugin 端到端,Plugin 提供 skill 来源)
- **Phase 2**:2.1(spawn_agent)→ 2.2(多类型 Task,local_agent 类型用 spawn_agent)→ 2.3(邮箱消费者);2.1 → 2.6(DAG 协调器);2.1 → 2.7(token 预算)
- **Phase 3**:3.1(SDK)→ 3.8(回放);3.5(ToolSearch)独立;3.10(auto 分类器)独立

### 4.3 可并行启动的任务

为缩短交付周期,以下任务无前置依赖,可在各自阶段启动时立即并行:

- Phase 0:0.1、0.2、0.3、0.5、0.7 互不依赖
- Phase 1:1.1(Plan Mode)、1.2(AskUserQuestion)、1.3(SkillTool)互不依赖
- Phase 3:3.2(防休眠)、3.3(限额感知)、3.5(ToolSearch)、3.11(file_read/grep)互不依赖

---

## 五、必须守住的差异化优势

在补齐 Claude Code 能力时,以下四条 TLAH 已有的优势**必须保留甚至强化**。每条给出"守护红线"——任何对齐任务不得越过:

### 5.1 CJK 感知 token 估算
- **现状**:`TokenBudgetService.cs:97-108`,CJK ×1.5、Latin /3.2,已优于 CC 的 `length/4`(`tokenEstimation.ts:203-208`)。
- **守护红线**:不得退化为纯 `length/N`。补全方向是增加按 block 类型估算(image 固定值、tool_use 按 JSON 长度、thinking 按文本)+ JSON 用 2 bytes/token,作为 Phase 0/1 的附带增强。

### 5.2 确定性零成本 SessionMemory
- **现状**:`SessionMemoryService.cs`,从 messages + DB 元数据确定性提取,零 LLM 成本。
- **守护红线**:不得全盘改成 CC 的 LLM fork 提取(`sessionMemory.ts:316-325`)。补全方向是"确定性为主 + 关键点(压缩前、`/summary`)可选叠加轻量 LLM 增强 Key results/Learnings 段",作为可选开关。

### 5.3 原始 HTTP 全量捕获
- **现状**:每次 LLM 调用的 RawRequest/RawResponse 入库,`DebugPanelControl` 可检视。
- **守护红线**:`ILlmProvider` 必须继续用裸 `HttpClient`(不引官方 SDK)。延伸方向是 Phase 3.8 的可观测回放,做成 CC 没有的产品化能力。

### 5.4 GUI 可视化
- **现状**:WinUI 3 桌面体验。
- **守护红线**:CC 的 CLI 能做的,TLAH 优先提供 GUI 等价物(技能管理仪表盘、协调器 DAG 可视化、agent run 逐帧回放、权限模式快捷切换 chip)。CLI 优势(快捷键、批处理)用 WinUI 快捷键 + SDK 补足。

---

## 六、风险与取舍记录

### 6.1 刻意不列入主线的能力

以下 Claude Code 能力**暂不列入主线**,因其与 TLAH 本地优先定位有张力,或需配套后端:

| 能力 | CC 实现 | 不列入理由 | 何时重新评估 |
|---|---|---|---|
| OAuth 登录(claude.ai) | `services/oauth/` | 强绑定云端账号体系;TLAH 定位本地优先,API key + DPAPI 已满足 | 有明确 claude.ai 订阅集成需求时 |
| 远程托管设置 | `services/remoteManagedSettings/` | 需企业后端 + 组织模型,TLAH 当前无组织体系 | 企业版立项时 |
| 组织策略限制 | `services/policyLimits/` | 同上,需后端 | 企业版立项时;可先做本地策略 schema |
| 云端设置同步 | `services/settingsSync/` | CC 绑定 anthropic API;TLAH 用 Phase 3.12 的 P2P git 同步替代 | — |
| 内部日志 | `services/internalLogging.ts` | Anthropic 内部 fleet 专用,无意义 | 不做 |

### 6.2 高风险任务标注

以下任务风险较高,实现前需单独评审:

- **Phase 2.6**(确定性 DAG 协调器):高风险高回报,是超越点,但 DAG 调度器正确性难保证,需充分测试。
- **Phase 3.5**(ToolSearch 延迟加载):高风险,涉及工具注册体系重构,可能影响 prompt cache 命中。
- **Phase 3.10**(auto 分类器):高风险,引入 LLM 安全裁决,误判后果严重,建议放最后。
- **Phase 3.12**(P2P 团队记忆同步):高风险,加密 + git 三方合并 + 冲突处理,建议放最后。

### 6.3 取舍原则

当"对齐 Claude Code"与"守护差异化优势"冲突时,优先守护差异化优势。例:不为了对齐 CC 的 LLM 记忆提取而放弃确定性零成本优势;不为了对齐 CC 的 CLI 体验而牺牲 GUI 可视化。

---

## 七、版本交付定义(Definition of Done)

每个版本的完整交付定义在对应阶段文档末尾。此处给出总则:

1. **代码**:所有任务项的验收标准全部勾选;`dotnet build TLAHStudio.sln -c Release` 无错误无警告(nullable 全通过)。
2. **测试**:`dotnet test TLAHStudio.Core.Tests -c Release` 全绿;新增/变更逻辑有对应测试;Windows-only 行为用 `OperatingSystem.IsWindows()` 守卫。
3. **CI**:`.\tools\ci.ps1 -Configuration Release -Platform x64` 通过。
4. **版本同步**:`appsettings.json`、各 `.csproj`、`TLAHStudio.Installer/version.json`、`setup.iss` 的版本号一致。
5. **文档**:CLAUDE.md 更新新增的架构性子系统;本路线图对应阶段任务标记完成。
6. **发布**:`build-release.ps1` → 验证 → commit/tag/push → `deploy.ps1` 原子上传到 `download.matrixlabs.cn`。

---

## 八、文档索引

| 文档 | 内容 |
|---|---|
| **本文** | 总路线图:战略、阶段、依赖、取舍 |
| [TLAH_4_8_PHASE0_FOUNDATION.md](./TLAH_4_8_PHASE0_FOUNDATION.md) | Phase 0(4.8.0)地基修复与正确性 — 7 项任务 |
| [TLAH_4_9_PHASE1_AUTONOMY.md](./TLAH_4_9_PHASE1_AUTONOMY.md) | Phase 1(4.9.0)智能体自主性 — 7 项任务 |
| [TLAH_5_0_PHASE2_ORCHESTRATION.md](./TLAH_5_0_PHASE2_ORCHESTRATION.md) | Phase 2(5.0.0)多代理编排 — 8 项任务 |
| [TLAH_5_1_PHASE3_PLATFORM.md](./TLAH_5_1_PHASE3_PLATFORM.md) | Phase 3(5.1+)平台与差异化 — 12 项任务 |

### 任务项模板(各阶段文档统一采用)

```
### N.x 任务名 [优先级 P0-P3][工作量 S/M/L][对齐/超越]
**背景**:为什么做这件事
**现状**:`路径:行号` — 当前实现的问题
**Claude Code 对标**:`路径:行号` — CC 如何做
**实现要点**:
- 具体步骤 1
- 具体步骤 2
**验收标准**:
- [ ] 可检验条件 1
- [ ] 可检验条件 2
**依赖**:前置任务编号 / 无
**风险**:风险描述与缓解
```

---

## 九、变更记录

| 日期 | 版本 | 变更 |
|---|---|---|
| 2026-07-04 | 初版 | 基于 Claude Code 五子系统对比研究,建立四阶段路线图 |
