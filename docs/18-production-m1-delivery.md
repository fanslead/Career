# 首个生产闭环交付与验证

2026-10-14–15 · macOS arm64 · 内部可用批次。依据 U17/U18，先完成正式工程及本地验证，真实模型配置与内容质量验证延期。本次完成单所有者、单执行槽的文本任务 → 计划确认 → MAF 执行 → 固定正文审批 → Markdown 文件发布 → 固定版本验收。首次正式工作区为空，不装载示例任务或测试模型。

这份记录描述已运行的范围；14/15 的完整工作包和 M5 发布门槛没有整体放行。2026-10-15 已完成提交前复审、修复与分批本地提交，没有推送；复审后的结果与提交范围见文末。

## 可运行交付

正式工程为 `Career.slnx`、`src/`、`apps/web/`、`tests/`、`packaging/`，P0 实验保持独立。macOS arm64 的发布目录为 `artifacts/release/osx-arm64/app/`，双击 `Career.command` 或运行 `Career.Host`。Host 为 Native AOT，同目录 `worker/Career.AgentWorker` 自带 .NET 运行时；测试在 `PATH=/usr/bin:/bin`、不存在的 `DOTNET_ROOT` 下完成，没有调用安装的 dotnet。

启动窗口给出本机地址和一次性配对码。模型密钥只进入本机加密凭据库，浏览器不回显。设置支持 OpenAI 兼容 Chat Completions；保存参数后需要单独测试文本与原生工具审批检查点，参数改变使旧验证失效。模型调用次数、输出上限和输入长度有界；用量没有确认或价格未配置时不会显示为零费用。

