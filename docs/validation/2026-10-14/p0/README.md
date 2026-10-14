# P0 集成与故障验证记录

更新：2026-10-14。范围仍是 `experiments/p0/` 的技术实验，没有展开完整业务开发。原始恢复修复轮的证据保留在下文；随后用户授权复审、修复并在验证通过后提交本地 Git，复审交付记录见 [代码与文档复审](review-and-delivery.md)。

**当前结论：macOS arm64、Linux arm64 Docker 本地持久卷、Linux x64 Docker 本地持久卷的最终脚本均为 23/23 通过，两项原始恢复问题及补充边界回归通过。Linux x64 使用 arm64 宿主上的 amd64 仿真，不是原生 x64 硬件。初轮共享挂载上的双 Host 锁问题已增加启动拒绝防线，该挂载不列为支持配置。** 这是明确配置下的运行器证据，不能把 [08 的完整产品矩阵](../../../08-delivery-and-validation.md)全部改成通过。

## 1. 实测结果与证据

| 验证 | 结果 | 原始证据 |
| --- | --- | --- |
| macOS arm64 Native AOT Host | 发布、启动、HTTP、静态资源、SQLite/FTS5、SSE 补读通过 | [最终发布日志](logs/recovery-pass/host-publish-osx-arm64-lock-guard.log)、[23 项结果](results/macos-lock-guard-final/summary.json) |
| 普通 .NET MAF Worker | 发布及四组独立场景通过；完整 Host 套件再次覆盖调用与恢复 | [发布日志](logs/recovery-pass/worker-publish.log)、[独立验证](logs/recovery-pass/worker-smoke.log) |
| Linux arm64 + Docker 本地持久卷 | **通过：23/23**，Native AOT、完整集成及故障恢复 | [完整结果](results/linux-arm64-volume-final/validation/summary.json)、[发布日志](results/linux-arm64-volume-final/docker-build.log)、[环境](results/linux-arm64-volume-final/environment.txt)、[资源清理](results/linux-arm64-volume-final/resource-cleanup.log) |
| Linux x64 + Docker 本地持久卷 | **通过：23/23**，Native AOT、完整集成及故障恢复；amd64 仿真 | [完整结果](results/linux-x64-volume-final/validation/summary.json)、[发布日志](results/linux-x64-volume-final/docker-build.normalized.log)、[环境](results/linux-x64-volume-final/environment.txt)、[资源清理](results/linux-x64-volume-final/resource-cleanup.log) |
| Linux arm64 容器首轮 | **失败：21/22**；同一共享工作区第二个 Host 也启动成功 | [首轮结果](results/linux-arm64-container/validation/summary.json)；其余 21 项通过，但不能据此放行双写保护 |
| 原问题共享挂载的启动防线 | **通过：拒绝启动**；错误为 `workspace_lock_not_enforced`，未创建 `app.db` | [实际共享挂载验证](results/linux-arm64-container/diagnostics/lock-guard-bind-rejected/assertion.json) |
| React 生产构建 | 通过 | [构建日志](logs/recovery-pass/web-build-final.log) |
| 浏览器交互 | 审批跨 Host 重启、待审批取消、收尾续接通过，并核对数据库/回执/事件 | [结果与操作边界](results/browser/summary.json)、[审批恢复截图](results/browser/approval-restart-succeeded.png)、[取消截图](results/browser/cancelled.png)、[待收尾截图](results/browser/finalization-pending.png)、[收尾成功截图](results/browser/finalization-succeeded.png) |

浏览器前三条场景中的审批重启与取消使用复核补丁前的 Host；收尾续接使用复核后的 Host。界面代码相同，完整 23 项进程套件已使用加入启动锁防线后的最终二进制重跑。结果 JSON 记录这一分界。

本机实测环境：macOS 27 / arm64，.NET SDK 10.0.400，Python 3.14。Host 使用 `Microsoft.Data.Sqlite 10.0.13`，实际 SQLite `3.53.3`；Worker 使用 `Microsoft.Agents.AI 1.24.0`，保持普通 .NET 进程。MAF 是真实框架，模型是确定性 `ScriptedIChatClient`，**没有调用真实模型供应商**。实际副作用是 Host 在每个用例的隔离目录写入专用回执，Worker 仅通过 stdio RPC 请求它。

[源码、依赖与 macOS 二进制指纹](results/source-and-macos-binaries.json)、[Linux arm64 二进制指纹](results/linux-arm64-volume-final/binary-fingerprints.normalized.txt)、[Linux x64 二进制指纹](results/linux-x64-volume-final/binary-fingerprints.txt)记录实际交付物。[最终核对索引](results/final-verification-index.json)汇总三轮套件、进程清理和文件一致性。每个用例都保留独立目录、SQLite 状态、事件快照、回执/暂存/隔离文件、Host 日志、Run/Operation ID、进程 PID 和清理结果。`summary.json` 指向该轮 `runs/<executionId>/`，重跑不会覆盖上一轮状态证据。

