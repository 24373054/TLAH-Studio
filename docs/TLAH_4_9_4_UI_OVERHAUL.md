# TLAH Studio 4.9.4 — UI/UX 全面对标 Claude Code

> **文档定位**:5.0.0(多代理编排)前的最后一个 UI 版本。基于对 Claude Code 源码(`_analysis/claude-code-src/src`)与 TLAH Studio(`TLAHStudio.App`)的逐文件对标研究,覆盖五个维度:消息渲染、输入交互、导航侧边栏、活动面板/调试、美学样式。目标是发挥 WinUI GUI 优势**超越** Claude Code 的终端体验,而非照搬 CLI 的视觉妥协。
>
> **研究方法**:三个 Explore agent 并行读真实源码,所有 `路径:行号` 经验证。第三维度(输入交互/导航)的 agent 输出质量不稳定,关键结论已人工复核补全。
>
> **路线图**:见 [TLAH_5_0_ROADMAP.md](./TLAH_5_0_ROADMAP.md)

---

## 核心洞察(三句话)

1. **TLAH 的数据层信息架构已接近 Claude Code**(`ChatRenderer`/`ChatMessageBlock`/`FormatAgentProgress`/`DepthForEvent`),但被 `ChatPage.xaml.cs` 的命令式 `BuildMessage` 路径完全绕过——切到 `ItemsRepeater` + `DataTemplateSelector` 是最高杠杆的一步。
2. **TLAH 在持久化与回放上已超越 Claude Code**(DB 存储 + Expander 列表 + 12 Tab Debug 面板 + 原始 HTTP 捕获),这是 GUI 的天然优势,应继续放大。
3. **TLAH 的核心问题是"双轨制样式"`:App.xaml` 有完整 token 系统,但控件代码大量硬编码 hex(`AgentActivityPanelControl.xaml.cs:579-749`),导致主题切换失效、维护成本高。且不应继承 CLI 的 ASCII 树/无阴影等视觉妥协。

---

## 一、消息渲染维度

### 1.1 现状对照

| 项 | Claude Code | TLAH 现状 |
|---|---|---|
| 块分发 | `MessageResponse` 按 block.type 分发到独立组件(`MessageResponse.tsx:22`) | `ChatRenderer.RenderAssistantBlocks` 有 block 枚举但 `ChatPage.xaml.cs:517-616` 命令式构建绕过它 |
| Markdown | `Markdown.tsx` + `marked.lexer`,标题/列表/表格/链接/行内代码/引用完整 | **无**——整段 answer 直接塞 `TextBlock`(`ChatPage.xaml.cs:601-616`) |
| 代码块 | `cli-highlight` 按 `token.lang` 高亮 + 语言标签 | **无**——与正文同 `TextBlock` |
| 流式 | `StreamingMarkdown` 稳定前缀 memo + 不稳定后缀重 lex(`Markdown.tsx:186-235`) | 字符队列 + 32ms batch + 530ms 光标闪烁,流式期间不解析 markdown(`ChatPageViewModel.cs:758-834`) |
| 工具卡片 | `ToolUseLoader` 三态色点(pending灰/running闪烁/done绿/error红)+ 参数 key-value | `BuildAgentToolPreview` 仅 title + preview 文本 + 3 行截断,无状态色点、无展开 |
| 思考块 | `AssistantThinkingMessage` dimColor + italic + ctrl+o 展开,内容走 markdown | `BuildLiveStreamBody` Expander + 普通 TextBlock,内容不渲染 markdown |
| 消息分隔 | `⎿ ` 前缀缩进,工具/结果子项对齐成视觉树 | 每消息独立 Border + Spacing=16,子块混在一个卡片 |

### 1.2 差距清单

| ID | 差距 | 严重度 | 涉及文件 |
|---|---|---|---|
| G1 | 无 Markdown 渲染(标题/列表/表格/链接/行内代码全丢) | P0 | `ChatPage.xaml.cs:601-616`、新建 `MarkdownBlock` 控件、`ChatRenderer.cs` |
| G2 | 代码块无语法高亮/语言标签/复制/折叠 | P0 | 新建 `CodeBlockControl.xaml`、`ChatMessageBlock.cs`(加 CodeBlock 枚举) |
| G3 | 工具卡片缺状态色点 + 参数非结构化 | P1 | `ChatPage.xaml.cs:1153-1202`、`ChatPageViewModel.cs:1038-1107` |
| G4 | 工具结果不可展开 | P1 | `ChatPage.xaml.cs:1153`、`ChatMessageBlock.cs:52`(启用 Children) |
| G5 | 思考块内容不渲染 markdown | P1 | `ChatPage.xaml.cs:618-709`、`AssistantContentFormatter.cs` |
| G6 | 流式期间不解析 markdown | P2 | `ChatPage.xaml.cs:331-379`、`ChatPageViewModel.cs:758-834` |
| G7 | 子块无层级竖线缩进(DepthForEvent 算了但 UI 没用) | P2 | `ChatPage.xaml.cs:1109-1151` |
| G8 | `ChatMessageBlocks` 死路径,与命令式构建并存 | P2 | `ChatPage.xaml`、`ChatPage.xaml.cs`、`ChatPageViewModel.cs:37` |
| G9 | AgentActivityPanel 无数据绑定 | P2 | `AgentActivityPanelControl.xaml` + `.cs` |