本次保留的交互预览在 [本机验证工作区](http://127.0.0.1:5199/)，包含两项已验收的工程验证任务；它使用单独的本地测试连接。正式启动脚本的默认端口为 5198、工作区为空，二者分开。

HTTP 合同见 [17](17-production-m1-contract.md)与 [OpenAPI](openapi-m1.yaml)，stdio 合同见 [Worker schema](worker-protocol-m1.schema.json)。主导航保留工作台、项目、团队、资料、设置；本批工作台提供总览、任务、审批、成果。其他模块显示当前阶段范围。

## 已完成的行为

| 边界 | 当前实现与本地证据 |
| --- | --- |
| 任务与计划 | 无模型也能保存草稿；固定输入、验收条目与修订；确认计划后才派发。暂停或失败后可编辑为新修订，旧未决许可失效 |
| 执行身份 | Host 是唯一数据库写者；Worker 通过版本化 stdio 提出请求。每次调用核对 Run、Attempt、epoch、fencing；旧写者未确认停止时暂停新执行 |
| 审批 | 文件名、目标、正文、载荷校验值与有效期固定；未批准不创建成果。拒绝后的改稿使用新提案；已成功操作不能重置成新待审批 |
| 成果与验收 | 写前保存意图，暂存校验后发布不可变文件及 manifest；固定版本下载校验 SHA-256。Run 成功进入待验收，Task Done 必须有当前修订/文件版本的独立验收；退回建立新修订与 Run，旧成果和决定保留 |
| 预算与恢复 | 模型调用先预留、后结算；跨 Attempt 保留额度。等待审批时重启保留原操作；普通运行重启后暂停并由用户继续；取消/暂停确认执行器停止后才进入稳定状态 |
| 会话与请求 | 访问令牌只在内存；刷新凭据轮换、退出可撤销。API、SSE、下载统一鉴权及 Host/Origin 检查。requestId 幂等，同键不同内容拒绝，旧任务版本返回冲突快照 |
| 界面 | 离线/补齐时禁写并保留备注，成功写后重读权威状态；冲突不重发、保留输入。导航保留创建与连接的非敏感草稿；刷新后的未决请求只恢复非敏感元数据，同操作不能换 ID 重提 |
| 离线备份 | 独占锁与旧执行者检查后备份独立 SQLite 文件、权威配置、成果和加密 vault；归档逐文件 hash。恢复只写空目标，损坏包不覆盖；首次启动撤销旧会话、暂停派发并保留恢复审计标记 |

## 实测结果与原始记录

下表保留首次交付快照（27 项核心、5 项前端测试）；提交前复审已扩展到 35 项核心、12 项前端测试及新的故障场景，以文末最新结果为准。必要脱敏证据已随本轮文档保存到 [M1 证据目录](validation/2026-10-15/m1/)，本机 `artifacts/validation/m1/` 仍保留完整工作记录。

所有模型请求使用 `tests/fixtures/model-server.py` 的本机协议服务，不调用真实供应商。测试走真实 HTTP、模型 SDK、MAF 审批/会话续接、受监管 Worker 与文件发布代码；固定回复仅在测试服务中。

| 检查 | 结果 | 原始证据 |
| --- | --- | --- |
| Release 工程构建 | 0 warning / 0 error | 正式 SDK 构建；发布日志另列于下方 |
| 状态/存储/成果测试 | 27/27，无跳过 | `artifacts/validation/m1/test-results/m1-core.trx` |
| Web 状态与未决请求测试 | 5/5，生产前端构建通过 | `apps/web/tests/`；不以这些单元测试替代浏览器实测 |
| SDK 完整集成 | 8/8 断言 | `artifacts/validation/m1/integration-sdk.json` |
| Native 完整集成 | 8/8 断言；没有系统 dotnet | `artifacts/validation/m1/integration-native.json` |
| SDK / Native 备份恢复 | 9 项检查均通过 | `artifacts/validation/m1/backup-sdk.json`、`backup-native.json` |
| SDK → Native → SDK 凭据切换 | 26ms 返回拒绝，未调用模型，原 vault hash 不变；原程序可恢复读取 | `artifacts/validation/m1/keychain-noninteractive.json` |
| Native 发布 | Host AOT、Worker 自包含、静态资源与启动脚本 | `artifacts/validation/m1/publish.log`；两条 clang module-cache 警告影响调试信息，发布及实际执行成功 |
| 启动脚本与空工作区 | 无系统 dotnet 可启动，0 个任务、无模型配置、无所有者，正常退出 0 | `artifacts/validation/m1/launcher.json` |

8 项集成断言覆盖必填字段 null → 400、完整生命周期与已确认用量、成果篡改拒绝、会话/来源/刷新、两次模型调用上限、待批重启、调用中的暂停和取消。主流程包括同请求幂等/不同载荷冲突、拒绝 → 新提案 → 批准 → 下载 hash → 验收 Done；一次发布，没有重复效果。该流程的账项为 3 次模型调用、771 输入 token、66 输出 token，未配置价格故费用未知。

钥匙串调用使用本机 SDK 的 Boolean/OSStatus 签名，在应用进程内串行关闭交互并恢复原状态。未获许可、原密钥丢失或 vault 校验失败时保留原库并返回恢复提示，不能新建密钥覆盖。文件钥匙串需要旧 Get/SetUserInteractionAllowed 接口的理由见 [Chromium 官方实现](https://chromium.googlesource.com/chromium/src/crypto/+/refs/heads/main/apple/scoped_keychain_user_interaction_allowed.cc)；现代 SecItem 查询选项说明见 [Apple 文档](https://developer.apple.com/documentation/security/ksecuseauthenticationuifail)。本批不改变已有钥匙串 ACL；签名、升级后的身份稳定与交互式凭据恢复属于后续发行门槛。

## 实际界面证据

| 场景 | 观察与截图 |
| --- | --- |
| 创建草稿/页面切换 | 320px 下填写的四项任务输入在项目 → 工作台后保留；页面无横向溢出。`screenshots/mobile-workbench-320.jpg` |
| 320px 审批 | 对话框宽 282px，高 695.6px；底部两按钮都在 740px 视口内，按钮高度 44.8px。未打开的对话框不可见。`screenshots/mobile-approval-320.jpg` |
| 断连与恢复 | 开着审批对话框停止 Host，确认按钮禁用，备注保留；重连后备注不变并恢复可用。文件/验收/命令记录没有因离线增加。`screenshots/mobile-offline-note-320.jpg` |
| 多窗口版本冲突 | 系统浏览器处理后，用另一窗口保留的旧版本提交得到 409，备注仍在，未重复发布。`screenshots/mobile-conflict-note-320.jpg` |
| 实际 200% 缩放 | Google Chrome 明确显示“缩放比例：200%”，固定审批正文可滚动，底部返回/确认可见，Tab 到确认后 Enter 提交。测试后重置缩放并关闭测试标签。`screenshots/browser-zoom-200.png` |
| 390px 验收与焦点 | 页面宽度 390px；确认/返回 44.8px 且在视口内。Escape 返回触发按钮；键盘提交验收后，消失的按钮由稳定 main 焦点接管。`screenshots/mobile-acceptance-390.jpg`、`mobile-done-390.jpg` |
| 实际下载 | 界面下载 `mobile-review.md`，375 bytes，SHA-256 与已发布版本完全一致 |

截图目录为 `artifacts/validation/m1/screenshots/`。界面验证工作区与正式默认工作区分开，样例内容的验收备注明确限定工程验证，不表示真实业务结论通过。

首次交付时的界面检查摘要为 `artifacts/validation/m1/ux-results.json`，成果总览为 `screenshots/closed-loop-artifacts.jpg`。当时的源码/发布文件指纹和保留核对见 `artifacts/validation/m1/final-verification.json`：79 个正式源文件、252 个发布文件；33 个 P0 源文件及用户原有 `.gitignore`、`.DS_Store`、`.vscode/settings.json` 均与批次前指纹一致。提交前复审后的新指纹另见文末清单，不覆盖这一历史快照。

## 尚未放行的范围

- 真实模型账号、供应商流式/工具兼容、长上下文和内容质量；按用户选择延期，后续用真实资料与验收条目复跑。
- Windows/原生 Linux 的正式生产工程和系统密钥后端实测；不引用 P0 的其他平台结果代替本批验证。
- 对话分支、持久多人协作、终端/仓库修改、网页与浏览器执行、知识/记忆、外发、定时与集成。
- 活跃 Host 在线备份、完整升级/回滚、异机系统密钥恢复、签名安装器、72h 负载、真实断电/磁盘满、全部路径替换竞争与完整无障碍测试。

下一批先配置一条真实连接，复跑任务质量与修改意见的闭环，再按 15 推进工具/协作及知识；安装发行与完整平台验收保持独立门槛。`docs/`、`artifacts/` 的用户既有忽略规则未改；本轮源码、方案、原型与必要证据按明确文件清单纳入本地 Git，运行数据库、密钥和发布二进制保留在本机。

## 提交前复审（2026-10-15）

本轮按单所有者、本机文件系统、单执行槽的实际负载审查整个新增工程及关联调用。重点是任务与执行状态、固定审批、发布回执、停止与重启、未决请求和备注恢复；没有扩展新业务模块。下列发现均已修复，最终独立复核没有剩余的阻塞发现。

| 发现与触发方式 | 修复及意义 |
| --- | --- |
| 连续停止或停止待核对执行，过早进入 Cancelled | Running、Stopping、Reconciling 都等执行器确认；不再用第二个请求跳过退出确认 |
| 停止待验收任务重写了已成功的 Run | 任务可取消，已成功或失败的执行终态、成果和结束时间保留 |
| 失败回包或旧暂停确认覆盖并发取消 | `Fail`、`StopAcknowledged` 在同一更新事务内读取最新停止意图，取消优先；退出未确认仍保持 Blocked/Reconciling |
| 保存新连接后，旧排队任务无限占住队头 | 不兼容的任务转暂停并提示检查连接，后续兼容任务可领取；用户继续时使用新 Run |
| 成果已成功发布，后续供应商失败、额度耗尽或终态丢失导致无法验收 | Worker 依据成功回执返回精简成功；Host 对账原 Operation 和文件后进入 InReview，不重复发布，仍须独立验收 |
| 应用退出期间把 Worker 的中断写成任务失败 | 执行停止令牌合并 ApplicationStopping，关闭期间保留未完成状态供重启对账为暂停 |
| 无法确认旧 Worker 停止后仍可能派发下一任务 | 持久冻结新派发；进程启动后的登记也放入退出保护，登记失败仍停止并等待子进程 |
| 刷新丢失正文后，404 回执永久阻塞交办 | 用户显式重填并使用原请求标识、路由和方法；不持久化正文或密钥，不换新标识绕过未决结果 |
| 版本冲突后仍绑定旧版本，重新打开会丢备注 | 比较固定内容后明确选择当前版本；只有同一未处理许可或同一成果版本才可继续，保留备注 |
| 非选中任务详情失败或缓存旧版本，收件箱误显为空或旧状态 | 并发上限 4，加载/失败状态与空结果分开；补齐缺失和落后版本，支持手动与重连重读 |
| 长时间离线不断积累等待回调 | 正常超时结束时移除 abort listener，停止同步时清理计时器 |
| 测试忽略临时 Keychain 删除失败 | 共享清理器核对删除结果和“条目不存在”的后置条件；清理失败不能报告通过，保留主要错误 |
| 多处文档仍声称生产尚未实施 | 更新当前状态，并保留原型、首次交付与后续生产实测的证据边界 |

若保留上述停止竞态，会丢掉用户最后一次取消决定；若保留成果收尾问题，已有固定成果可能永远无法走正常验收。这些修复均在本次提交前完成；两轮失败基线及更正后的记录一并保留。额度场景的新预期为“2 次模型调用、1 个已批准成果、InReview、尚未验收”，供应商请求数仍不得增加到第三次。

| 最新检查 | 结果与证据 |
| --- | --- |
| SDK Release 构建 | 0 warning / 0 error；[构建日志](validation/2026-10-15/m1/review-build.log) |
| 核心回归 | 35/35、0 跳过；[TRX](validation/2026-10-15/m1/review-test-results/m1-core-review.trx)，包括最新停止意图、阻塞状态、队列与跨路由请求标识 |
| 前端状态回归与构建 | 12/12；[测试日志](validation/2026-10-15/m1/review-web-tests.log)、[构建日志](validation/2026-10-15/m1/review-web-build.log) |
| SDK / Native 完整流程 | 各 10/10；[SDK](validation/2026-10-15/m1/review-integration-sdk.json)、[Native](validation/2026-10-15/m1/review-integration-native.json)，新增普通运行重启/人工继续及退出撤销 |
| 发布后供应商失败且丢失最终响应 | SDK / Native 各 5/5；[SDK](validation/2026-10-15/m1/review-finalization-sdk.json)、[Native](validation/2026-10-15/m1/review-finalization-native.json)，终态丢失标记、原文件 hash、单版本及独立验收均核对 |
| 最小备份恢复 | SDK / Native 各 9 项通过；[SDK](validation/2026-10-15/m1/review-backup-sdk.json)、[Native](validation/2026-10-15/m1/review-backup-native.json)。测试工作区不含凭据，vault 归档分支仅完成代码核对；系统凭据恢复另验。该轮在最后的停止竞态修复前运行，后者不改变归档/恢复实现 |
| 原生包与清理 | 无系统 dotnet 的受限 PATH 中运行；测试前后发布树不变，临时 Keychain 条目不存在；AOT 发布仍有两条已知 clang 调试信息警告，运行验证通过 |
| 实际浏览器 | 打开许可后，API 暂停/继续使版本 v6→v8；确认按钮禁用。明确比较后备注不变，随后审批、发布并独立验收为 Done。[版本比较截图](validation/2026-10-15/m1/screenshots/review-decision-comparison.jpg)、[备注保留](validation/2026-10-15/m1/screenshots/review-note-preserved.jpg)、[已验收成果](validation/2026-10-15/m1/screenshots/review-accepted-artifact.jpg) |

本轮没有重新跑全部窄屏/200%/离线截图矩阵；此前交付证据保留。新增的刷新恢复、详情补齐与等待清理由状态测试和代码审查验证，不冒充新的完整浏览器故障矩阵。真实供应商、其他原生平台、无法终止进程的实际 OS 故障注入与前述完整发布门槛仍未放行。

本地提交分为四批：`6a07645` 工程与核心回归，`112deba` 前端与恢复交互，`b450f59` 打包与集成验证，以及包含本文的方案/原型/证据提交。每批使用明确文件清单并通过暂存差异检查；没有推送。既有 `.gitignore`、`.DS_Store`、IDE 设置和 33 个 P0 源文件保持原指纹。最终范围与文件指纹见 [复审核对清单](validation/2026-10-15/m1/review-verification.json)。
