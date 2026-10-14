# Paperclip 产品与任务生命周期核查

核查日期：2026-10-14。固定上游提交：[`2c17d3ca52a7840f280f1dd87975a2ad77de3b35`](https://github.com/paperclipai/paperclip/commit/2c17d3ca52a7840f280f1dd87975a2ad77de3b35)。源码链接固定到该提交并标注行号。本次只读检查源码、仓库文档及测试文件；未安装、运行或登录 Paperclip。下文的“源码确认”表示路径存在于该提交，不表示本次运行验证。

## 产品定位与证据边界

Paperclip 将公司、人员/agent、任务、预算和审批建模成一个控制面。官方操作指南把 agent 描述成带组织汇报关系的角色，并让 board operator 配置 adapter、能力、预算与心跳。[官方 agent 配置说明](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/managing-agents.md#L19-L28)

源码把 issue 的层级、依赖、当前责任人和可继续执行的路径分开；issue 每次最多一个 agent 或一个 human 负责人。这是其协作模型的硬边界。[执行语义：责任人与 issue 状态](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L15-L41)

## 用户流程：创建任务、领取、执行、审阅和结束

1. **创建并分派：** board operator 或 manager 创建 issue，填写负责人、状态、优先级及可选父 issue/目标。官方 agent 工作流将子任务表示为独立 issue，并建议设置 `parentId` 维护层级。[任务创建与子任务示例](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L53-L69)
2. **唤醒和领取：** 被分派的 agent 通过 heartbeat/wake 收到可执行工作；checkout 将 issue 从可领取状态转为 agent 拥有的 `in_progress`。该领取是原子操作：并发竞争只允许一个 run 获得 checkout，另一方得到 `409`。`checkoutRunId` 记录 issue 执行权归属，`executionRunId` 记录实际活跃的 run，两个锁含义不同。[Checkout API 契约（官方指南）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L8-L22) [执行锁模型（源码文档）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L189-L213)
3. **工作中更新：** agent 可评论、更新 issue 状态并创建子任务；遇到无法推进的外部依赖应进入 `blocked` 并说明责任人/解除条件，而不是静默等待。heartbeat 指南要求 agent 身份、收件箱、领取和定期进展回报。[状态与进展更新示例](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L24-L51) [Heartbeat 协议](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/heartbeat-protocol.md#L31-L83)
4. **明确请求人类决定：** 如果需要 board/user 对方案作出接受或退回决定，agent 应创建 `request_confirmation` 交互；计划审批可以精确绑定到 issue 文档的某个 revision。它是 issue thread 里的显式交互，不是任何 `done` 状态都隐含的一次通用验收。[确认交互与 revision 绑定](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L71-L116) 路由实现还会在 issue `in_review` 时检查待决 review interaction 和该 issue 的 review policy。[Review verdict 权限检查](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/server/src/routes/issues.ts#L6210-L6256)
5. **结束、交接或恢复：** 官方示例用 `PATCH status=done` 表示 agent 自报完成；issue 也可转 `in_review`、`blocked` 或交由人类负责。终态 run 结束时按 run ID 比对并清理仍归该 run 的锁；崩溃后可清理指向终态/不存在 run 的陈旧锁，但不得抢占仍在运行的 owner。[完成与阻塞更新示例](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L24-L51) [终结锁与恢复规则](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L202-L213)

**并发/恢复的代码证据：** 固定提交中的 route tests 覆盖仍有活跃 owner 时返回 `409` 并保留其锁，以及原 run 已失败后新 assignee checkout 可恢复陈旧 `checkoutRunId`。[活跃锁冲突测试](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/server/src/__tests__/issue-stale-execution-lock-routes.test.ts#L351-L440) [失败 owner 的陈旧锁自愈测试](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/server/src/__tests__/issue-stale-execution-lock-routes.test.ts#L516-L575)。这些测试本次未执行。

## 动态角色、职责、人工治理与预算

- **动态角色与配置：** agent 可配置角色、`reportsTo`、能力描述、adapter/runtime、prompt/model、heartbeat 和预算；这些字段描述 agent 的职责与执行方式，不构成多人共同拥有同一 issue 的机制。[官方 agent 字段和配置](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/managing-agents.md#L19-L28) [Heartbeat 与预算设置](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/managing-agents.md#L67-L75)
- **人类 board 的职责：** 配置 agent、分派/改派 issue、批准 hire 或战略提案、定义 review policy，并可暂停或终止 agent。hire 的生命周期单独包括等待批准、准备、配置验证、ready、暂停与终止状态。[官方审批队列说明](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/approvals.md#L8-L42) [Hire 生命周期](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/AGENT-LIFECYCLE.md#L36-L62)
- **审批对象与完成状态：** 官方指南中的 `hire_agent`/CEO strategy 是治理审批；task workflow 的 `request_confirmation` 是 issue 对话内、可绑定目标 revision 的接受/修改交互；issue `done` 是任务状态。三者不应混称为“验收”。审批指南所列的 approval payload、reviewer UI 流程是官方文档自述，本次未实际打开 UI。[审批类型和 payload（官方文档）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/approvals.md#L8-L42) [Issue confirmation](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L71-L116)
- **预算和暂停：** agent 可有月预算；board 文档说明达到月预算上限会自动暂停。代码生命周期模型将手动暂停、预算暂停与公司暂停作为独立 holds，手动 resume 不会解除其他 hold。[预算说明（官方文档自述）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/managing-agents.md#L67-L91) [暂停 holds（源码文档）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/AGENT-LIFECYCLE.md#L162-L177)
- **执行工作区：** workspace 配置描述可运行的 services/jobs；issue heartbeat 可创建或复用 execution workspace 并启动配置为 running 的服务。执行工作区有独立 checkout/runtime，生命周期可持续到人类关闭。该指南说明意图与实现模型，但没有本次端到端体验证据。[Workspace/run 生命周期（官方指南及当前实现说明）](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/execution-workspaces-and-runtime-services.md#L14-L66) [人工关闭与清理](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/execution-workspaces-and-runtime-services.md#L50-L56)

## 对 Career 可借鉴与不宜照搬

**可借鉴：**

1. 将任务归属锁和活跃执行 run 分开，并让领取、冲突与陈旧锁恢复成为可检查状态；不要只用一个“处理中”字段表示排队、占有和执行。[锁与恢复语义](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L189-L213)
2. 把明确需要人的接受/退回做成有目标、有版本、有状态的 interaction，而不是把自然语言留言或 `done` 当作审批。[Confirmation API 合同](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/agent-developer/task-workflow.md#L71-L116)
3. 对异步等待要求持久的下一步负责人/动作，把 parent relation、blocker 和负责人的含义分开，避免只靠本地后台进程表示工作还会继续。[Durable waits 与 issue links](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L261-L300) [本地 watcher 不构成持久动作路径](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L407-L444)

**不宜照搬：**

1. 对一人公司，不宜把完整公司组织、招聘审批和多层管理设置作为第一次交办任务的前置条件。可以借鉴一个任务有明确责任人的原则，协作者通过子任务或产物协作参与。[单负责人不变量](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L22-L41)
2. 不要把 `done` 当成人工核验，也不要将 Paperclip 文档里的预算/heartbeat/workspace 规则直接移植成 Career 产品要求；它们服务于持续执行 agent，公司治理和隔离 checkout 的上下文。[状态语义](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/doc/execution-semantics.md#L43-L71) [执行工作区模型](https://github.com/paperclipai/paperclip/blob/2c17d3ca52a7840f280f1dd87975a2ad77de3b35/docs/guides/board-operator/execution-workspaces-and-runtime-services.md#L32-L56)

## 后续体验验收（本次未执行）

1. 实际 UI 创建 issue，分别分配给 human/agent；确认状态、负责人、wake 及事件记录如何呈现。
2. 用两个并发 run checkout 同一 issue，确认只有一个获得所有权；终止 owner 后再 checkout，观察陈旧锁恢复与 audit trail。
3. 从 UI 提交 hire approval、issue `request_confirmation` 和完成任务，分别核实它们的对象、按钮、权限、状态迁移及 `done` 是否需要额外 reviewer。
4. 启动一次带运行服务的 issue heartbeat 后重启服务，验证 execution workspace、checkout、日志与 service 的实际恢复行为。

**未验证：** 本次没有安装或运行 Paperclip，没有验证真实 provider、agent 工具副作用、UI 显示、费用计量或重启恢复。本文是 pinned-source desk research，测试文件只作为预期合同证据，不代表测试通过。