---

## 二、输入交互维度

### 2.1 现状对照(经人工复核)

| 项 | Claude Code | TLAH 现状 |
|---|---|---|
| 历史命令 | `history.ts` 上下键回溯 | **无**——`MessageInputControl` 无历史栈 |
| 斜杠命令 | `/` 触发补全下拉,`exampleCommands` 缓存 | **无**——输入框无斜杠识别 |
| @提及 | `ContextSuggestions` 文件/agent/skill 提及 | **无** |
| placeholder | 动态提示(随上下文变) | 静态 "Type a message..." |
| 快捷键 | `defaultBindings.ts` 全局/Chat/Autocomplete/Settings 分区 | 输入框无 `KeyboardAccelerator`,仅 Shift+Enter 换行 |
| 权限模式 | 文字提示 | **已有** flyout(MessageInputControl.xaml:115-149) |
| Agent 模式 | REPL toggle | **已有** AgentModeButton(MessageInputControl.xaml:29-34) |

### 2.2 差距清单

| ID | 差距 | 严重度 | 涉及文件 |
|---|---|---|---|
| G10 | 无历史命令上下键 | P1 | `MessageInputControl.xaml.cs` |
| G11 | 无斜杠命令补全 | P1 | `MessageInputControl.xaml.cs`、新建命令注册表 |
| G12 | 无 @提及 | P2 | `MessageInputControl.xaml.cs` |
| G13 | placeholder 静态,无上下文提示 | P2 | `MessageInputControl.xaml` |
| G14 | 快捷键体系缺失(发送/中断/清屏/历史) | P1 | `MessageInputControl.xaml`、`ChatPage.xaml` |

---

## 三、导航与侧边栏维度

### 3.1 现状对照

TLAH 侧边栏已有基础:Pin/Archive(`SidebarPage.xaml:62,72`)、SearchBox(`:205`)、Pinned 分组(`:242`)、ArchivedToggle(`:216`)。这部分 TLAH 不弱,主要差距在信息密度与视觉。

| ID | 差距 | 严重度 | 涉及文件 |
|---|---|---|---|
| G15 | 聊天列表无时间分组(今天/昨天/更早) | P2 | `SidebarPage.xaml.cs` |
| G16 | 项目空间切换不够醒目 | P2 | `SidebarPage.xaml` |
| G17 | 搜索仅标题,无全文/元数据 | P3 | `SidebarPage.xaml.cs` |
| G18 | ChatHeader 信息密度低(18sp 标题 + Context 文本) | P2 | `ChatHeaderControl.xaml` |

---

## 四、活动面板与调试维度

### 4.1 现状对照

| 项 | Claude Code | TLAH 现状 |
|---|---|---|
| Run 步骤树 | 终端滚动,不持久 | **超越**——Expander + 持久化(`AgentActivityPanelControl.xaml.cs:184-209`) |
| Token 用量 | 6 类归因 + 网格图 + 压缩状态(`ContextVisualization.tsx`) | 4 类分段 bar(`ChatHeaderControl.xaml.cs:77-127`),缺 Auto-compact buffer/压缩状态行/MCP 分组 |
| 工具耗时 | 不显示 | **超越**——行间 `+{elapsed}` |
| 工具结果 | 限 10 行 + "ctrl+o see all" | MaxLines=5 + [truncated],无展开按钮 |
| 错误视觉 | 红色 Text | ERR 徽章,无色条/图标/重试按钮 |
| 历史回放 | 无 | **超越**——Expander 列表(可加时间轴滑块) |
| Debug 原始 HTTP | 不捕获 | **远超**——12 Tab + JSON 树(核心差异化护城河) |

### 4.2 差距清单

