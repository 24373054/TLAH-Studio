# Phase 1:智能体自主性(4.9.0)

> **阶段目标**:让模型会规划、会提问、会调用技能。本阶段补齐 Claude Code "模型能自主推进长任务"的三大支柱——Plan Mode(声明只读研究态)、AskUserQuestion(结构化澄清)、Skills(按需调用提示词封装),并打通 M2.12.0 预埋但从未接通的 Skills/Plugins 死代码。完成后,TLAH 的模型自主性对齐 Claude Code。
>
> **版本**:4.9.0(前置 4.8.0)
> **前置依赖**:0.1(权限模式 Normalize 修正)、0.6(重注入框架就位,本阶段补 skill/plan 重注入)
> **后续影响**:1.3(SkillTool)是 Phase 2.8(零成本 Dream)与 0.6-skill 重注入的前置;1.7(Plugin 端到端)为 Phase 3 的 marketplace 铺路。
>
> **路线图**:见 [TLAH_5_0_ROADMAP.md](./TLAH_5_0_ROADMAP.md)

---

## 任务清单总览

| # | 任务 | 优先级 | 工作量 | 类型 | 依赖 |
|---|---|---|---|---|---|
| 1.1 | Plan Mode(只读研究 + 计划审批) | P0 | M | 对齐 | 0.1 |
| 1.2 | AskUserQuestion 工具 | P0 | M | 对齐 | 无 |
| 1.3 | SkillTool + 技能清单注入系统提示 | P0 | M | 激活死代码 | 0.6 |
| 1.4 | 渐进式披露(frontmatter 清单 + 懒加载 body) | P0 | M | 对齐 | 1.3 |
| 1.5 | OutputStyles 输出风格 | P1 | S | 对齐 | 无 |
| 1.6 | 技能多源目录 + 条件技能(paths) | P1 | M | 对齐 | 1.3 |
| 1.7 | Plugin 端到端打通 | P1 | L | 激活死代码 | 1.3、1.6 |

> 1.1、1.2、1.3 互不依赖,可并行启动。1.4/1.6/1.7 依附 1.3。

---

## 1.1 Plan Mode(只读研究 + 计划审批)[P0][M][对齐]

**背景**:Claude Code 的 Plan Mode 让模型先只读研究、产出计划、用户审批后再执行写操作。TLAH 无此能力,模型无法声明"我只读不写"的安全态,长任务前期调研与后期执行混在同一权限下,调研阶段的探索性工具调用与执行阶段的写操作风险不分离。

**现状**:
- `TLAHStudio.Core/Services/AgentPermissionModes.cs:5-7` — 仅 3 档(BypassPermissions/AutoApprove/RequestApproval),无 plan
- 全仓搜 `plan_mode`/`EnterPlanMode` 零命中

**Claude Code 对标**:
- `tools/EnterPlanModeTool/EnterPlanModeTool.ts:77-102` — 进入:stash `prePlanMode`,设 `mode='plan'`
- `tools/ExitPlanModeTool/ExitPlanModeV2Tool.ts:221-239, 361` — 退出:`checkPermissions` 返回 ask,`requiresUserInteraction=true`,审批后 `restoreMode = prePlanMode ?? 'default'`
- `utils/permissions/permissionSetup.ts:1462-1493` — `prepareContextForPlanMode`,写类工具在 plan 模式返回 ask
- 计划本身写到计划文件,`ExitPlanModeV2Tool` 读回(`:246, 253`)

**实现要点**:
1. `AgentPermissionModes` 新增 `Plan` 档(枚举 + Normalize 别名 `plan`)。
2. 新增 `enter_plan_mode` / `exit_plan_mode` 工具(注册到 `AgentToolNames`)。
3. `enter_plan_mode`:在 `AgentRunState` stash `PrePlanMode`(当前权限模式),设当前模式为 `Plan`;发出 `PlanModeEntered` 事件通知 UI(Header 显示 plan chip)。
4. `exit_plan_mode`:`RequiresExplicitApproval = true`(强制用户审批);`checkPermissions` 等价返回 ask;审批后恢复 `PrePlanMode`。
5. **Plan 模式下的工具约束**:在 `AgentRunEngine` 权限决策路径(`AgentRunEngine.cs:482-535`),Plan 模式下所有写类工具(`IsWrite`/`RequiresExplicitApproval`)返回需审批(即使用户在 plan 前是 AutoApprove);只读工具正常执行。
6. 计划内容:模型在 plan 模式期间把计划写到 `.tlah_context/plans/{chatId}-{slug}.md`;`exit_plan_mode` 工具参数接受计划摘要,审批时 UI 展示完整计划。
7. UI:ChatHeaderControl 加 Plan 模式 chip + 进入/退出按钮;权限模式快捷切换(配合 Phase 3.5 的模式循环,本阶段先做按钮)。

