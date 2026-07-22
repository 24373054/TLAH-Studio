<p align="center">
  <img src="./TLAHStudio.App/Assets/logo.png" width="96" height="96" alt="TLAH Studio 标志">
</p>

<h1 align="center">TLAH Studio</h1>

<p align="center">
  <strong>可观测、可控制的 Windows 原生 AI 智能体工作台。</strong><br>
  在一个桌面应用中完成聊天、工具执行、MCP、工作区审阅、模型调试与持久化运行追踪。
</p>

<p align="center">
  <a href="./README.md">English</a> ·
  <a href="https://github.com/24373054/TLAH-Studio/releases/latest">最新版本</a> ·
  <a href="https://download.matrixlabs.cn">下载安装</a> ·
  <a href="./docs/README.md">项目文档</a>
</p>

<p align="center">
  <a href="https://github.com/24373054/TLAH-Studio/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/24373054/TLAH-Studio/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  <a href="https://github.com/24373054/TLAH-Studio/releases/latest"><img alt="最新版本" src="https://img.shields.io/github/v/release/24373054/TLAH-Studio?display_name=tag&sort=semver&style=flat-square"></a>
  <a href="https://github.com/24373054/TLAH-Studio/releases"><img alt="下载量" src="https://img.shields.io/github/downloads/24373054/TLAH-Studio/total?style=flat-square&logo=github"></a>
  <a href="https://github.com/24373054/TLAH-Studio/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/24373054/TLAH-Studio?style=flat-square&logo=github"></a>
  <a href="https://github.com/24373054/TLAH-Studio/forks"><img alt="Forks" src="https://img.shields.io/github/forks/24373054/TLAH-Studio?style=flat-square&logo=github"></a>
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="WinUI 3" src="https://img.shields.io/badge/WinUI-3-0078D4?style=flat-square&logo=windows11&logoColor=white">
  <a href="./LICENSE"><img alt="专有许可证" src="https://img.shields.io/badge/license-Proprietary-E34F26?style=flat-square"></a>
</p>

<p align="center">
  <a href="https://github.com/24373054/TLAH-Studio/releases/latest"><strong>下载最新 Windows x64 版本 →</strong></a>
</p>

![包含聊天、工作区控制与 Agent Activity 的 TLAH Studio 桌面工作台](./docs/assets/readme/tlah-studio-overview.png)

> 官方版本支持 Windows 10 build 19041+ 与 Windows 11 x64。安装包已包含运行时，按用户安装，无需管理员权限。

## 为什么选择 TLAH Studio

TLAH Studio 面向那些不能只靠聊天框完成的工作。它让智能体执行过程保持可见，为每个会话提供明确的工作区与权限边界，并持久化理解长任务执行结果所需的构件和记录。

| 原生体验 | 全程可观测 | 权限可控制 | 能力可扩展 |
|---|---|---|---|
| WinUI 3 桌面外壳，提供 Windows 原生输入、主题与窗口行为 | 智能体步骤、工具调用、检查点、构件、模型原始载荷与调试追踪 | 统一权限矩阵保证 Ask 批准后可执行，并让完全访问的语义保持一致 | OpenAI-compatible、Anthropic 协议，MCP、Skills 与受信任本地插件清单 |

应用采用本地优先设计，但并非完全离线：聊天与运行记录持久化在本机；只有在用户配置或调用模型提供商、MCP 服务器、网页/HTTP 工具、远程执行或更新服务时，提示词或工具数据才会离开设备。

## 产品亮点