以上为原始恢复修复轮的采集快照；其中“未提交”及源码指纹描述采集时状态，不代表后续复审交付状态。摘要内的绝对路径定位本机原始证据，不是可移植仓库链接。Git 只纳入源码、文档和文中直接引用的验证摘要、日志、截图；数据库、WAL/SHM、构建产物和完整逐用例状态目录保留在本机，不随提交分发。

Linux 两轮均运行 Ubuntu 24.04 / .NET SDK 10.0.400 的验证镜像。x64 Host 实际为 ELF64 x86-64，并使用 x64 .NET runtime 启动托管 Worker；Docker daemon 为 linux/arm64，因此本轮只证明仿真环境下的构建与功能，不作为原生 x64 性能或部署验收。

## 2. 两项恢复修复

### 回执半写与损坏

原实现直接创建最终回执文件，进程在写入中断时可能留下半截文件。现在先在同一工作区 `staging/` 创建独占临时文件，写完整并刷盘，再以不覆盖方式发布至 `receipts/`；最终文件内容必须与 Operation ID 和已批准正文的规范字节一致。

恢复时按实际证据处理：完整暂存文件可核验后发布；半截暂存文件先隔离，确认该专用目标没有发布结果后才允许同一 Operation 重试；最终文件损坏或内容冲突则持久标记 `Quarantined` / `UnknownOutcome`，阻止重放，再搬到隔离目录。隔离标记落库后若搬移未完成，重启会继续搬移。最终回执数量只统计内容有效的文件。

这项“未发布即允许重试”仅成立于 **Host 独占写入的 P0 专用文件目标**，不适用于邮件、任意 shell 或外部 API。

### 工具成功后收尾中断

当工具账本已经 `Succeeded`，但 Worker 退出、超时或最终 Run 提交未完成时，Run 进入 `FinalizationPending`，保留原审批与 MAF 会话安全点。用户可在界面选择“继续整理结果”，Host 重启也可从这一边界继续。

恢复仍使用原 Operation ID；Host 核验已有回执后返回已保存结果，记录 `ToolResultReused`，不会重复写入。补充复核发现的“收尾前回执后来损坏”也已修复：缓存复用前再次核验，损坏/缺失时进入待核对并隔离，不报成功。

另增加最小取消路径以验证 `CRASH-11` 的运行器部分：先持久化取消意图，再停止 Worker；有未决副作用时保留 `UnknownOutcome`，核对后才结束取消，已发生的效果不回滚。这不是完整 Task、父子协作或暂停系统。

## 3. 完整脚本的 23 项

以下 23 项在 macOS arm64、Linux arm64 Docker 本地卷、Linux x64 Docker 本地卷（amd64 仿真）最终轮均通过。这是同一组 23 个场景在三种配置上的 69 次用例执行，不是 69 个不同场景。

| 用例 | 核验的事实 |
| --- | --- |
| `normal` | 原生 Host、React 静态资源、SQLite/FTS5、真实 MAF 工具循环、审批 hash、重复决定拒绝、SSE 游标重连及唯一回执 |
| `denied` | 拒绝后零副作用；权威状态为 Denied，不依赖模型措辞 |
| `invalid-arguments` | 畸形参数在 Host 写入前被拒绝 |
| `endless` | 六轮工具循环上限生效，未完成调用不冒充成功 |
| `approval-restart` | 保存同一审批/会话后终止 Host，重启仍等待原审批 |
| `fault-before-effect` | 意图已入账而尚未写入时终止，先 UnknownOutcome，专用目标核对后重试 |
| `fault-after-effect` | 文件已发布但账本未登记时终止，恢复补记并复用，不重写 |
| `receipt-partial` | 临时文件半写时终止；隔离半文件，最终只有一个完整回执 |
| `receipt-flushed` | 临时文件完整刷盘后终止；核验后发布，工具不再次执行 |
| `receipt-corrupt-json` | 坏 JSON 最终回执隔离，重复核对不放行；另模拟隔离已入账但文件尚未搬走的重启状态 |
| `receipt-conflicting-final` | 有效 JSON 但正文冲突也隔离，不能覆盖或重放 |
| `after-ledger-host-restart` | 工具成功已入账后终止 Host，重启只继续收尾 |
| `before-final-commit-resume` | Worker 已返回，最终 Run 提交前超时；显式 resume 成功且仅复用结果 |
| `after-ledger-worker-death-timeout-restart` | 工具已成功后结束 Worker，超时进入待收尾，重启复用同一结果 |
| `cancel-waiting-approval` | 待审批取消及时返回，零副作用，重复取消/审批不放行 |
| `cancel-persisted-restart` | 取消意图入账后、实际停止前终止 Host；重启不重新调度 |
| `cancel-before-effect-reconcile` | 在途取消先待核对，确认目标未发布后结束取消，不重试 |
| `cancel-after-effect-reconcile` | 在途取消保留已发生效果，核对后结束取消，不再发起写入 |
| `executing-ledger-write-failure` | SQLite 触发器注入写前事务失败，事务回滚且零副作用 |
| `corrupt-session-checkpoint` | 损坏会话 JSON 后恢复失败，保留错误且不执行工具 |
| `duplicate-host-state-lock` | 同一工作区第二个 Host 拒绝启动，原 Host 继续可用 |
| `finalization-corrupt-receipt` | 工具成功后破坏回执，再续接会隔离并保留 UnknownOutcome，不重复执行或假成功 |
| `workspace-lock-disabled-rejected` | Unix 文件锁被关闭时拒绝启动，业务数据库和副作用均未创建 |

