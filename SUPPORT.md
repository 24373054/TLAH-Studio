# Support

## Where to Ask

| Need | Channel |
|---|---|
| Installation, configuration, or usage question | Open a [support issue](https://github.com/24373054/TLAH-Studio/issues/new?template=bug_report.yml) with the `question` context |
| Reproducible bug | Use the [bug report](https://github.com/24373054/TLAH-Studio/issues/new?template=bug_report.yml) template |
| Product idea | Use the [feature request](https://github.com/24373054/TLAH-Studio/issues/new?template=feature_request.yml) template |
| Security vulnerability | Use [private vulnerability reporting](https://github.com/24373054/TLAH-Studio/security/advisories/new) |

Before filing, update to the latest release and search existing issues.

## Useful Diagnostics

Include:

- TLAH Studio version and Windows build
- Provider protocol and model name, without the API key
- Permission mode, reasoning setting, workspace/sandbox mode, and execution backend
- Minimal steps, expected result, actual result, and screenshots where relevant
- A redacted diagnostics export when the problem involves startup, updates, or provider compatibility

Never attach API keys, authorization headers, private prompts, customer data, signing keys, full local databases, or confidential workspace files.

## Common Checks

1. Confirm the provider base URL, protocol, model, and key in **Settings → Connection**.
2. Retry in a new chat with the private sandbox and **Ask each time** permissions.
3. Disable optional MCP servers or remote execution backends to isolate the failure.
4. Check **Agent Activity** and the debug surface for a redacted error.
5. For update failures, compare the installed version with the [latest release](https://github.com/24373054/TLAH-Studio/releases/latest).

---

## 中文支持说明

安装、配置和可复现 Bug 请使用仓库 Issue 模板；功能建议使用 Feature Request；安全问题必须使用私密漏洞报告。提交前请升级到最新版本并搜索现有 Issue。

请提供版本、Windows build、模型协议/模型名、权限模式、推理设置、工作区模式、执行后端、最小复现步骤与必要截图。不要上传 API Key、Authorization Header、隐私提示词、客户数据、签名私钥、本地数据库或机密工作区文件。
