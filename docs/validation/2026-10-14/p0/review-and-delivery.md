# P0 代码、文档复审与本地交付

更新：2026-10-14。本记录对应用户授权的 P0 复审、修复、验证和本地 Git 提交；没有展开完整业务开发，也不包含远端推送。此前恢复修复轮的结果保留在 [原始记录](README.md)，下面列出复审后的最终源码与验证结果。

**结论：复审发现的问题已修复；macOS arm64、Linux arm64 Docker 本地卷、Linux x64 Docker 本地卷均为 23/23 通过，另有 16 项针对性回归测试和 4 组 Worker smoke 通过。** Linux x64 使用 arm64 宿主上的 amd64 仿真。MAF 是实际框架，模型仍为确定性替身，副作用限隔离目录中的专用文件回执。

## 1. 发现与修复

| 问题 | 修复及验证 |
| --- | --- |
| Host 仅比较 Origin 与请求自带的 Host，来自回环地址、但使用任意域名的匹配请求仍可创建 Run | 修复前已复现预期 403、实际 202，见 [原始复现](results/review-before-fixes/host-header-reproduction.json)。现在先要求 Host 为 localhost 或回环 IP，再执行 Origin/自定义请求头检查。完整套件验证非本地域名的 GET/POST 均为 403、没有新增 Run，合法 localhost/IPv4/IPv6 及域名大小写继续可用。这是 P0 本机访问边界，不是生产身份认证 |
| 快速切换 Run、刷新列表或旧 SSE 回调可能让迟到数据覆盖当前详情；读取失败时旧审批仍可能可操作 | 所有详情读取以选择 ID 和读取代次校验；列表刷新使旧详情读取失效；SSE 校验流生命周期和 Run ID。失败时显示缓存状态提示并禁用写操作，权威读取恢复后才重新开放。4 项确定性测试覆盖响应倒序及旧错误；浏览器验证切换、断线、恢复和动作对象 |
| 窄屏视觉重排与 DOM/键盘顺序不一致 | 保持“创建 → 记录列表 → 详情”的一致顺序；浏览器实测 390px 下从创建按钮经过记录按钮到详情取消按钮。该检查不是完整无障碍验收 |
| Worker smoke 可能永久等待 stdout，异常返回时可能遗留进程 | 每次 Worker 调用设置总截止时间，并在 finally 中回收本次进程组；2 项测试分别覆盖不响应和无效 JSON 后继续挂起。实际发布 DLL 的 4 组 smoke 全部通过 |
| 验证驱动可能把空字符串 checkpoint 当缺失、空 SSE 重连当通过，或只凭表面状态判定拒绝成功 | 空字符串明确记为“存在但 JSON 无效”；SSE 必须读到游标之后的指定事件；拒绝路径同时核对新的 Worker/model 调用、持久终态、事件、零账本和零回执 |
| 清理和结果导出可能在检查失败时错误宣告完成 | 清理只有确认成功后才记录完成，检查失败可重试；Docker 检查失败与资源确实不存在分开，未知错误保留本次卷并失败退出；汇总必须匹配非空用例注册表、数量和严格布尔结果。10 项驱动测试覆盖这些边界，包括 Docker context 缺失 |
| 锁探测参数缺失或空白时可能落入正常 Host 启动 | 在应用初始化前返回 64，不创建业务数据库；见 [发布后二进制检查](results/review-host-probe-arguments.json) |

恢复账本、半写回执隔离与收尾复用继续按原有 P0 合同运行，完整故障套件确认本轮修复未破坏这些路径。文档同步区分历史采集快照、复审后的当前结果和仍未完成的产品验收合同。

## 2. 最终验证与可复核证据

[最终核对索引](results/review-verification-index.json)记录三种配置的结果摘要、指纹、进程清理与受保护文件核对；[源码和 macOS 二进制指纹](results/review-source-and-macos-binaries.json)覆盖本轮全部 33 个 P0 源码/构建输入文件。

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| React 读取顺序回归与生产构建 | 4/4；构建通过 | [测试](logs/review-pass/web-tests.log)、[构建](logs/review-pass/web-build.log) |
| Worker 测试驱动的截止时间/清理 | 2/2 | [测试](logs/review-pass/worker-harness-tests.log) |
| 集成验证驱动与 Docker 清理边界 | 10/10 | [测试](logs/review-pass/validation-driver-tests.log) |
| macOS Native AOT Host、普通 .NET Worker 发布 | 通过 | [Host](logs/review-pass/host-publish.log)、[Worker](logs/review-pass/worker-publish.log) |
| 发布后 Worker smoke | 4 组通过 | [日志](logs/review-pass/worker-smoke.log) |
| macOS arm64 完整集成/故障套件 | **23/23** | [摘要](results/review-macos-final/summary.json)、[运行日志](logs/review-pass/macos-validation.log) |
| Linux arm64 Docker 本地持久卷 | **23/23** | [摘要](results/review-linux-arm64-final/validation/summary.json)、[构建](results/review-linux-arm64-final/docker-build.normalized.log)、[环境](results/review-linux-arm64-final/environment.txt)、[镜像元数据](results/review-linux-arm64-final/image-inspect.json)、[二进制指纹](results/review-linux-arm64-final/binary-fingerprints.txt)、[清理](results/review-linux-arm64-final/resource-cleanup.log) |
| Linux x64 Docker 本地持久卷，amd64 仿真 | **23/23** | [摘要](results/review-linux-x64-final/validation/summary.json)、[构建](results/review-linux-x64-final/docker-build.normalized.log)、[环境](results/review-linux-x64-final/environment.txt)、[镜像元数据](results/review-linux-x64-final/image-inspect.json)、[二进制指纹](results/review-linux-x64-final/binary-fingerprints.txt)、[清理](results/review-linux-x64-final/resource-cleanup.log) |
| 浏览器：选择一致性、断线禁用、恢复、批准/取消、窄屏 Tab 顺序 | 通过限定场景 | [结果及权威终态](results/review-browser/summary.json)、[离线 DOM](results/review-browser/offline-dom.json)、[窄屏顺序与几何](results/review-browser/narrow-order.json)、[进程记录](results/review-browser/host-processes.json) |

