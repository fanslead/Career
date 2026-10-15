# Agent 工作台设计文档

更新：2026-10-15。状态：P0 已完成复审与本地提交；[生产方案](14-production-implementation-plan.md)、[工作包](15-production-work-packages.md)及[原型体验修订](16-prototype-and-ux-review.md)之后，现已建立正式工程，完成首个单所有者的本地任务 → 执行 → 审批 → 固定成果验收闭环。当前可运行行为、验证与剩余门槛见 [18 本批交付](18-production-m1-delivery.md)，实现合同见 [17](17-production-m1-contract.md)。

本轮从用户的新需求开始，不继承此项目此前的代码、规格、架构结论或验收结果。完整设计合同、默认值与验收目标不等于已实现能力；实际通过范围单列在 18，用户确认范围记录在 [09 决策记录](09-decisions-and-open-questions.md)。

## 产品主线

面向独立开发者和一人公司的本地 AI 工作台：用户先交办任务，系统按需组织岗位成员与临时助手，交付可检查的成果，保留可接管的过程和可复用的经验。办公、研发、经营三种模式共用同一内核。商业价值与付费意愿仍需真实用户验证。

这次将四条主线梳理为可评审的合同：

| 主线 | 本次确定的设计表达 | 主要文档 |
| --- | --- | --- |
| 岗位协作 | 长期岗位、稳定成员、临时执行身份分开；任务有一个责任人，执行另有租约；委派、等待、改派与取消可追踪 | [04 协作](04-agent-harness-and-projects.md) |
| 任务流程 | 交办 → 补齐输入 → 执行/分工 → 候选成果 → 固定版本验收；Run 成功不直接代表 Task 完成 | [04 流程](04-agent-harness-and-projects.md)、[07 界面](07-ux-and-reporting.md) |
| 记忆 | 工作上下文、经历、事实/经验、知识分开；候选发布、私有共享、版本、冷热、更正与遗忘有明确规则 | [06 记忆](06-memory-and-knowledge.md) |
| 重启恢复 | 区分历史恢复、界面重连、运行续接、副作用对账；先保存动作意图，未知结果先核对 | [13 恢复](13-restart-and-recovery.md) |

## 已由用户确认的技术和范围

- `.NET 10 + SQLite + MAF + React`；MAF 按 Microsoft Agent Framework 理解，配合 Microsoft.Extensions.AI 的具体适配仍需验证。
- Core Host 优先 Native AOT；MAF、供应商 SDK、解析器和扩展可放在普通 .NET、Node 或 Python 独立进程。
- 首版 React + 独立 .NET 服务，Windows/macOS 启动后用浏览器；Docker 使用同一界面，Electron 后加。
- 配置与知识以本地文件为权威；任务、对话与执行记录允许 SQLite 为主存。每类对象只有一个权威位置。
- 首次或高风险对外动作确认，已授权固定流程自动执行；第一版覆盖文件、网页、终端与代码工具，桌面点击后续加入。
- P0 复审与本地提交已完成；正式工程按完善后的方案开始分阶段实现，先完成本地闭环，真实模型后续配置，其他业务模块尚未交付。

本地数据由工作区管理，不意味着云模型不会接收经授权的任务上下文。“仅本地处理”需作为明确策略实施和验证。

## 阅读顺序

查看当前运行结果先读 18 与 17；完整方案和后续顺序见 14、15；原型目标见 16 与图册；对象与恢复合同重点看 04、06、13。

