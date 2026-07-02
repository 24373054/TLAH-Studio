# TLAH Studio

> Talk Like A Human — Windows 原生 AI 智能体工作台

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue?logo=windows)](https://www.microsoft.com/windows)
[![Version](https://img.shields.io/badge/version-4.4.2-brightgreen)](https://download.matrixlabs.cn/tlah/windows/latest.json)
[![License](https://img.shields.io/badge/license-Proprietary-red)](./LICENSE)

TLAH Studio 是一款 Windows 原生 AI 智能体（Agent）工作台，使用 C#、WinUI 3 和 Windows App SDK 构建。它将聊天对话、工具执行、MCP 集成、提示词调试和持久化活动时间线融于一个桌面应用中。

与基于 Web 的编码助手（如 Claude Code、Cursor、GitHub Copilot）不同，TLAH Studio 强调**本地化、可调试和完全受控**的智能体运行体验——每次运行的完整原始数据（包括 LLM 请求/响应、智能体步骤、工具调用、审批记录、构件文件）都持久化存储在本地 SQLite 数据库中，你可以随时查阅、调试和审计。

---

## 核心功能

### 🤖 智能体模式
- 默认启用，直接从输入栏发送任务
- **权限模式**：完全访问 / 自动批准 / 每次询问，完全对标编码智能体工作流
- 每个聊天可选择独立的工作区文件夹，不选则使用私有沙箱
- 长链路运行支持暂停/恢复、检查点保存，应用重启后可从断点恢复
- 右侧 **Agent Activity** 面板可回放历史运行记录

### 🛠 内置工具集（40+ 工具）

| 类别 | 工具 | 说明 |
|---|---|---|
| **文件** | `file_read` `file_write` `file_list` `file_search` `file_mkdir` `file_move` `file_delete` | 完整的文件系统操作 |
| **代码** | `read` `grep` `glob` `edit` `multi_edit` `diff` `apply_patch` `rollback` `lsp_diagnostics` `symbols` | 代码阅读、搜索、编辑和回滚 |
| **终端** | `terminal_exec` | 沙箱化的命令行执行 |
| **版本控制** | `git` | Git 操作 |
| **网络** | `http_request` `web_search` `browser_read` | HTTP 请求、网页搜索、页面读取 |
| **MCP** | `mcp_list_tools` `mcp_call` `mcp_list_resources` `mcp_read_resource` | 模型上下文协议集成 |
| **记忆** | `memory_read` `memory_write` `memory_list` | 跨对话持久化记忆 |
| **任务** | `task_create` `task_update` `task_list` `task_output` `task_stop` `task_send_message` | 后台任务和 TODO |
| **辅助** | `todo_write` `tool_search` `read_persisted_output` | 任务管理和输出恢复 |

### 🔌 MCP（模型上下文协议）
- 支持 STDIO 和 Streamable HTTP 两种传输方式
- 自动发现工具和资源
- 可在设置中配置多个 MCP 服务器

### 🔍 调试面板
- 捕获并展示每次 LLM API 调用的**完整原始 HTTP 请求和响应**
- 支持 JSON 格式查看和复制
- 帮助排查提供商兼容性问题

### 🔄 自动更新
- RSA 数字签名保证更新包完整性
- SHA256 校验防止下载损坏
- 支持灰度发布（按安装 ID 哈希分桶，0-100% 可控）
- 支持强制更新（最低版本检查）
- 静默安装，无需管理员权限

### 🔒 安全机制
- API 密钥使用 Windows DPAPI（`ProtectedData`）加密存储
- 密钥自动脱敏，不会出现在日志或调试输出中
- 工具安全流水线：安全分类 → 效果预测 → 审批门控 → 沙箱执行
- 网络安防：HTTPS 白名单、私有/回环地址拦截、重定向控制
- 多种执行后端：本地受限、WSL、Docker（无网络）、远程沙箱

---

## 技术栈

| 技术 | 版本 | 用途 |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/) | 8.0.407 | 运行时和 SDK |
| [Windows App SDK](https://learn.microsoft.com/windows/apps/windows-app-sdk/) | 2.1.3 | WinUI 3 桌面应用框架 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | MVVM 框架 |
| [Entity Framework Core](https://learn.microsoft.com/ef/core/) | 8.0.14 | 数据访问层 |
| [SQLite](https://www.sqlite.org/) | — | 本地嵌入式数据库 |
| [xUnit](https://xunit.net/) | 2.5.3 | 单元测试框架 |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 6 | 安装包制作 |

---

## 项目结构

```
TLAH/
├── TLAHStudio.App/          # WinUI 3 桌面应用
│   ├── Views/               # XAML 页面、控件和对话框
│   ├── ViewModels/          # MVVM 视图模型
│   ├── Models/              # UI 模型 (ChatMessageBlock, ChatRenderer)
│   ├── Converters/          # XAML 值转换器
│   └── Assets/              # 图标、字体、图片资源
│
├── TLAHStudio.Core/         # 核心业务逻辑
│   ├── Llm/                 # LLM 提供商抽象层
│   ├── Models/              # 领域模型和数据实体
│   ├── Services/            # 服务层
│   │   ├── AgentRuntime/    # 智能体运行时状态机
│   │   ├── Context/         # 上下文管理和压缩
│   │   ├── Tools/           # 工具系统 (V3) 和钩子管线
│   │   ├── Lsp/             # LSP 诊断管理
│   │   ├── Memory/          # 持久化记忆系统
│   │   ├── Observability/   # 运行时指标
│   │   ├── Plugins/         # 插件发现和信任管理
│   │   ├── Sdk/             # 本地 SDK (HTTP + 命名管道)
│   │   ├── Workspace/       # 工作区根目录解析
│   │   └── Background/      # 后台任务服务
│   └── Helpers/             # 安全辅助类
│
├── TLAHStudio.Data/         # 数据访问层
│   └── TlahDbContext.cs     # EF Core 上下文 + 轻量级迁移
│
├── TLAHStudio.Updater/      # 独立更新程序（单文件发布）
├── TLAHStudio.Installer/    # Inno Setup 安装脚本和发布元数据
├── TLAHStudio.Core.Tests/   # xUnit 单元测试（~30 个测试类）
│
├── tools/                   # 构建、签名、验证、部署脚本
├── docs/                    # 开发计划和架构文档
├── deploy/                  # 下载页面网站资源
└── artifacts/               # 构建产物（不提交）
```

### 依赖关系

```
TLAHStudio.App  ──→  TLAHStudio.Core  +  TLAHStudio.Data
                          ↑
                    (Core 定义接口和模型, Data 实现 EF 上下文)
```

`Core` 和 `Data` 没有直接耦合——`Core` 定义业务接口和领域模型，`Data` 使用这些模型构建 EF Core 数据上下文。

---

## 快速开始

### 环境要求

- **操作系统**：Windows 10（版本 19041+）/ Windows 11
- **SDK**：[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（`global.json` 锁定 8.0.407）
- **IDE**：Visual Studio 2022+，需安装 **Windows App SDK / WinUI** 工作负载
- **安装包构建**（可选）：[Inno Setup 6](https://jrsoftware.org/isinfo.php)

### 构建和运行

```powershell
# 1. 克隆仓库
git clone <repo-url>
cd TLAH

# 2. 恢复依赖
dotnet restore

# 3. Debug 构建
dotnet build TLAHStudio.sln -c Debug

# 4. 运行桌面应用
# 方式一：在 Visual Studio 中按 F5 启动（支持 XAML 热重载）
# 方式二：从构建输出目录运行
start TLAHStudio.App\bin\x64\Debug\net8.0-windows10.0.19041.0\TLAHStudio.App.exe

# 5. 运行测试
dotnet test TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release

# 6. 运行 CI 质量门
.\tools\ci.ps1 -Configuration Release -Platform x64
```

### 发布构建（独立可执行文件）

```powershell
dotnet publish TLAHStudio.App\TLAHStudio.App.csproj `
  -c Release -r win-x64 --self-contained true
```

---

## 开发指南

### 常用命令汇总

| 命令 | 用途 |
|---|---|
| `dotnet restore` | 恢复所有 NuGet 依赖 |
| `dotnet build TLAHStudio.sln -c Debug` | Debug 构建 |
| `dotnet build TLAHStudio.App\TLAHStudio.App.csproj -c Release -p:Platform=x64` | App 项目 Release 构建 |
| `dotnet test TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release` | 运行测试 |
| `dotnet publish ... -r win-x64 --self-contained true` | 独立发布 |
| `.\tools\ci.ps1 -Configuration Release -Platform x64` | CI 质量门 |
| `cd TLAHStudio.Installer && iscc setup.iss` | 编译安装包 |
| `.\tools\verify-release.ps1 -Version 4.4.2 -AllowUntrustedAuthenticode` | 验证发布包 |

### 架构概览

TLAH Studio 采用分层架构，核心业务流程如下：

```
┌─────────────────────────────────────────────────────┐
│                    WinUI 3 视图层                      │
│    Views (.xaml) ←→ ViewModels (CommunityToolkit)     │
├─────────────────────────────────────────────────────┤
│                    服务层 (Services)                   │
│   ILlmService → IChatService → IToolPlatformService   │
│         ↓                                              │
│   智能体运行时 (AgentRuntime)                           │
│   AgentRunEngineV2 — 状态机 while 循环                  │
│   每一步 → AgentRunFrame → 事件流 → UI + SDK            │
├────────────┬────────────────────┬───────────────────┤
│  LLM 抽象层  │   工具系统 (V3)       │   安全流水线        │
│  HttpClient │  IAgentToolV3       │  ToolSafetyKernel │
│  原始捕获    │  ToolLifecycleRunner │  ProtocolGuard    │
│             │  ToolHookPipeline   │  沙箱执行           │
├────────────┴────────────────────┴───────────────────┤
│                EF Core + SQLite 数据层                 │
│    TlahDbContext → %LOCALAPPDATA%\TLAH Studio\        │
└─────────────────────────────────────────────────────┘
```

### 关键设计模式

1. **原始 HTTP 捕获**：`ILlmProvider` 实现直接使用 `HttpClient`，不用官方 SDK。每次请求/响应完整序列化到 `RawRequest`/`RawResponse` 并存入数据库。

2. **流式 + 持久化双通道**：`SendMessageAsync` 接受可选的 `IProgress<LlmStreamUpdate>` 做 UI 实时流式输出，同时缓冲完整响应用于数据库持久化。

3. **智能体工具安全流水线**：
   ```
   模型输出解析 → 安全分类 → 安全评估 → 审批门控 → 执行器调度 → 沙箱执行
   ```

4. **带钩子的工具生命周期**：`ToolHookRegistry` 支持 `BeforeUse`/`AfterUse`/`AfterFailedUse` 钩子，`IToolLifecycleRunner` 统一管理预览→执行→回滚全流程。

5. **EF Core 轻量级迁移**：`ApplyLightweightMigrations()` 使用 `ALTER TABLE ADD COLUMN` / `CREATE TABLE IF NOT EXISTS` 做前向兼容变更，无标准迁移，不提供回滚。

6. **DI 主机模式**：应用启动时通过 `Microsoft.Extensions.Hosting` 注册所有服务、工具和视图模型，支持完整的依赖注入。

7. **状态机提取**：`AgentRunEngineV2` 是独立的智能体 while 循环，发出类型化的 `AgentRunFrame` 记录，同时供 WinUI 和本地 SDK HTTP 服务消费。状态为不可变 `AgentRunState` 记录，提供显式 `DeepClone()` 方法。

8. **渐进式上下文压缩**：`ReactiveCompactor` 按激进程度依次尝试：修剪工具输出 → 微压缩 → 中间摘要 → 模型辅助摘要 → 紧急截断。压缩后注入结构化运行时上下文（项目记忆、活跃任务、最近文件、待解决问题）。

### 编码规范

- C# 启用 nullable 引用类型和 implicit usings，使用文件范围命名空间，4 空格缩进
- 异步方法以 `Async` 结尾
- 数据传输对象优先使用不可变 `record` 类型
- WinUI 视图和 ViewModel 按名称配对（如 `ChatPage.xaml` + `ChatPageViewModel`）
- LLM 提供商必须直接使用 `HttpClient`，不得引入官方 SDK
- 测试命名格式：`方法或功能_条件_预期结果`
- Windows 特有行为使用 `OperatingSystem.IsWindows()` 守卫

---

## 发布与部署

### 完整发布流水线

```powershell
.\tools\build-release.ps1 `
  -Version 4.4.2 `
  -ReleaseNotes "<发布说明>" `
  -CertificateThumbprint F6DC173C746447A05FF83B9F7162121344CC09F0 `
  -AllowUntrustedCertificate `
  -ForceSmokeTest `
  -Upload
```

此脚本自动完成：构建 → Authenticode 签名 → 打包 Inno Setup → 计算 SHA256 → 更新 `latest.json` → RSA 签名 → 冒烟测试 → 上传到服务器。

### 版本同步清单

发布新版本时，以下位置的版本号必须保持一致：

| 位置 | 字段 |
|---|---|
| `TLAHStudio.App/appsettings.json` | `App.Version` |
| 每个 `.csproj` 文件 | `<Version>`, `<FileVersion>`, `<AssemblyVersion>`, `<InformationalVersion>` |
| `TLAHStudio.Installer/version.json` | `version` |
| `TLAHStudio.Installer/latest.json` | `version` |
| `TLAHStudio.Installer/setup.iss` | `#define MyAppVersion` |

当前版本：**4.4.2**，语义化版本号 (`Major.Minor.Patch`)。

### 更新机制

```
客户端启动 (3s 后)
    ↓
获取 latest.json ← https://download.matrixlabs.cn/tlah/windows/
    ↓
验证 RSA 数字签名
    ↓
检查版本 > 当前版本？(考虑 rolloutPercent 灰度分桶)
    ↓ 是
下载安装包 → 校验 SHA256
    ↓
启动 TLAHStudio.Updater.exe
    ↓
主应用退出 → Inno Setup 静默安装 → 重新启动新版本
```

`latest.json` 关键字段说明：

| 字段 | 说明 |
|---|---|
| `version` | 最新语义版本号 |
| `channel` | 发布通道：`stable` / `beta` |
| `installerUrl` | 安装包下载地址 |
| `sha256` | 安装包 SHA256（小写十六进制） |
| `rolloutPercent` | 灰度比例 0–100（按安装 ID 哈希分桶） |
| `forceUpdate` | `true` 则用户不可跳过更新 |
| `minSupportedVersion` | 低于此版本强制更新 |

### 服务器部署

更新文件托管在 `download.matrixlabs.cn` 的 Nginx 上：

```
/var/www/download/tlah/windows/
├── latest.json
├── latest.json.sig
├── TLAHStudioSetup-x.y.z.exe
└── ...
```

详细部署指南见 [SERVER-DEPLOY.md](./SERVER-DEPLOY.md)。

### Authenticode 证书

```
CN="Beijing Ke Entropy Technology Co., Ltd., O=Beijing Ke Entropy Technology Co., Ltd., C=CN"
Thumbprint: F6DC173C746447A05FF83B9F7162121344CC09F0
```

---

## 本地数据

所有应用数据存储在用户本地，不上传任何信息：

| 路径 | 内容 |
|---|---|
| `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` | SQLite 主数据库（聊天记录、设置、智能体运行数据） |
| `%LOCALAPPDATA%\TLAH Studio\config\` | 本地 UI 和工作区配置 |
| `%LOCALAPPDATA%\TLAH Studio\sandboxes\` | 私有聊天沙箱目录（未选择工作区时使用） |
| `%LOCALAPPDATA%\TLAH Studio\logs\` | 应用和启动日志 |
| `{workspace}\.tlah_context\tool-results\` | 持久化的大型工具输出文件 |

API 密钥使用 Windows DPAPI 加密保护，绝不应提交到版本控制或对外导出。

### 数据库表概览

SQLite 数据库中包含 20+ 张表，核心实体关系：

```
Chat ──→ Message ──→ Turn ──→ AgentRun ──→ AgentStep ──→ ToolInvocation
                                     ├──→ AgentCheckpoint
                                     ├──→ AgentArtifact
                                     └──→ AgentEvent
GlobalSettings (单例)    ChatSettings    ConfigProfile
ProjectSpace             PromptTemplate  AuditLogEntry
AgentTaskItem            ToolPermission  ToolPolicyRule
McpServerConfig          CredentialEntry ToolPlatformSettings
BackgroundTaskRecord
```

所有 LLM API 调用的原始请求/响应存储在 `RawRequest` / `RawResponse` 表中。

---

## 开发计划

当前开发路线图参见 `docs/` 目录下的计划文档：

| 文档 | 内容 |
|---|---|
| [TLAH_3_0_AGENT_DEVELOPMENT_PLAN.md](./docs/TLAH_3_0_AGENT_DEVELOPMENT_PLAN.md) | 3.0.0 智能体开发计划（8 个里程碑：运行时所有权、UI 虚拟化、工具调度器、上下文/记忆、代码智能、MCP/插件、沙箱/后台、SDK/可观测性） |
| [TLAH_3_3_STABILITY_AND_TOOL_LIFECYCLE_PLAN.md](./docs/TLAH_3_3_STABILITY_AND_TOOL_LIFECYCLE_PLAN.md) | 3.3.0 稳定性和工具生命周期（统一工具管线、安全预览、效果规划、钩子系统、回滚支持） |
| [TLAH_4_0_AGENT_PLATFORM_PLAN.md](./docs/TLAH_4_0_AGENT_PLATFORM_PLAN.md) | 4.0 智能体平台计划（持久化任务层、上下文恢复、本地后台智能体、工具发现、事件表面） |
| [TLAH_4_1_1_TOOL_CAPABILITY_PLAN.md](./docs/TLAH_4_1_1_TOOL_CAPABILITY_PLAN.md) | 4.1.1 工具能力增强（文件管理、代码符号、web_search 加固、tool_search 改进） |

### 版本历史

- **v4.4.2** — 当前版本
- **v4.0** — 智能体平台：任务层、上下文恢复、后台智能体、SDK
- **v3.3** — 稳定性：统一工具生命周期、安全预览、效果规划、钩子系统
- **v3.0** — 智能体 GA：运行时提取、UI 虚拟化、工具调度器、MCP 集成
- **v2.x** — MVP：基础聊天、LLM 提供商、调试面板
- **v1.x** — 概念验证

---

## 工具脚本

`tools/` 目录下的 PowerShell 脚本：

| 脚本 | 用途 |
|---|---|
| `ci.ps1` | 本地 CI：还原 → 测试 → Release 构建 |
| `build-release.ps1` | 全量发布：构建 → 签名 → 打包 → 验证 → 冒烟测试 → 上传 |
| `verify-release.ps1` | 验证发布：`latest.json` 签名检查、SHA256 校验、Authenticode 验证、可选冒烟安装 |
| `deploy.ps1` | 通过 SCP 上传安装包和元数据到更新服务器 |
| `sign-latest.ps1` | 用 RSA 私钥对 `latest.json` 进行数字签名 |
| `sign-authenticode.ps1` | 对安装包应用 Authenticode 数字签名 |
| `generate-keys.ps1` | 生成 RSA 密钥对用于更新签名 |

---

## 许可证与版权

版权所有 &copy; 2026 北京刻熵科技有限公司（Beijing Ke Entropy Technology Co., Ltd.）。保留所有权利。

---

*本 README 同时提供[英文版](./README.md)。For the English version, see [README.md](./README.md).*
