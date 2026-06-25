# TLAH Studio

Talk Like A Human — Windows 原生 Prompt 调试框架。

构建于 C# + WinUI 3 + Windows App SDK，完整捕获每次 LLM API 调用的原始 HTTP 请求与响应。

## Agent Runtime

1.3.0 引入可持久化的智能体运行时，1.4.0 将它升级为工具与安全平台：

- OpenAI-compatible 与 Anthropic 原生 tool calling，保留旧 JSON 工具协议回退
- 类型化工具注册表与 JSON Schema 参数定义
- Agent Run、Step、Tool Invocation、Checkpoint、Artifact 和 Permission 持久化
- 文件、Git、HTTP、搜索、浏览器和终端类型化工具
- MCP Client，支持 STDIO 与 Streamable HTTP 传输
- 工具执行前审批：本次允许、此项目允许、始终拒绝
- 受限本地、WSL2、Docker 与远程沙箱执行后端
- HTTPS 域名白名单、私网/回环地址阻断、禁用自动重定向
- Windows DPAPI 凭据代理，按工具和域名授权且不向模型暴露密钥
- 运行时间、输出、文件大小、内存和进程数量限制
- 暂停、停止、重启恢复和扩展步数后继续
- 沙箱产物哈希、大小和类型登记，并进入隐私导出与审计记录

## 构建

```bash
# 还原依赖
dotnet restore

# 编译
dotnet build -c Release

# 自包含发布
dotnet publish TLAHStudio.App/TLAHStudio.App.csproj -c Release -r win-x64 --self-contained true

# 打包安装器
cd TLAHStudio.Installer && iscc setup.iss
```

## 开发

需要 Visual Studio 2022+ 及 Windows App SDK 工作负载。

打开 `TLAHStudio.sln` 即可开始开发，支持 XAML Hot Reload。

## 质量门

```powershell
# 单元测试 + Release 构建
.\tools\ci.ps1

# 验证已生成的安装包、latest.json 签名、SHA256、Authenticode 状态
.\tools\verify-release.ps1 -Version 1.4.0 -AllowUntrustedAuthenticode
```

`verify-release.ps1` 支持安装包 smoke install，但会在检测到当前用户已有安装时自动跳过，避免破坏本机安装登记。只在一次性测试机上使用 `-ForceSmokeInstall`。

## 配置

- `appsettings.json` — 应用级配置（数据库路径、更新服务器 URL）
- `%LOCALAPPDATA%\TLAH Studio\data\tlah.db` — 用户数据
- 首次启动可在设置中配置 LLM Provider（API Key、模型等）
- 侧边栏 `Tools & Security` — MCP、执行后端、网络、凭据、权限和资源限制

## 部署

1. 下载最新 `TLAHStudioSetup-x.y.z.exe`
2. 双击安装（用户级安装，无需管理员权限）
3. 支持自动更新：App 启动时检查 `latest.json`，通过 Updater.exe 静默升级