| 文档 | 主要回答 |
| --- | --- |
| [01 产品定位与范围](01-product-and-scope.md) | 三模式如何统一、需求覆盖与边界 |
| [02 系统架构与 AOT](02-architecture.md) | 产品/执行/Worker 分层、MAF 与 AOT 边界 |
| [03 存储与工作区](03-storage-and-workspace.md) | 文件与数据库权威、数据对象、共享、备份 |
| [04 岗位协作与任务流程](04-agent-harness-and-projects.md) | 责任、临时委派、持久等待、状态机、改派与验收 |
| [05 模型、技能、MCP 与插件](05-models-tools-plugins.md) | 多协议接入、能力检测、按成员分配、兼容范围 |
| [06 记忆与知识库](06-memory-and-knowledge.md) | 候选发布、范围、版本、冷热、撤回、RAG 与索引迁移 |
| [07 界面、监控与邮件](07-ux-and-reporting.md) | 任务优先入口、待处理动作、真实状态与汇报 |
| [08 交付路线与验收](08-delivery-and-validation.md) | 分阶段门槛、故障注入、真实副作用与记忆验收 |
| [09 决策与待验证事项](09-decisions-and-open-questions.md) | 用户确认、设计默认、未决项与当前实施边界 |
| [10 一手资料索引](10-sources.md) | 来源和证据口径 |
| [11 前端与 Runtime 通信](11-frontend-runtime-protocol.md) | 任务 API、HTTP/SSE、内部协议、事件补齐与版本冲突 |
| [12 React 与 Electron](12-react-and-electron.md) | 首版 Web 与后续桌面壳的关系 |
| [13 审批与重启恢复](13-restart-and-recovery.md) | Operation 账本、启动次序、未知结果、文件发布与接管 |
| [14 生产级实现方案](14-production-implementation-plan.md) | 正式工程、生产边界、六个实施批次、支持矩阵与放行门槛 |
| [15 生产实施任务清单](15-production-work-packages.md) | 21 个工作包、模块责任、依赖、完成标准与起步顺序 |
| [16 前端原型与体验审查](16-prototype-and-ux-review.md) | 页面与状态覆盖、原型走查修正、六条用户旅程与真实验证边界 |
| [17 首批生产合同](17-production-m1-contract.md) | 当前实现的 HTTP、状态、Worker、幂等、审批与恢复规则 |
| [18 首个闭环交付](18-production-m1-delivery.md) | 可运行产物、复审后 35+12 测试、SDK/Native 集成、实际 UX、备份和未放行范围 |

## 研究入口

[九产品比较与设计修正](research/product-comparison.md)汇总七个开源仓库的固定提交及两款产品的官方文档。研究区分源码确认、官方声明、设计推论、未验证，不以源码存在代替实测结果。

新增 [DeepSeek Harness 深度研究](research/products/deepseek-harness.md)，配套 [Web/Host 调用链](research/deepseek-web-runtime.md)与[运行、协作和恢复机制](research/deepseek-runtime-recovery.md)。它可独立以浏览器运行，另有 Electron 壳；实验 Teams、外部 MCP memory 与未知工具结果处理均记录了具体边界。

其他专项：[NextCoWork 通信](research/nextcowork-runtime-communication.md)、[MAF 与 AOT](research/dotnet-maf-aot-evidence.md)。最初概览 [Agent/Harness](research/agent-harness-references.md)和[组织与知识](research/business-knowledge-references.md)保留用于追溯，详细结论以固定提交研究为准。

## 当前验证与版本管理状态

两项原始恢复问题已修复；复审进一步修复 Host 请求来源检查、界面迟到读取和离线动作、窄屏顺序及测试驱动边界。macOS arm64、Linux arm64 Docker 本地卷、Linux x64 Docker 本地卷的复审后完整套件各 23/23 通过，另有 16 项针对性测试通过；x64 为 arm64 宿主上的 amd64 仿真。以 [P0 复审交付](validation/2026-10-14/p0/review-and-delivery.md)为当前结论，[原始实测记录](validation/2026-10-14/p0/README.md)保留恢复修复与共享挂载限制，[08 验收计划](08-delivery-and-validation.md)中的完整业务与平台门槛仍按证据逐项保留。

P0 源码、设计/研究文档和必要证据已提交本地 Git `642b2e8`；后续规划、原型、正式工程与复审证据按本轮授权分批本地提交，见 18，没有推送。P0 业务代码未改动。`docs/` 和 `artifacts/` 的既有忽略规则保持原样；新增文档按明确清单纳入，原始数据库、密钥和构建产物保留本机。