| 领域 | 已实现能力 |
|---|---|
| **智能体运行时** | 多步执行、有界 Provider 重试、失败感知的重新规划、未知结果防重放围栏、自适应步骤预算、暂停/恢复、检查点、构件、任务与 Activity 回放 |
| **工具智能** | 确定性上下文选择，初始可调用工具不超过 15 个；实时目录搜索、官方 Provider 严格 Schema、结构化结果与面向恢复的错误 |
| **工作区工具** | 文件与代码操作、Git、PowerShell 执行、私有会话沙箱与 Changes 变更审阅 |
| **创建与研究** | 可见的研究、表格、文档、图表与本地工具质量页面，提供普通文件/文件夹操作、可靠的公开来源回退，并且不依赖隐藏命令 |
| **推理与权限** | 独立的 `Auto / Off / Low / Medium / High / Max` 推理控制与四种工具权限模式 |
| **模型与 MCP** | Anthropic 和 OpenAI-compatible HTTP 协议；支持 STDIO 与 Streamable HTTP 的 MCP 工具/资源 |
| **上下文与记忆** | 自适应长链预算、响应式压缩、项目/会话记忆、大型工具输出持久化与斜杠命令 |
| **可调试性** | 经过敏感信息脱敏的模型请求/响应、运行事件、诊断导出与本地审计数据 |
| **桌面体验** | 明暗主题、沉浸式动态水族箱、响应式右侧工作台、长会话虚拟化、设置搜索、声音与减少动态效果支持 |
| **自动更新** | ECDSA 签名更新元数据、SHA-256 安装包校验、灰度发布、最低版本与原子部署 |

### 沉浸式动态水族箱

展开侧边栏顶部是一座带装饰框和纵深分层的水族箱，由本地美术资源、可摆尾鱼群、水体光影、植被、微粒与气泡共同构成。它使用 GPU 合成动效，不运行逐帧 UI 定时器；可在 **Settings → Appearance** 中直接选择 `Auto`、`Eco`、`Balanced` 或 `High` 画质，并记住暂停/继续状态。开启减少动态效果、高对比度或节能模式，以及折叠侧边栏或窗口失去活动状态时，场景会自动切换为经过设计的静态画面。

### 执行控制

| 控制项 | 选项 | 用途 |
|---|---|---|
| 工具权限 | 每次询问 · 仅规划 · 自动批准 · 完全访问 | 决定工具何时可以读取、写入、执行或访问宿主机 |
| 推理强度 | Auto · Off · Low · Medium · High · Max | 独立于权限选择模型的推理深度 |
| 工作区 | 指定文件夹 · 私有沙箱 | 限定每个会话的文件、Git 与命令操作范围 |

| 权限模式 | 授权行为 |
|---|---|
| 每次询问 | 安全操作直接运行；风险或上下文受限调用需要确认。批准会授权该条已持久化的精确调用，并在执行和恢复后继续有效。 |
| 仅规划 | 以只读研究为主，写入或破坏性操作前需要确认。 |
| 自动批准 | 普通操作自动运行，但上下文限制以及敏感的仓库、环境或 Shell 路径仍会询问。 |
| 完全访问 | 绕过普通审批、已存策略、宿主机路径、网络允许列表和敏感文件限制；只保留不可绕过的灾难级操作硬阻断与必须等待用户输入的交互暂停。 |

审批参数默认只读。只有显式开启高级编辑后，且内容是目标工具能够接受的有效 JSON 对象，才会替换已持久化的调用参数。受限执行依赖策略与后端，并不等同于虚拟机安全边界；完全访问只能用于可信提示词和工作区。

### 创建与研究工作台

可从展开侧边栏、紧凑侧边栏、消息输入区或命令面板直接打开 **Create & Research**。这些都是产品中的可见入口，不是需要记忆的对话暗号。

| 页面 | 可直接完成的工作 |
|---|---|
| 研究 | 运行 Quick、Balanced 或 Deep 公开资料研究；筛选域名、时效和语言，并通过查询感知的回退提高覆盖率；保存带提供方与许可归因的证据报告 |
| 表格 | 粘贴 CSV/TSV 风格数据，创建带冻结表头、筛选、自动列宽和可选图表预览的 XLSX 工作簿 |
| 文档 | 从普通文本和结构化章节创建 Markdown、DOCX 或 PDF 文件 |
| 图表 | 创建流程图、架构图、柱状图或折线图，并输出 SVG 与高 DPI PNG |
| 工具质量 | 查看本地调用结果、延迟、Shell 回退、目录搜索和逐工具成功率，不读取提示词或文件内容 |

输出默认写入当前工作区。未选择工作区时，TLAH Studio 使用该会话独立的 `%LOCALAPPDATA%\TLAH Studio\sandboxes\<chat>` 目录。工作台会显示完整路径，并提供结果预览、**Open result** 与 **Open folder**，用户无需在应用外预先准备环境。

