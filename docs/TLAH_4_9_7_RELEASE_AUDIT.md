# TLAH Studio 4.9.7 发布审计

## 系统与交付链路

主程序是 `App -> Core + Data` 的 WinUI 3 桌面应用：`Core` 承载 LLM、Agent Runtime、工具安全与更新逻辑，`Data` 提供 EF Core/SQLite 持久化，`Updater` 独立执行静默安装。标准交付链路为：代码与回归测试 → `tools/ci.ps1` → 自包含 publish → Inno Setup → Authenticode 签名 → `latest.json` ECDSA 签名 → 安装/启动烟测 → Git commit/tag/push → 原子上传。

## 4.9.7 已解决问题

| 优先级 | 问题 | 处理 |
|---|---|---|
| P0 | SQLite、旧 Markdown/xUnit 依赖树包含 High 漏洞 | 升级 EF Core 8.0.28、SQLitePCLRaw 3.0.3/SourceGear SQLite，并覆盖安全的 ColorCode/System 包；CI 新增传递依赖漏洞门禁。 |
| P1 | 后台任务独立 `DbContext` 反射取 `Options` 必然歧义，完成状态可能永久停在 `running` | 精确选择泛型属性，查询改为无跟踪，完成后释放运行表与 CTS，并新增持久化回归测试。 |
| P1 | 灰度发布用随机化 `string.GetHashCode()`，且安装 ID 每次启动重建 | 改用 SHA-256 稳定分桶，并将安装 ID 持久化到本地配置目录。 |
| P1 | 工具参数脱敏后直接参与执行，鉴权参数会变成 `[REDACTED]`；改存明文又会暴露到审批与数据库 | 分离展示参数与执行参数；展示/审计值脱敏，原值以 DPAPI 保护，审批恢复后解密执行；检查点同时保护。 |
| P1 | SessionMemory 等待超时时会释放不属于自己的信号量，并异步枚举仍在变化的消息列表 | 仅在成功获取后释放；异步任务使用快照、观察异常，并脱敏命令预览。 |
| P1 | `IProgress<T>` 异步回调导致工具生命周期在返回结果时丢失进度事件 | 改为同步进度收集器。 |
| P1 | Windows `git apply` 通过 stdin 时存在换行/编码问题，Git for Windows 启动器还会偶发拉起失败 | 使用 BOM-less 临时 patch 文件、规范化换行、预校验路径并返回真实 stderr；Windows 直接解析原生 Git，压力回归连续 20 次通过。 |
| P2 | 轻量迁移会释放 EF Core 持有的 SQLite 连接 | 保留连接所有权，仅关闭迁移方法自行打开的连接。 |
| P1 | 流式 Markdown 同一块增长时 UI 不刷新；超长代码截断后剩余内容被再次解析 | 局部替换最后稳定块；截断后消费至闭合 fence，并补充明确截断标记。 |
| P2 | 滚轮方向判断、流式光标计时器、斜杠菜单异步返回存在竞态 | 按目标偏移判断滚动状态，统一停止孤儿计时器，并在异步完成后重新核对输入。 |
| P1 | 发布脚本直接覆盖线上三文件，客户端可能读取半发布状态；旧部署脚本还可能重写带 BOM 的 JSON | 上传到临时名后在服务器快速提升，`latest.json` 最后作为提交点；部署阶段只验证并上传已签名产物，不再改变 Git 对应内容。 |

## 验证基线

- Release CI：NuGet 漏洞审计、295 项 xUnit、App x64、Updater x64。
- Debug 覆盖率基线：行 56.6%，分支 45.6%。
- 发布门禁：严格 SemVer、版本同步、SHA-256、ECDSA、Authenticode、安装包文件与启动烟测。

## 后续结构性债务

4.9.7 不冒险重写以下非阻断项：`LlmService` 仍保留一套低覆盖的旧 Agent 循环；WinUI 渲染缺少独立自动化测试项目；SQLite 仍采用前向型轻量迁移而非正式 EF migrations；Core 中少数大型服务职责过宽。当前 Authenticode 证书为自签名证书，虽已嵌入完整性签名，但 Windows 无法建立公共信任链；正式商业分发应更换受信任的代码签名证书。建议 5.0 前先拆除旧循环并为更新、工具安全、后台任务和 UI parser 设置覆盖率增长门槛。
