# 为 TLAH Studio 贡献代码

感谢你帮助改进 TLAH Studio。本指南用于确保改动可审阅、可复现，并符合 Windows 桌面产品的设计约束。

English version: [CONTRIBUTING.md](./CONTRIBUTING.md).

## 开始之前

- 提交前先搜索现有 [Issues](https://github.com/24373054/TLAH-Studio/issues)，避免重复。
- Bug 和功能建议使用公开 Issue；安全问题使用[私密漏洞报告](https://github.com/24373054/TLAH-Studio/security/advisories/new)。
- Pull Request 应保持范围明确。架构变化应在实现前说明替代方案与迁移影响。
- 阅读 [LICENSE](./LICENSE)。这是专有、源码可见仓库，外部贡献不会将其转为开源项目。

## 开发环境

- Windows 10 build 19041+ 或 Windows 11
- 由 `global.json` 选择的 .NET 8 SDK
- Visual Studio 2022 与 Windows App SDK / WinUI 工作负载
- 建议使用 PowerShell 7 运行仓库脚本

```powershell
dotnet restore .\TLAHStudio.sln
dotnet build .\TLAHStudio.App\TLAHStudio.App.csproj -c Debug -p:Platform=x64
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release
.\tools\ci.ps1 -Configuration Release -Platform x64
```

## 开发流程

1. Fork 仓库或创建能描述改动的分支，例如 `fix/activity-theme`。
2. 行为变化只要能够脱离 WinUI 测试，就应增加回归测试。
3. 遵循相邻代码的架构和风格，不要夹带无关格式化或生成文件。
4. 运行完整 CI 质量门，并手动验证受影响的 WinUI 流程。
5. 使用仓库模板创建 Pull Request。

## 代码与界面规范

- C# 使用四空格缩进、文件范围命名空间、可空引用类型；异步方法以 `Async` 结尾。
- 类型和公共成员使用 PascalCase，局部变量/参数使用 camelCase，私有字段使用 `_camelCase`，接口以 `I` 开头。
- View 与 ViewModel 按功能配对；显式管理 XAML 事件生命周期，并解除长生命周期订阅。
- 保持工具安全流水线与权限语义，不得静默扩大文件、命令、网络或凭据访问范围。
- 同时验证明暗主题、紧凑密度、窄窗口、键盘、屏幕阅读器与减少动态效果。
- 不要提交 API Key、本地数据库、签名私钥、安装包、日志或用户工作区数据。

## 测试

项目使用 xUnit。测试文件命名为 `*Tests.cs`，方法名称应描述行为，例如 `VerifySignature_TamperedData_ReturnsFalse`。

```powershell
# 全部测试
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release

# 指定测试
dotnet test .\TLAHStudio.Core.Tests\TLAHStudio.Core.Tests.csproj -c Release `
  --filter "FullyQualifiedName~UpdateCryptoTests"
```

项目没有数值化覆盖率门槛。智能体运行时、持久化、隐私、更新和工具安全的新分支应按风险充分覆盖。

## Pull Request 检查清单

- 说明面向用户的行为和问题根因。
- 关联对应 Issue 或设计讨论。
- 写明验证命令与结果。
- 可见 WinUI 改动附带前后截图。
- 记录配置、存储、安全或发布变化。
- 普通功能 PR 不应包含版本号升级和生成的发布签名。

提交贡献即表示你确认拥有提交该内容的权利，并同意 [LICENSE](./LICENSE) 中的贡献授权条款。