| ID | 差距 | 严重度 | 涉及文件 |
|---|---|---|---|
| G19 | ContextGauge 缺 Auto-compact buffer 段 + 压缩状态行 | P1 | `ChatHeaderControl.xaml.cs:77-127` |
| G20 | 工具结果无展开/复制/语言高亮 | P1 | `ChatPage.xaml.cs:1153`、`AgentActivityPanelControl.xaml.cs:507` |
| G21 | 错误卡片无色条/图标/重试 | P1 | `ChatPage.xaml.cs:471-515` |
| G22 | ASCII 树 `├─` 是 CLI 妥协,应改 WinUI TreeView/缩进连接线 | P2 | `AgentActivityPanelControl.xaml.cs:298-326` |
| G23 | 审批门仅 WAIT 徽章,缺审批卡片(参数预览/风险/影响路径/按钮) | P1 | 新建 `ToolApprovalCard.xaml` |
| G24 | 无 Run 时间轴滑块(差异化机会) | P3 | `AgentActivityPanelControl.xaml` |

---

## 五、美学与样式设计维度

### 5.1 现状对照

**TLAH 有完整 token 系统(`App.xaml:11-150` 双主题 ThemeDictionaries)但存在双轨制**:
- 集中式:`AppBackgroundBrush/AppChromeBrush/PanelBrush/SurfaceBrush/AccentBrush/DangerBrush`
- 硬编码:`AgentActivityPanelControl.xaml.cs:579-749`、`DebugPanelControl.xaml.cs:456-474` 直接 `Color.FromArgb(...)`,绕过 ThemeResource

**语义色缺失**:Success/Warning/Info 色未在 App.xaml 定义,只在控件内硬编码。
**字号散落**:18/17/16/13/12/11/11.5/10/9sp 混用,无统一 token。
**间距散落**:Padding `16,15,14,12` / `14,14,14,16` / `26,16,22,14` 三套 header 不一致。
**阴影缺失**:WinUI 3 支持 `ThemeShadow` 但未用,层级感不足。
**中文字体未声明**:回退系统 CJK,字号偏小行高偏挤。

### 5.2 差距清单

| ID | 差距 | 严重度 | 涉及文件 |
|---|---|---|---|
| G25 | 语义色缺失(Success/Warning/Info) + 控件硬编码 hex | P0 | `App.xaml`、`AgentActivityPanelControl.xaml.cs:579-749`、`DebugPanelControl.xaml.cs:456-474` |
| G26 | 字号无 token,11.5/9 等非整数 | P0 | `App.xaml`、各控件 |
| G27 | 间距无 scale,header padding 三套 | P0 | `App.xaml`、`AgentActivityPanelControl.xaml:21,64`、`ChatHeaderControl.xaml:6`、`DebugPanelControl.xaml:24` |
| G28 | 无阴影,层级感不足 | P2 | Expander/Border 控件 |
| G29 | 圆角不统一(8/7/6/5/2 散落) | P2 | 全局 |
| G30 | 无微交互动画(hover/pressed 过渡) | P2 | 按钮/Expander |
| G31 | 中文字体未声明,中文场景字号偏小 | P1 | `App.xaml` |
| G32 | Compact 密度仅影响字号/行高,不影响 padding/徽章 | P2 | `IDensityService` |

---

## 六、优化路径(4.9.4 实施计划)

### Phase A — 样式地基(P0,最高优先,1 周)
> 这是后续所有视觉改进的地基。先统一 token,否则改一处坏一处。

- **A1**:`App.xaml` 补齐语义色 token
  - Dark:Success `#4EC99E`/SuccessSurface `#1F3A2E`;Warning `#E8C84C`/WarningSurface `#3D3418`;Info `#71A7FF`/InfoSurface `#1A2F52`
  - Light:Success `#0F9F93`/SuccessSurface `#DBF7E8`;Warning `#B45309`/WarningSurface `#FEF3C7`;Info `#2F5FEA`/InfoSurface `#DBEAFF`
- **A2**:`App.xaml` 定义字号 token:`Display=18/Title=16/Body=13/Caption=12/Label=11/Badge=10`,Compact 缩放因子 0.92
- **A3**:`App.xaml` 定义间距 scale:`XS=4/S=8/M=12/L=16/XL=20/XXL=26`
- **A4**:删除 `AgentActivityPanelControl.xaml.cs:579-749`、`DebugPanelControl.xaml.cs:456-474` 硬编码 hex,全改 `{ThemeResource}`

### Phase B — 数据流重构(P0,前置,1 周)
> 切到 ItemsRepeater + DataTemplateSelector,让后续改进是"加模板"而非"改 if-else"。

- **B1**:`ChatMessageBlock` 加 `CodeBlock`/`MarkdownText`/`Table`/`Quote` 子类型,`Metadata` 改强类型
- **B2**:`ChatRenderer.RenderAssistantBlocks` 用 Markdig 拆 answer 成子块序列
- **B3**:`ChatPage.xaml` 改 `ItemsRepeater` + `DataTemplateSelector`,绑定 `ChatMessageBlocks`,删 `BuildMessage` 命令式路径(G8)