**验收标准**:
- [ ] 模型可调用 `enter_plan_mode` 进入只读研究态
- [ ] Plan 模式下写类工具调用被拦截(返回需审批),只读工具正常
- [ ] `exit_plan_mode` 强制用户审批,审批后恢复进入前的权限模式
- [ ] 计划写入 `.tlah_context/plans/`,UI 展示计划供审批
- [ ] Plan 模式状态在 run 持久化(resume 后保留)
- [ ] 新增单测:Plan 模式下写工具被拦截、退出后恢复

**依赖**:0.1(权限模式 Normalize 修正,新增 Plan 档更安全)

**风险**:中。需确保 Plan 模式与现有三档权限模式正确叠加(Plan 是正交的"只读约束",不是替代)。缓解:Plan 不作为独立权限档,而是 `AgentRunState` 上的 `IsPlanMode` 布尔,与权限模式正交。

---

## 1.2 AskUserQuestion 工具 [P0][M][对齐]

**背景**:Claude Code 模型可用结构化多选/多问向用户提问,答案经权限 UI 注入。TLAH 模型只能发文本回合等用户回复,无法在工具流中澄清歧义,导致长任务因歧义跑偏。结构化提问能让模型在关键决策点获取明确输入。

**现状**:全仓搜 `AskUser` 零命中;模型只能发文本回合。

**Claude Code 对标**:
- `tools/AskUserQuestionTool/AskUserQuestionTool.tsx:62-67` — schema:`questions`(1-4 个),每个含 `question`/`header`(≤12 字符)/`options`(2-4 个,含 `label`/`description`/可选 `preview`)/`multiSelect`
- `:182-188` — `checkPermissions` 返回 `{behavior:'ask', message:'Answer questions?'}`
- `:209-244` — 答案注入 `updatedInput.answers`,工具返回,`mapToolResultToToolResultBlockParam` 格式化为 `"question"="answer"` 对
- 工具标志:`shouldDefer:true`、`isReadOnly:true`、`isConcurrencySafe:true`、`requiresUserInteraction:true`——答案来自用户,模型无法伪造

**实现要点**:
1. `AgentToolNames` 新增 `ask_user_question` 工具。
2. 工具 schema:`questions` 数组(1-4 个),每个含 `question`(string)、`header`(string,≤12 字符)、`options`(2-4 个,各含 `label`/`description`,可选 `preview`)、`multiSelect`(bool)。
3. `IAgentToolV3.ClassifySafetyAsync` 返回 `RequiresExplicitApproval = true`(走审批门)。
4. 审批 UI(WinUI):渲染多选/单选对话框,每个 question 一组选项;用户作答后,答案注入 `updatedInput.answers`。
5. 工具 `ExecuteWithProgressAsync` 返回答案,格式化为 `"question"="answer"` 对(多选用逗号分隔)。
6. `ToolProtocolGuard` 与 `AgentRunEngine` 的审批流复用:ask_user_question 触发 `ApprovalRequested` 事件,`ChatPageViewModel.CompleteApprovalFlowAsync` 收答案。
7. UI 细节:`preview` 字段(可选)用于代码片段/布局预览,WinUI 用等宽字体框渲染。

**验收标准**:
- [ ] 模型可调用 `ask_user_question` 提 1-4 个结构化问题
- [ ] WinUI 渲染多选/单选对话框,用户作答
- [ ] 答案正确注入工具结果,模型可见
- [ ] 多选问题支持多选,单选问题限制单选
- [ ] `preview` 字段(若提供)以等宽框渲染
- [ ] 新增单测:schema 校验(questions 数量、options 数量、header 长度)、答案格式化

**依赖**:无

**风险**:中。WinUI 对话框渲染需处理动态问题数 + 选项数 + preview 布局。缓解:参考现有 `SettingsContentDialog` 的动态表单模式;preview 用简单的 ScrollViewer + 等宽 TextBlock。

---

## 1.3 SkillTool + 技能清单注入系统提示 [P0][M][激活死代码]