## 项目概况

| 指标 | 当前仓库状态 |
|---|---:|
| 稳定版本 | `4.15.0` |
| 已注册智能体工具 | `51` |
| 内置 Skills | `12` |
| MCP 传输方式 | STDIO + Streamable HTTP |
| 官方产物 | Windows x64 自包含安装包 |

实时仓库数据：

[![Stars](https://img.shields.io/github/stars/24373054/TLAH-Studio?style=for-the-badge&logo=github&label=Stars)](https://github.com/24373054/TLAH-Studio/stargazers)
[![Forks](https://img.shields.io/github/forks/24373054/TLAH-Studio?style=for-the-badge&logo=github&label=Forks)](https://github.com/24373054/TLAH-Studio/forks)
[![Downloads](https://img.shields.io/github/downloads/24373054/TLAH-Studio/total?style=for-the-badge&logo=github&label=Downloads)](https://github.com/24373054/TLAH-Studio/releases)

## 架构

```mermaid
flowchart LR
    UI[WinUI 3 Views<br/>ViewModels] --> ORCH[LlmService]
    UI --> WORKBENCH[Create & Research<br/>直接工作台]
    UI --> SETTINGS[Chat · Settings · Workspace services]
    SETTINGS --> DB[(EF Core + SQLite)]
    ORCH --> SELECTOR[工具上下文选择器<br/>初始不超过 15 个]
    SELECTOR --> PROVIDERS[HTTP provider adapters<br/>OpenAI-compatible · Anthropic]
    ORCH --> ENGINE[AgentRunEngineV2]
    ENGINE --> CONTEXT[Token budget<br/>Compaction · Memory]
    ENGINE --> TOOLS[Tool registry<br/>Scheduler · Lifecycle hooks]
    WORKBENCH --> SPECIALISTS[研究 · 表格<br/>文档 · 图表]
    TOOLS --> SPECIALISTS
    SPECIALISTS --> FILES[(经过验证的工作区构件)]
    TOOLS --> GUARD[Safety classification<br/>Permission gate · Protocol guard]
    GUARD --> EXEC[Workspace · PowerShell · Git<br/>WSL · Docker · Remote]
    TOOLS --> MCP[MCP<br/>STDIO · Streamable HTTP]
    ENGINE --> EVENTS[Events · Checkpoints<br/>Artifacts · Tasks]
    EVENTS --> DB
    DB --> SURFACES[Activity · Changes<br/>Debug · Export]
```

主要依赖方向为 `App → Core + Data`、`Data → Core`、`Tests → Core + Data`。Core 负责编排与服务契约，Data 负责 EF Core 配置和 SQLite 初始化。运行时、持久化、工具安全与更新流程详见[架构文档](./docs/ARCHITECTURE.md)。

## 技术栈

| 组件 | 版本 / 用途 |
|---|---|
| .NET SDK | `8.0.407`，允许滚动到已安装的最新 8.0 feature band |
| Windows App SDK | `2.1.3` / WinUI 3 桌面外壳 |
| CommunityToolkit.Mvvm | `8.4.0` |
| Entity Framework Core | `8.0.28` |
| SQLite | 本地嵌入式持久化 |
| 研究与专业构件 | AngleSharp/PdfPig、ClosedXML/CsvHelper、Open XML/PDFsharp 与 SkiaSharp |
| xUnit / coverlet | `2.9.3` / `10.0.1` |
| Inno Setup | 用户级 x64 安装包 |

## 安装

1. 打开[最新版本](https://github.com/24373054/TLAH-Studio/releases/latest)或[官方下载页](https://download.matrixlabs.cn)。
2. 下载 Windows x64 的 `TLAHStudioSetup-<version>.exe`。
3. 运行安装程序，打开 **Settings → Connection**，配置 Anthropic 或 OpenAI-compatible 端点、模型与 API Key。
4. 选择工作区文件夹或继续使用私有沙箱，设置推理强度与权限模式，然后开始会话。

当前 Authenticode 证书为自签名证书，因此即使安装包已经签名，Windows 仍可能显示“不受信任的发布者”。版本完整性还由 ECDSA 签名元数据与公开 SHA-256 摘要共同保护。详见[发布与签名](./docs/RELEASING.md)。

## 从源码构建

### 环境要求

- Windows 10 build 19041+ 或 Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 与 Windows App SDK / WinUI 工作负载，用于 F5 和 XAML 热重载
- PowerShell 7、Inno Setup 6 与 Windows SDK SignTool 仅在签名发布时需要

```powershell
git clone https://github.com/24373054/TLAH-Studio.git
cd TLAH-Studio

dotnet restore .\TLAHStudio.sln
dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Debug -p:Platform=x64
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release
.\tools\ci.ps1 -Configuration Release -Platform x64
```

使用 Visual Studio 打开 `TLAHStudio.sln`，启动 `TLAHStudio.App` 进行桌面调试。提交改动前请阅读[开发指南](./docs/DEVELOPMENT.md)与[贡献指南](./CONTRIBUTING-CN.md)。

## 仓库结构

```text
TLAHStudio.App/          WinUI 外壳、Views、ViewModels、动效与资源
TLAHStudio.Core/         智能体运行时、模型、工具、研究、构件、MCP 与安全
TLAHStudio.Data/         EF Core 模型、SQLite 初始化与前向迁移
TLAHStudio.Updater/      独立更新辅助程序
TLAHStudio.Installer/    Inno Setup 与签名发布元数据
TLAHStudio.Core.Tests/   xUnit 回归与发布测试
tools/                   CI、签名、验证和部署脚本
docs/                    当前指南与归档设计记录
deploy/download-page/    下载站点资源与服务配置
```

## 安全与数据边界

- API Key 使用 Windows DPAPI 支持的保护机制，并从诊断载荷中脱敏。
- 会话、设置、运行历史与审计记录默认存储在本地 SQLite。
- 模型提示词/响应会发送到用户选择的端点；使用网页、HTTP、MCP、远程执行与更新能力时也会与外部服务通信。
- 工具请求统一经过安全分类与授权；`完全访问` 会按设计绕过普通限制，但绝不会绕过灾难级操作硬阻断。
- Tool Quality 只根据工具名、状态和时间戳计算本地聚合指标，不查询提示词、工具参数/结果或文件内容。
- 安全问题应通过 [GitHub 私密漏洞报告](https://github.com/24373054/TLAH-Studio/security/advisories/new)提交，不要创建公开 Issue。

处理敏感工作区之前，请阅读 [SECURITY.md](./SECURITY.md) 与[隐私和数据流](./docs/PRIVACY.md)。

## 文档

| 文档 | 用途 |
|---|---|
| [文档索引](./docs/README.md) | 当前指南、路线图和历史设计记录 |
| [架构](./docs/ARCHITECTURE.md) | 运行时、持久化、工具、MCP 与更新拓扑 |
| [开发](./docs/DEVELOPMENT.md) | 环境配置、命令、规范与测试 |
| [发布与签名](./docs/RELEASING.md) | 版本同步、CI、签名、验证与部署 |
| [隐私和数据流](./docs/PRIVACY.md) | 本地存储和外部传输边界 |
| [更新日志](./CHANGELOG.md) | 面向用户的版本历史 |
| [支持](./SUPPORT.md) | 使用问题、Bug 报告与诊断信息 |

## 参与贡献

欢迎提交 Issue 和范围明确的 Pull Request。请先阅读[英文贡献指南](./CONTRIBUTING.md)或[中文贡献指南](./CONTRIBUTING-CN.md)，运行完整 CI 质量门，并为可见的 WinUI 改动附上截图。所有参与者都应遵守[行为准则](./CODE_OF_CONDUCT.md)。

本仓库公开可见，但采用专有、源码可见许可，并不构成开源授权。使用或再分发代码前请阅读 [LICENSE](./LICENSE)。第三方组件继续遵循各自条款，详见 [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md)。

## 致谢

TLAH Studio 基于 .NET、Windows App SDK、CommunityToolkit、Entity Framework Core、SQLite、xUnit、TextMate grammars 以及第三方声明中列出的其他项目构建。

<p align="center">
  版权所有 © 2026 北京刻熵科技有限责任公司。保留所有权利。
</p>
