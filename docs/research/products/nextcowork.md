# NextCoWork：桌面工作台与恢复边界

核查：2026-10-14；固定提交 [`b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917`](https://github.com/AIDotNet/NextCoWork/commit/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917)。本次阅读源码，没有安装或运行上游。下文“源码确认”不是运行通过。

## 产品与用户路径

NextCoWork 把会话、文件、编辑器、终端和工具执行组合在桌面工作空间内。[产品功能](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/README.md#L9-L29) 实现由 React renderer、Electron 主进程与 TypeScript 代码组成，并非 .NET。它适合参考“人在执行过程中随时查看和接管”的工作台，而不能直接当作浏览器版服务架构。[项目结构](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/README.md#L71-L80)

用户发起会话，界面显示运行和工具事件；需要确认时等待用户回答，执行后把历史、子任务结果和文件变更留下。普通对话结束、某个工具执行完成、业务成果已验收是不同边界；本次没有发现可以据此认定“完整公司岗位流程已实现”的依据。

## 调用链一：输入、模型、工具与历史

源码主链为：React session store 先登记 `runId` → renderer service → preload 白名单 IPC → main handler 先订阅事件再启动 → `RunHandle/RunPump` → `AgentSession` → `UpstreamRouter` → HTTP/SSE 模型流 → Agent 事件回到 UI。每一层的固定源码链接见 [完整通信链](../nextcowork-runtime-communication.md)。React 不是直接访问模型。

这里值得借鉴的是“传输适配与运行内核分离”，不是 Electron IPC 这个具体选择。用户已选的 React + 独立 .NET 服务可保留相同职责分层，外部换成 HTTP/SSE。

会话历史从 store 读取，`message_commit` 时逐条保存，结束时再合并最新持久化记录。父运行的旧内存快照不能覆盖刚完成的独立子任务回执；实现专门处理了这种竞争。[启动上下文](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L2835-L2855)、[逐条提交](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L2958-L2975)、[子任务回执合并](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L1928-L2008)、[运行收尾](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L3134-L3182)。

模型重试和 failover 归 `UpstreamRouter` 管理，`AgentSession` 明确避免再叠一层通用重试，减少重复副作用。这是职责划分证据，不等于任何工具都具备幂等性。[AgentSession 约定](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/kernel/agent-session.ts#L1-L11)。

## 调用链二：交互确认与不同层级的恢复

`InteractionGate.request()` 创建待答交互，存在主进程的 Map 与 Promise 中，向运行流发布请求；renderer 回答后，`respond()` 校验交互仍存在、运行仍有效和响应类型，再完成等待。取消和运行结束负责清理。窗口重新加载不会丢掉主进程内的待答对象。[InteractionGate](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/kernel/interaction-gate.ts#L65-L134)。

**边界：这条代码证明窗口刷新恢复，不证明主进程被杀后的审批续跑。** 从数据库读到的会话 `running` 状态会归一为 `idle`；恢复历史和恢复正在执行的 Promise 不能画等号。[会话读取](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/db/repo.ts#L495-L505)。

但它另有更强的**远端设备命令账本**，不能因此笼统说“没有恢复设计”：

1. 变更请求必须有幂等键；方法与参数摘要相同则返回原回执，键相同但参数变了则拒绝。[接纳](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/db/device-commands.ts#L40-L70)
2. 状态迁移检查预期旧状态，回执与事件同事务写入。[迁移](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/db/device-commands.ts#L81-L103)
3. 服务重启后，未开始的 accepted 变 interrupted，已开始的 running 变 unknown；不把未知结果自动重放成成功。[重启收尾](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/db/device-commands.ts#L107-L129)

## 多 Agent、资源和记忆

子任务队列以同步占位避免竞争，具有等待数量上限，并处理父任务取消、死锁和队列溢出；不能把临时子任务队列直接当作长期岗位编制。[subagent queue](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/kernel/subagent-queue.ts#L57-L110)。

项目提供 Skills 与 MCP 接入，插件还可贡献 UI、工具、技能和应用；这是 NextCoWork 自己的扩展系统，不能据“支持插件”推断与 Codex 插件的 manifest、授权、生命周期完全兼容。[插件概述](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/README.md#L17-L19)。

已核到持久对话、压缩上下文、个性化和技能提炼入口；未建立“岗位经验分层、冷热迁移、跨岗位共享发布和纠错”整套机制的证据。尤其要把 session 历史、摘要、可复用技能与长期事实分开建模，不能统称 memory 后认为需求已覆盖。[启动上下文](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L2835-L2855)、[收尾与技能激活](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/runtime.ts#L3110-L3182)。

远端设备走 WSS，复用运行服务；当前 broker 明确限制部分远端附件、子代理和计划输入，不能把本地能力自动算成远端能力。[远端限制](https://github.com/AIDotNet/NextCoWork/blob/b9f580a6c7dadf5ee37e8f1d7e7c09d10a07b917/src/main/device/broker.ts#L147-L176)。

## 对本项目的取舍

建议采用：统一运行事件；renderer 刷新后重新读取权威状态；子任务结果与父会话分开提交再合并；设备命令的幂等键、参数摘要和 unknown 状态。

不照搬：把业务运行绑死 Electron 主进程；把所有故障都归成 idle；将内存审批等待当作持久 checkpoint。以上是设计判断，不是上游缺陷定论。

下一轮若做实际试用，应分别测窗口刷新、主进程终止、父子同时结束、同键不同参数、远端断线五类场景；记录是否重复执行、结果是否丢失、UI 是否能说明下一步。本次均未执行。