**背景**:TLAH 在 M2.12.0 预埋了 Skills 脚手架(`SkillLoader`、`AgentSkill` record、`ISkillLoader`),DI 已注册但**无任何消费者**——`SystemPromptBuilder` 不注入技能清单,`AgentToolNames` 无 skill 工具,`SkillLoader.FindRelevantSkillsAsync` 无人调用且仅做 `Contains` 关键字匹配。这是当前最大能力缺口:Skills 是 Claude Code 的杀手锏,TLAH 完全没接通。

**现状**:
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:169-286` — `AgentSkill` record、`ISkillLoader`、`SkillLoader`(Contains 匹配,无人调用)
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:200-203` — 单一目录 `%LOCALAPPDATA%\TLAH Studio\skills`
- `TLAHStudio.Core/Helpers/SystemPromptBuilder.cs:18-71` — 仅拼接 chat/profile/global + ProjectSpace + AgentFile,**无技能清单注入**
- `TLAHStudio.Core/Services/AgentTools.cs:9-57` — `AgentToolNames` 30+ 工具,**无 skill 工具**
- `TLAHStudio.App/App.xaml.cs:167-172` — DI 注册但注释标 `// M2.12.0: MCP & Plugins & Skills`(死代码)

**Claude Code 对标**:
- `tools/SkillTool/SkillTool.ts:331-841` — `Skill` 工具,输入 `skill`+`args`,`call()` 经 `processPromptSlashCommand` 展开技能 body 注入
- `utils/attachments.ts:2661-2751` — `getSkillListingAttachments`,每轮收集技能清单,包装 `system-reminder` 注入
- `utils/messages.ts:3728-3738` — 清单渲染为 "The following skills are available for use with the Skill tool"
- `tools/SkillTool/prompt.ts:173-196` — Skill 工具静态 prompt:"When a skill matches...BLOCKING REQUIREMENT: invoke the relevant Skill tool BEFORE generating any other response"
- `utils/attachments.ts:2699-2730` — `sentSkillNames` 增量集合,只发新技能

**实现要点**:
1. `AgentToolNames` 新增 `skill` 工具;实现 `SkillAgentTool : IAgentToolV3`,输入 `{skill: string, args?: string}`。
2. `SystemPromptBuilder` 加技能清单注入层:
   - 调 `ISkillLoader.LoadSkillsAsync` 取技能列表。
   - 格式化为 `- {name}: {description} — {whenToUse}` 列表(等效 CC `formatCommandsWithinBudget`)。
   - 包装为 `[skill-listing]` meta 块(等效 system-reminder),附 Skill 工具使用说明("匹配时优先调用 skill 工具")。
   - 维护 `sentSkillNames` 增量集合,每轮只发新技能(避免重复 cache 成本)。
3. `SkillAgentTool.ExecuteWithProgressAsync`:调 `ISkillLoader.GetSkillContentAsync(skillName)` 取 body,作为 newMessages 注入(模型后续轮次可见技能正文)。
4. 激活现有 `AgentSkill` record 的字段:`Name`/`Description`/`WhenToUse`/`Content`/`AllowedTools`/`Paths`/`Hooks`(`PluginManifestService.cs:172-179`)。
5. **回头补 0.6 的 skill 重注入**:1.3 完成后,在 `AgentRunEngine` 压缩后重注入中补"已用 skill 内容"(每 skill 5K,总 25K budget),完成 0.6 的剩余部分。

**验收标准**:
- [ ] `skill` 工具出现在 `AgentToolNames` 并注册到 `IAgentToolRegistry`
- [ ] 系统提示含技能清单(`[skill-listing]` 块),模型可见
- [ ] 模型调用 `skill` 工具后,技能 body 注入后续上下文
- [ ] `sentSkillNames` 增量生效:第二轮不重复发已发技能
- [ ] 压缩后已用 skill 内容被重注入(完成 0.6-skill)
- [ ] 新增单测:清单注入格式、增量发送、skill body 注入

**依赖**:0.6(重注入框架就位;1.3 完成后回头补 0.6-skill)

**风险**:中。需确保技能清单 token 占用受控(见 1.4 渐进式披露)。`SkillLoader` 现有 `Contains` 匹配需在本任务中替换为"清单注入 + 模型自主选择"范式(不再用关键字预筛选)。

---

## 1.4 渐进式披露(frontmatter 清单 + 懒加载 body)[P0][M][对齐]

**背景**:若把所有技能的完整正文塞进系统提示,技能一多就占满上下文。Claude Code 用渐进式披露:清单只发 frontmatter(name+description+whenToUse),body 在模型调用 Skill 工具时才懒加载,且清单总量受 1% 上下文预算约束。TLAH 现有 `SkillLoader.FindRelevantSkillsAsync` 用 `Contains` 匹配后 `GetSkillContentAsync` 一次性返回全部正文,无预算控制、无懒加载。

