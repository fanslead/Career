# Career

.NET 10 + SQLite + Microsoft Agent Framework + React 的本地工作区。正式工程已开始分阶段实现，当前批次聚焦工作台交办、计划确认、MAF 执行、固定工具许可和成果版本验收。后续协作、知识、外发与安装发行仍按 [生产工作包](docs/15-production-work-packages.md)推进。

## 开发运行

需要 .NET SDK 10.0.400 与 Node.js 24。

```sh
./packaging/run-development.sh
```

打开启动窗口给出的本机地址，输入一次配对码。在设置中保存 OpenAI 兼容 Chat Completions 模型连接，单独测试文本和工具能力。工作台可先保存任务草稿，执行前需要连接测试通过。

首次工作区为空；没有预置示例任务或固定模型。模型密钥由本机加密凭据库保存，浏览器不回显密钥。macOS 使用 Keychain，Windows 使用 DPAPI；Linux/Docker 需要部署时提供受保护的 `CAREER_VAULT_KEY_FILE`（32 字节或 base64，0400/0600）。工作区默认在 `artifacts/development-workspace`，可通过 `CAREER_WORKSPACE` 选择其他本机目录。

## 本机发布

```sh
./packaging/build-local.sh
```

产物在 `artifacts/release/<RID>/app`。macOS 可双击 `Career.command`，或启动 `Career.Host`。Host 是 Native AOT；MAF Worker 为普通 .NET 的自包含执行器。发布包使用自身的 `worker/` 和 `wwwroot/`，macOS arm64 已在找不到已安装 dotnet 的环境中验证。当前默认仅监听 loopback，浏览器需要所有者配对；远程部署、签名和安装器属于后续发布阶段。

当前是内部可用的首个闭环，默认工作区为空。真实模型接入和内容质量验证按用户选择延期；完整支持矩阵与后续工作见 [本批交付记录](docs/18-production-m1-delivery.md)。未经签名的开发/原生程序可能具有不同的钥匙串访问身份；无法读取原主密钥时保留旧库并提示恢复，不能用新密钥覆盖。

## 最小离线备份与恢复

先退出使用同一工作区的 Host。备份需要工作区独占锁并核对旧执行者，目标 ZIP 不得已存在；恢复只写入不存在或空目录。

```sh
./Career.Host --workspace /path/to/workspace --backup /path/to/backup.zip
./Career.Host --restore /path/to/backup.zip --workspace /path/to/empty-directory
```

归档包含一致的独立 SQLite 文件、权威配置、成果与加密凭据库，每个文件有 SHA-256 清单。系统主密钥不导出；异机恢复可能需要重新配置模型凭据。恢复首次启动撤销旧浏览器会话、暂停新派发，核对任务与成果后再继续。当前不支持活跃 Host 在线备份或完整升级/回滚。

## 验证

```sh
dotnet test Career.slnx -c Release
cd apps/web && npm test && npm run build
```

集成脚本 `tests/integration/m1_flow.py` 在独立临时工作区启动 Host 和测试专用模型服务，检查真实 HTTP/MAF、幂等、拒绝后改稿、固定版本验收、内容完整性、会话/来源、额度、重启、暂停与取消；`backup_flow.py` 另验证归档/损坏保护/恢复屏障。测试服务不会成为生产默认模型。脱敏结果、截图与未运行门槛见 [本批记录](docs/18-production-m1-delivery.md)。

## 工程

- `src/Career.Contracts`：版本化 DTO 和 Worker 协议。
- `src/Career.Core`：任务状态、计划、审批、验收、操作与调用账本。
- `src/Career.Infrastructure`：SQLite、文件发布、凭据和 Worker 监管。
- `src/Career.Host`：所有者会话、HTTP/SSE 和执行协调。
- `src/Career.AgentWorker`：真实模型 SDK、MAF 审批和会话续接。
- `apps/web`：生产 React 工作台。
- `experiments/p0`：独立实验和已有回归证据。

[生产合同](docs/17-production-m1-contract.md) · [交互原型](docs/prototype/index.html) · [设计与验收文档](docs/README.md)
