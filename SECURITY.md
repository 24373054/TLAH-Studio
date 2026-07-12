# Security Policy

## Supported Versions

Security fixes are provided for the latest stable release line. Users should update before reporting an issue that may already be fixed.

| Version | Supported |
|---|---|
| 4.12.x | Yes |
| 4.11.x and earlier | No |

## Reporting a Vulnerability

Do not open a public issue for suspected vulnerabilities, leaked secrets, unsafe update behavior, or sandbox/permission bypasses.

1. Use [GitHub Private Vulnerability Reporting](https://github.com/24373054/TLAH-Studio/security/advisories/new).
2. If that channel is unavailable, email `2315766973@qq.com` with the subject `TLAH Studio security report`.
3. Include the affected version, permission mode, execution backend, reproduction steps, expected impact, and any minimal proof of concept.
4. Remove API keys, credentials, personal data, proprietary workspace content, and unrelated logs.

The maintainers will acknowledge a report when received, investigate it privately, and coordinate disclosure after a fix is available. Response times are goals, not guarantees.

## Security Boundaries

- `Full access` intentionally permits host and network access. It is not a sandbox.
- Restricted execution uses command, path, protocol, permission, and backend policy. It is not equivalent to a hardened VM boundary.
- Prompts and selected context are sent to the configured model provider. MCP, web, HTTP, remote execution, and update operations may contact additional endpoints.
- API keys use Windows DPAPI-backed protection and diagnostic redaction, but a compromised user session can still access data available to that user.

## Release Trust Model

TLAH Studio uses three distinct checks:

1. **ECDSA P-256** verifies `latest.json` update metadata with an embedded public key.
2. **SHA-256** verifies that the downloaded installer matches the signed manifest.
3. **Authenticode** signs executables and installers. The current certificate is self-signed, so Windows may report the publisher as untrusted.

See [docs/RELEASING.md](./docs/RELEASING.md) for the verified release process.

---

## 安全策略摘要

仅最新稳定版本线接收安全修复。安全问题请使用 [GitHub 私密漏洞报告](https://github.com/24373054/TLAH-Studio/security/advisories/new)，不要创建公开 Issue。报告中应包含版本、权限模式、执行后端、复现步骤与影响，并删除 API Key、个人数据和无关日志。

`完全访问` 按设计允许访问宿主机和网络；受限执行也不等同于虚拟机隔离。当前 Authenticode 证书为自签名证书，Windows 可能提示发布者不受信任；更新元数据还会独立经过 ECDSA 与 SHA-256 校验。