**现状**:
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:219-238` — `FindRelevantSkillsAsync` 用 `lower.Contains(trigger)`,`GetSkillContentAsync` 一次性返回全部正文
- 无预算控制、无增量、无截断

**Claude Code 对标**:
- `skills/loadSkillsDir.ts:100-105` — `estimateSkillFrontmatterTokens` 只算 name+description+whenToUse,body 不计入
- `tools/SkillTool/prompt.ts:21-29` — `SKILL_BUDGET_CONTEXT_PERCENT = 0.01`(1% 上下文预算),`MAX_LISTING_DESC_CHARS = 250`
- `tools/SkillTool/prompt.ts:70-171` — `formatCommandsWithinBudget`,bundled 技能永不截断,非 bundled 按预算截断或降级为仅名称
- `skills/loadSkillsDir.ts:344-399` — `getPromptForCommand` 闭包仅在调用时执行(body 懒加载)
- `utils/attachments.ts:2717-2730` — 增量发送

**实现要点**:
1. 清单注入只发 frontmatter:`Name` + `Description`(≤250 字符,超长截断)+ `WhenToUse`,不发 `Content`。
2. 引入 `SkillBudgetContextPercent = 0.01`(1% 上下文预算,基于 `ITokenBudgetService` 的窗口估算)。
3. `formatCommandsWithinBudget` 等效逻辑:bundled 技能(内置)永不截断;非 bundled 按预算累计,超预算则截断 description 或降级为仅名称。
4. body 懒加载:`SkillAgentTool` 调用时才 `GetSkillContentAsync` 取正文注入(1.3 已实现调用入口,本任务确保正文不进清单)。
5. `sentSkillNames` 增量(1.3 已引入,本任务确认清单格式与增量协同)。
6. 移除 `FindRelevantSkillsAsync` 的 `Contains` 预筛选——改为全量清单 + 预算约束 + 模型自主选择。

**验收标准**:
- [ ] 系统提示的技能清单只含 frontmatter,不含 body
- [ ] 单条 description ≤250 字符,超长截断
- [ ] 清单总 token ≤ 1% 上下文预算
- [ ] bundled 技能永不截断,非 bundled 超预算降级为仅名称
- [ ] body 仅在 `skill` 工具调用时注入
- [ ] 新增单测:预算截断、降级、bundled 不截断

**依赖**:1.3(SkillTool 入口)

**风险**:中。1% 预算在 200K 窗口下约 2K token,约 8-15 个技能;技能更多时降级策略需测试。缓解:降级为仅名称后,模型仍可通过 `skill` 工具按名调用(只是清单里没描述)。

---

## 1.5 OutputStyles 输出风格 [P1][S][对齐]

**背景**:Claude Code 的 OutputStyles 让同一模型在不同风格下表现不同(default/Explanatory/Learning),通过向系统提示追加风格 prompt 实现。TLAH 全代码库无 OutputStyle 概念,`SystemPromptBuilder` 只有静态拼接。低成本高价值,TLAH 完全没有。

**现状**:全仓零 `OutputStyle` 匹配;`SystemPromptBuilder.cs:18-71` 静态拼接。

**Claude Code 对标**:
- `constants/outputStyles.ts:39-135` — 3 内置:`default`(null,不追加)、`Explanatory`(追加教育性 "Insight" 块)、`Learning`(追加 "Learn by Doing" 请求人类贡献 2-10 行代码)
- `constants/outputStyles.ts:137-175` — 多源加载优先级:built-in < plugin < user < project < managed
- `outputStyles/loadOutputStylesDir.ts:26-92` — 从 `.claude/output-styles/*.md` 加载(文件名即样式名),frontmatter 提供 `name`/`description`/`keep-coding-instructions`
- `constants/outputStyles.ts:206-210` — settings `outputStyle` 字段切换

**实现要点**:
1. 新增 `OutputStyleConfig` record(`Name`/`Description`/`PromptAppend`/`KeepCodingInstructions`)与 `IOutputStyleService`。
2. 3 内置(直接移植 CC prompt 文本):
   - `default`:不追加。
   - `Explanatory`:追加教育性 Insight 块指令。
   - `Learning`:追加 "Learn by Doing" 指令(请求人类贡献代码)。
3. 从 `%LOCALAPPDATA%\TLAH Studio\output-styles\*.md` 加载自定义样式(文件名即样式名,frontmatter 解析)。
4. `SystemPromptBuilder` 追加当前样式 `PromptAppend` 到系统提示尾部。
5. `SettingsContentDialog` 加 OutputStyle 切换 ComboBox;持久化到 `GlobalSettings`。
6. 多源优先级:built-in < user(`%LOCALAPPDATA%`)< project(`<workspace>/.tlah/output-styles`),后者覆盖前者同名。

**验收标准**:
- [ ] 3 内置样式可切换,系统提示按样式追加对应 prompt
- [ ] 自定义样式从 `output-styles/*.md` 加载,frontmatter 解析正确
- [ ] SettingsDialog 切换持久化,重启后保留
- [ ] 多源优先级正确(project > user > built-in)
- [ ] 新增单测:样式加载、优先级合并、prompt 追加

**依赖**:无

**风险**:低。纯系统提示追加,无运行时行为改变。

---

## 1.6 技能多源目录 + 条件技能(paths)[P1][M][对齐]

**背景**:Claude Code 从 managed/user/project 三层加载技能,且支持 `paths` frontmatter 让技能在触碰匹配文件时才激活(精准上下文控制)。TLAH 只有单一 `%LOCALAPPDATA%\TLAH Studio\skills` 目录,无项目级、无动态发现;`AgentSkill.Paths` 字段定义了但 `SkillLoader` 完全不处理。

**现状**:
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:200-203` — 单一目录
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:178` — `Paths` 字段定义但不处理

**Claude Code 对标**:
- `skills/loadSkillsDir.ts:638-714` — `getSkillDirCommands`,managed(`<managedPath>/.claude/skills`)/user(`~/.claude/skills`)/project(cwd 向上到 home 的所有 `.claude/skills`)三层并行加载
- `skills/loadSkillsDir.ts:725-769` — `realpath` 解析符号链接去重,首次获胜
- `skills/loadSkillsDir.ts:997-1058` — `activateConditionalSkillsForPaths`,带 `paths` 的技能存 `conditionalSkills`,触碰匹配文件时激活
- `skills/loadSkillsDir.ts:861-915` — `discoverSkillDirsForPaths`,Read/Write/Edit 文件时从父目录向上走到 cwd 发现嵌套 `.claude/skills`

**实现要点**:
1. `SkillLoader` 支持三层目录:
   - project:`<workspace>/.tlah/skills`
   - user:`%LOCALAPPDATA%\TLAH Studio\skills`(现有)
   - managed:可配置的策略级目录(本阶段可留空,预留接口)
2. 去重:用 `Path.GetFullPath`(Windows 等效 realpath)解析,首次获胜(project > user > managed 优先级)。
3. `paths` frontmatter 解析:`SkillLoader` 把带 `Paths` 的技能存入 `conditionalSkills`,不直接进清单。
4. 条件激活:在 `file_read`/`file_write`/`edit` 工具执行时(或 `ReadFileTracker.MarkRead`),调 `ActivateConditionalSkillsForPaths(path)`,匹配的技能加入动态清单,emit `skillsLoaded` 清缓存。
5. gitignore 风格匹配:可用 `dotnet-ignore` NuGet 包或自实现简化版 glob。
6. 配合 1.3 的清单注入:条件激活的技能加入 `sentSkillNames` 增量发送。

**验收标准**:
- [ ] project/user/managed 三层技能都被加载
- [ ] 同名技能按优先级去重(project 胜)
- [ ] 带 `paths` 的技能默认不进清单,触碰匹配文件后激活
- [ ] 条件激活后技能进入清单(增量发送)
- [ ] 新增单测:多源加载、去重、条件激活

**依赖**:1.3(SkillTool 与清单注入就位)

**风险**:中。gitignore 风格匹配需正确实现,误匹配会激活无关技能。缓解:先用简化 glob(`*`/`**`),复杂模式后置。

---

## 1.7 Plugin 端到端打通 [P1][L][激活死代码]

**背景**:TLAH 的 `PluginManifestService` 能解析 `plugin.json`,但**无 UI、无启动加载、工具不注册到 `IAgentToolRegistry`、MCP server 不注册到 `IToolPlatformService`、技能不加载**。Plugin 系统是死代码。Claude Code 的 `createPluginFromPath` 自动探测多组件目录并接入各子系统。

**现状**:
- `TLAHStudio.App/App.xaml.cs:167-172` — DI 注册但无消费者
- `TLAHStudio.Core/Services/Plugins/PluginManifestService.cs:113-166` — 能解析 `plugin.json`,无后续接入

**Claude Code 对标**:
- `utils/plugins/pluginLoader.ts:1348-1591` — `createPluginFromPath` 读 `.claude-plugin/plugin.json`,自动探测 `commands/`/`agents/`/`skills/`/`output-styles/` 目录
- `utils/plugins/pluginLoader.ts:3096` — `loadAllPlugins` 从所有 marketplace 加载
- `utils/plugins/schemas.ts:884` — `PluginManifestSchema`:`name`/`version`/`skills`/`mcpServers`/`hooks`/`commands`/`agents`/`outputStyles`/`lspServers`/`settings`

**实现要点**:
1. 启动时调 `IPluginManifestService.DiscoverPluginsAsync`(已有),遍历信任插件。
2. 信任插件的组件接入:
   - `Tools` → 注册到 `IAgentToolRegistry`(动态工具)。
   - `McpServers` → 注册到 `IToolPlatformService`(启动 MCP server)。
   - `Skills` → 经 `ISkillLoader` 加载(配合 1.6 多源)。
   - `OutputStyles` → 经 `IOutputStyleService` 加载(配合 1.5)。
   - `Hooks` → 经 `IToolHookRegistry` 注册。
3. `ToolPlatformDialog` 加插件管理页:列出已发现插件、信任/取消信任、启用/禁用、展示 manifest 内容。
4. 信任模型:复用现有 `.trusted.json`(`PluginManifestService.cs:80-97`);`PluginTrustLevel.Partial` 实际化(按工具/路径细粒度授权)。
5. 插件热加载(可选):监听插件目录变更,本阶段可只做启动加载。

**验收标准**:
- [ ] 启动时发现并加载信任插件
- [ ] 插件的 Tools 注册到 `IAgentToolRegistry`,模型可调用
- [ ] 插件的 McpServers 启动,工具可经 `mcp_call` 调用
- [ ] 插件的 Skills 经 `ISkillLoader` 加载,进入技能清单
- [ ] ToolPlatformDialog 展示插件管理页,可信任/启用/禁用
- [ ] 新增单测:插件发现、组件接入、信任判定

**依赖**:1.3(SkillTool)、1.6(多源 SkillLoader,Plugin 的 skills 才有加载路径)

**风险**:中。动态工具注册到 `IAgentToolRegistry` 需确保 prompt cache 稳定(工具顺序固定)。缓解:插件工具追加在内置工具之后,按名排序。安全:不可信插件的工具默认不注册,需显式信任。

---

## 版本交付定义(4.9.0 Definition of Done)

1. **代码**:1.1-1.7 全部验收标准勾选;`dotnet build TLAHStudio.sln -c Release` 无错误无警告。
2. **回头补全 0.6-skill**:1.3 完成后,压缩后重注入已用 skill 内容(每 skill 5K,总 25K),完成 0.6 的剩余部分。
3. **测试**:
   - 新增覆盖:Plan 模式工具约束、AskUserQuestion schema/答案注入、技能清单注入/增量/懒加载、OutputStyles 加载/优先级、多源技能去重、条件技能激活、Plugin 组件接入。
   - `dotnet test TLAHStudio.Core.Tests -c Release` 全绿。
4. **CI**:`.\tools\ci.ps1 -Configuration Release -Platform x64` 通过。
5. **版本同步**:`appsettings.json`、各 `.csproj`、`TLAHStudio.Installer/version.json`、`setup.iss` → `4.9.0`。
6. **文档**:CLAUDE.md 新增 Skills/OutputStyles/Plan Mode 章节;本文件 1.1-1.7 标记完成。
7. **发布**:tag `v4.9.0` → `build-release.ps1` → `verify-release.ps1` → SCP 上传。

---

## 测试策略要点

- **Skill 清单测试**:构造多个技能(含 bundled/非 bundled、超长 description、带 paths),验证清单格式、预算截断、增量发送、条件激活。
- **Plan Mode 测试**:mock `AgentRunState.IsPlanMode`,验证写工具被拦截、只读工具放行、退出后恢复。
- **AskUserQuestion 测试**:schema 校验单测 + WinUI 对话框手动验证(动态问题/选项/preview)。
- **Plugin 接入测试**:构造测试插件(plugin.json + skills/ + mcpServers/),验证组件注册到各子系统。

---

## 变更记录

| 日期 | 变更 |
|---|---|
| 2026-07-04 | 初版,定义 4.9.0 七项智能体自主性任务 |