审批/Operation/回执 ID、session hash、ToolExecuting/ToolResultReused 次数都取自数据库与文件。隔离搬移窗口采用合成的持久状态测试，报告明确标识；不声称在真实进程中精确命中了那条搬移指令。数据库失败采用 `RAISE(ABORT, ...)`，不等价于真实磁盘满测试。

## 4. 与整体验收计划的对应

| 计划项 | 本轮证据 | 尚不能外推的部分 |
| --- | --- | --- |
| `BASE-AOT-01` | 指定 RID 原生发布与 Host/HTTP/SSE/SQLite 集成 | 其他 RID 和生产部署 |
| `BASE-MAF-01` | 固定 MAF 版本、确定性模型的工具循环/审批/会话恢复/取消 | 真实供应商流式、费用和跨版本会话 |
| `APP-01`, `CRASH-04` | 待审批跨重启、浏览器恢复同一对象 | 完整权限、有效期、任务 revision 合同 |
| `APP-02–04`, `CRASH-05` | hash 不匹配和重复决定拒绝、参数严格检查 | 批准提交到派发之间的专门终止窗口、权限撤销与内容版本体系 |
| `CRASH-06–08`, `SIDE-FILE-01` | 专用文件目标的真实进程终止、回执对账、半写/冲突隔离、收尾恢复 | 通用 Artifact 发布、任意文件编辑、断电和目录元数据持久性 |
| `CRASH-11` | 运行器取消意图、在途结果核对、重启不重排 | 暂停、父子取消传播、完整业务任务状态 |
| `CRASH-12` | 损坏 session JSON 失败关闭 | 跨版本 checkpoint、授权撤回、记忆遗忘及接管入口 |
| `BASE-DB-01`, `OPS-02` | WAL/FTS5 运行、写前事务失败、SSE 重连、单 Host 锁 | 完整备份恢复、索引重建、磁盘满、网络目标故障 |

`CRASH-01–03` 的队列提交前后、运行中领取与旧租约 fencing、真实模型流中断及预算连续记账仍未完整实现/验证。完整 Task/Attempt/lease 模型属于后续业务接入，不能把单 Host 文件锁当作其替代。上述产品合同保留“未验证”，只将本表对应的实验子集计为通过。

## 5. 已保留的失败过程

- 首轮 macOS 为 **11/14**：[原始结果](results/macos-initial/summary.json)。两项失败来自测试驱动对已退出进程组再次发信号触发 EPERM；另一项是恢复已经删除 `fault-ready`，脚本此时才读取它。修正为仅清理本次拥有的 Host/Worker PID、检查归属并保留清理结果，故障标记在屏障处提前抓取。未放宽产品断言；扩充后的 22 项先完整通过；增加启动锁防线后，最终 23 项再次完整通过。
- Linux 首次锁定恢复遇到 `NU1004`：原 Host lock graph 只有 osx-arm64。容器内生成目标 RID 的 graph 后再次锁定 restore/publish，本机锁文件保持原样；[锁文件与目标差异](results/linux-arm64-volume-final/lockfiles/target-diff.txt)及 SHA-256 副本均保留。
- 第一次发布命令把 `--locked-mode` 用在不支持它的 publish 入口；保留失败日志，改用 `-p:RestoreLockedMode=true` 后发布成功。
- 浏览器验证切换最终 Host 时，旧 Host 正在等待 SSE 连接完成优雅关闭，立即启动的新 Host 被工作区锁拒绝；旧进程完全退出后再启动成功。未删除或绕过锁；[拒绝日志](results/browser/host-3.log)保留。