### Phase C — Markdown 与代码块(P0,2 周)
- **C1**:`MarkdownBlock` 控件(RichTextBlock + Markdig,或 `CommunityToolkit.MarkdownTextBlock`)——标题/列表/链接/行内代码/引用(G1)
- **C2**:`CodeBlockControl`——语言标签 + Copy + Fold chevron + 行号 + TextMate 语法高亮(G2)
- **C3**:表格 renderer——Grid 动态列宽,窄屏降级 key-value(对标 `MarkdownTable.tsx:184`)

### Phase D — 工具卡片与思考块(P1,1.5 周)
- **D1**:`BuildAgentToolPreview` 加状态色点(Ellipse + 动画)+ 参数 key-value 行(G3)
- **D2**:工具卡片改 Expander,content 走等宽 RichTextBlock + ANSI 着色;启用 `ChatMessageBlock.Children`(G4、G20)
- **D3**:思考块内容复用 MarkdownBlock(dim 色调)+ redacted 占位(G5)
- **D4**:审批卡片 `ToolApprovalCard.xaml`——风险色条 + 参数表 + 影响路径 + Approve/Deny/Edit(G23)
- **D5**:错误卡片——Danger 色条 + 图标 + 错误栈 ScrollViewer + Copy/Retry(G21)

### Phase E — 输入与交互(P1,1.5 周)
- **E1**:历史命令栈 + 上下键回溯(G10)
- **E2**:斜杠命令补全下拉(G11)——复用现有 Skills/Plugins 命令注册
- **E3**:快捷键体系——发送/中断/清屏/历史/聚焦搜索(G14)
- **E4**:placeholder 动态化(随 workspace/上下文变)(G13)

### Phase F — 活动面板与上下文(P1,1 周)
- **F1**:ContextGauge 加 Auto-compact buffer 段 + 压缩状态行(G19)
- **F2**:ASCII 树改 WinUI 缩进连接线(Border 左竖线)(G22)——4.9.3 已加 Depth,这里接入视觉
- **F3**:中文字体声明 + 中文场景字号/行高调整(G31)

### Phase G — 美学精细化(P2,1 周)
- **G1**:ThemeShadow 给 Expander/Border 加浮起感(G28)
- **G2**:圆角统一语言(容器8/卡片7/徽章6/小标签5/进度条2)(G29)
- **G3**:微交互动画——按钮 hover 150ms 过渡、Expander 展开过渡、ContextGauge 段宽 300ms 动画(G30)
- **G4**:聊天列表时间分组(今天/昨天/更早)(G15)

### Phase H — 差异化放大(P3,5.0+ 再做)
- Run 时间轴滑块(G24)、JSON diff 高亮 + JSONPath 搜索、Run 对比视图、活动时间账本

---

## 七、取舍记录

- **不照搬 CLI 视觉**:ASCII 树、单色徽章、无阴影是终端限制,GUI 用 TreeView/色条/阴影表达层级。
- **Markdown 渲染选型**:优先 `CommunityToolkit.WinUI.Controls.MarkdownTextBlock`(成熟),若不满足定制需求再自研 Markdig + RichTextBlock。
- **语法高亮选型**:优先 TextMate grammar(VS Code 同源),复用 TLAH 已有 LSP 基础设施(`ILspManager`)更佳。
- **流式 markdown**:对标 `StreamingMarkdown` 稳定前缀法,不走"流式纯文本、结束再 markdown"的妥协。
- **Debug 面板是护城河**:raw HTTP 捕获是 Claude Code 做不到的,继续投资 diff/search/schema。

---

## 八、版本交付定义(4.9.4 Definition of Done)

1. **代码**:Phase A-G 验收标准全部勾选;`dotnet build TLAHStudio.sln -c Release` 无错误无警告。
2. **测试**:`dotnet test TLAHStudio.Core.Tests -c Release` 全绿;新增样式 token/Markdown 渲染/工具卡片状态的单测。
3. **CI**:`.\tools\ci.ps1 -Configuration Release -Platform x64` 通过(或手动验证因 PS7 环境)。
4. **版本同步**:`appsettings.json`、各 `.csproj`、`version.json`、`latest.json`、`setup.iss` → `4.9.4`。
5. **文档**:CLAUDE.md 更新 UI 子系统章节;本文件 Phase A-G 标记完成。
6. **发布**:tag `v4.9.4` → `build-release.ps1` → 验证 → SCP 上传。

---

## 九、变更记录

| 日期 | 变更 |
|---|---|
| 2026-07-05 | 初版,基于三维度 Claude Code 源码对标研究,定义 4.9.4 八阶段 UI 优化路径 |