三种配置运行相同的 23 个场景，共 69 次用例执行，不是 69 个不同场景。所有摘要的用例名称和数量均与最终脚本注册表一致。测试拥有的 Host/Worker 已结束，Linux 两轮容器及专用卷已清理，完整原始状态已导出到本机。

浏览器中 A、B、C 均通过界面创建；A-B-A 切换后详情与事件一致。Host 停止时显示缓存状态且审批/拒绝/取消不可用；重启后读取恢复，B 和 C 取消后均为零回执，A 批准后为 Succeeded、一个有效回执和一次 ToolExecuting。界面工具调用统计包含审批/恢复调用，不等于物理写入次数。截图：[离线动作禁用](results/review-browser/offline-actions-disabled.png)、[恢复后成功](results/review-browser/recovered-selection-succeeded.png)、[390px 键盘顺序](results/review-browser/narrow-keyboard-order.png)。浏览器没有注入网络响应延迟；倒序响应由上述确定性测试覆盖。

Linux arm64 此轮首次下载 ILCompiler 包遇到 unexpected EOF，重试恢复和构建后完整通过，构建日志保留该过程。仿真 x64 不提供原生硬件性能、长期运行或生产部署结论。

## 3. 复跑

在仓库根目录执行针对性测试：

```sh
npm --prefix experiments/p0/Web ci
npm --prefix experiments/p0/Web test
npm --prefix experiments/p0/Web run build
python3 experiments/p0/P0.Worker/smoke_test.py
python3 experiments/p0/scripts/test_validate.py
```

发布及完整套件命令见 [原始记录的复跑部分](README.md#7-复跑与结果边界)。使用新的结果目录；本次最终 macOS 发布物位于 `experiments/p0/.artifacts/review/osx-arm64/`，最终结果目录均以 `review-` 开头。Linux 脚本自动创建本次专用卷，验证导出摘要后再清理。不要在真实业务工作区运行故障注入。

## 4. 本地提交范围与剩余门槛

本次本地 Git 提交纳入 P0 源码、依赖锁文件、测试脚本，以及设计/研究文档和文中直接引用的必要摘要、日志、截图。此前这些文件尚未受 Git 跟踪，因此提交包含本轮设计与实验基线；修复范围仍限 P0。设计文档中的目标能力不因此变为已实现。

仓库已有的根 `.gitignore` 修改、`.DS_Store`、`.vscode/settings.json` 保持原样并排除在提交外。`docs/` 的既有忽略规则保留，本次按文件清单显式纳入文档。数据库、WAL/SHM、完整逐用例状态、回执目录、构建产物与依赖安装目录保留本机，不进入 Git。摘要内绝对路径用于定位本机原始证据，不能当作跨机器仓库链接；提交中的摘要和截图只是必要证据摘录，完整复核可重新运行套件。

四份原始日志/指纹文本含终端换行符或行尾空格。Git 保存对应的 `.normalized` 文本副本，仅统一换行并移除行尾空白和多余末尾空行；原文件保持原样留在本机。[文本证据对照](results/evidence-text-normalization.json)记录原文件与提交副本的路径、SHA-256 和转换规则，报告中的链接指向提交副本。JSON 结果、源码指纹和截图均未作这类转换。

仍未完成：真实模型与用量连续记账、Windows、原生 Linux x64 硬件、真实 HTTP/SMTP/shell 外部效果、断电和真实磁盘满、跨版本 checkpoint、完整备份恢复、Task/Attempt/lease 与父子协作、完整无障碍及 200% 缩放验收。[08 的产品矩阵](../../../08-delivery-and-validation.md)仍按复合判据保留“未验证”。本轮没有据此宣称生产发布就绪。