## 6. Docker 工作区锁的实测限制与修复

Linux arm64 首轮不是纯脚本问题：同一 Docker Desktop 宿主 bind mount 上，第二个 Host 也返回健康。进一步用两个独立进程验证，原生 `flock` 和 SQLite `BEGIN IMMEDIATE` 同样同时成功；同一镜像的容器本地目录则正确拒绝第二个 Host / flock 持有者。见 [三组诊断](results/linux-arm64-container/diagnostics/20261014T105848Z-lock-probes/summary.json)。这仅描述本机实际挂载，不概括所有 Docker bind mount。

`.NET 10` 的 Unix `FileShare.None` 使用尽力而为的 advisory lock，部分锁错误可被忽略，不能单靠打开文件成功来证明已拿到互斥锁；见 [.NET 固定版本源码](https://raw.githubusercontent.com/dotnet/runtime/v10.0.11/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)。本次开关 `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` 未设置；原生 flock 与 SQLite 对照结果表明问题不只在 .NET 开关。

Host 现在持有锁后启动一个只执行争用探测的独立自身进程。只有得到预期的锁争用错误才继续打开业务数据库；若第二个探测进程也能拿锁，启动报 `workspace_lock_not_enforced` 并停止。故障或不支持的文件系统不会被静默当作可安全运行。另以关闭 .NET 文件锁的进程环境测试这一拒绝路径；修复后的 Host 在原问题共享挂载上也已实际拒绝启动且未创建 `app.db`，见 [拒绝结果](results/linux-arm64-container/diagnostics/lock-guard-bind-rejected/assertion.json)。

本次 Dockerfile 是包含 SDK 和工具链的验证镜像，尚非生产发行镜像。容器支持路径改为 **Docker 本地持久卷**，测试结束导出完整证据，然后仅清理本次创建的容器和卷。原共享挂载的失败与修复后的拒绝证据分别保留；不会把在另一种存储介质上通过解释成共享挂载已支持。不能把“锁文件放在容器内、业务数据仍共享挂载”视为修复，因为这种方式不能保证共享工作区只有一个写者。

## 7. 复跑与结果边界

从仓库根目录进入 `experiments/p0/` 后构建，使用锁定 SDK/依赖：

```sh
cd experiments/p0
npm --prefix Web ci
npm --prefix Web run build
dotnet publish P0.Host/P0.Host.csproj -c Release -r osx-arm64 --self-contained true -p:RestoreLockedMode=true -p:PublishAot=true -o .artifacts/osx-arm64/host
dotnet publish P0.Worker/P0.Worker.csproj -c Release -p:RestoreLockedMode=true -p:UseAppHost=false -o .artifacts/osx-arm64/worker
python3 P0.Worker/smoke.py --worker "$PWD/.artifacts/osx-arm64/worker/P0.Worker.dll"
python3 scripts/validate.py --host "$PWD/.artifacts/osx-arm64/host/P0.Host" --worker "$PWD/.artifacts/osx-arm64/worker/P0.Worker.dll" --output "$PWD/../../docs/validation/2026-10-14/p0/results/macos-rerun"
```

Linux 容器从同一目录复跑；使用新的空结果目录，脚本自动创建专用本地卷、导出证据并清理本次资源：

```sh
bash scripts/validate-linux-container.sh "$PWD/../../docs/validation/2026-10-14/p0/results/linux-arm64-rerun" --platform linux/arm64 --rid linux-arm64
bash scripts/validate-linux-container.sh "$PWD/../../docs/validation/2026-10-14/p0/results/linux-x64-rerun" --platform linux/amd64 --rid linux-x64
```

故障入口只在 `P0_ENABLE_FAULTS=1` 时接受固定注入点；测试可将 Worker 超时设为 3 秒，正常默认 40 秒。不要对真实工作区运行这些注入。对账只重试专用文件目标，不能移植为一般外部动作的自动重发策略。

仍未验证：真实模型及用量、Windows、原生 Linux x64 硬件及其余目标平台、真实 HTTP/SMTP/shell 外部副作用、断电、真实磁盘满、跨版本 checkpoint、备份恢复、多进程执行租约、200% 缩放/窄屏/完整键盘验收。计数只包括成功返回给 Host 的 Worker 统计，中断尝试未完整计费；不能声称费用连续或所有工具物理执行次数都等于该界面计数。

当前实验提供的恢复动作是审批、专用文件核对、收尾续接与取消。损坏最终回执保留人工接管状态；完整修复/接管工作台尚未开发。没有据此宣称一般意义的恰好一次执行或生产发布就绪。
