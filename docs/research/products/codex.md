# Codex：可嵌入运行服务、审批与会话记录

核查日期：2026-10-14；固定提交 [`95273b84fbff40f9df7d71d50f1ca52c6489b25d`](https://github.com/openai/codex/commit/95273b84fbff40f9df7d71d50f1ca52c6489b25d)。阅读公开源码与 OpenAI 官方文档，没有安装该提交或执行上游测试。开源 core/app-server 的实现，不等于 Codex 桌面产品、云服务和所有账号功能都开源或默认可用。

## 值得研究的产品边界

Codex 的参考价值是把交互界面与具有会话、工具、审批和事件协议的执行内核分开。App Server 可供客户端创建/恢复 thread、启动或引导 turn、接收 item 更新并处理审批。官方文档以双向 JSON-RPC 为基础，stdio 使用逐行消息；其中 WebSocket 仍有实验边界，不能因存在一种传输就认定它适合直接公网部署。[App Server 官方文档](https://learn.chatgpt.com/docs/app-server)

对本项目的直接启发是：React 不需要内嵌模型循环，桌面壳也不必拥有业务状态。岗位身份、任务记录、运行会话和工具动作可各自有稳定标识。

## 调用链一：开始执行 → 工具 → 审批 → 结果

1. `TurnStart` 经 message processor 进入 turn processor；后者构造模型、审批和 sandbox 等覆盖配置，再调用 `start_or_steer_turn`。结果区分 Started、Steered、NotSubmitted，故不能把每个 start 请求都理解为新建一个并行运行。[消息分派](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/app-server/src/message_processor.rs#L1643-L1651)、[启动或引导](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/app-server/src/request_processors/turn_processor.rs#L640-L702)
2. 模型发出工具调用后，调用 item 先进入 conversation history，再交给执行队列；工具 future 完成后把结果 envelope 放回历史。调用意图、执行中、执行结果是不同记录。[调用排队](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/stream_events_utils.rs#L323-L357)、[结果写入](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/session/turn.rs#L2564-L2590)
3. 命令需要批准时，审批实现进入 `request_command_approval`；会话先登记一个响应等待者，再发布审批事件，等待决定。等待通道关闭则返回 Abort。[审批调用](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/tools/approvals.rs#L643-L688)、[登记、发布与等待](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/session/mod.rs#L2920-L3008)

官方协议让客户端把审批请求关联到具体 thread、turn 和 item，答复后等待后续执行事件。批准只说明许可，不表示命令已经成功。[审批协议](https://learn.chatgpt.com/docs/app-server#approvals)

**恢复边界：** 这里的 oneshot 是活跃进程中的等待机制。虽然会话和事件会持久化，但仅凭这条链不能推断“杀死进程后，旧审批自动绑定原副作用并安全继续”；这需要单独的重启实验。

## 调用链二：创建子 Agent → 建立关系 → 取消或恢复

工具层创建 child 时携带 parent thread/turn、root turn 和调用来源；control 层先创建 child thread，等待父子 edge 的持久化尝试完成，再交付初始 input。[工具入口](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/tools/handlers/multi_agents/spawn.rs#L113-L139)、[child 与 edge](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/agent/control/spawn.rs#L840-L913)、[初始输入](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/agent/control/spawn.rs#L935-L960)。

创建中途取消时，未解除的 `PendingSpawn` guard 负责移除和关闭 child、清理持久记录，并将父子 edge 收束为 Closed。恢复时先找已加载运行对象，仅在找不到时才从 rollout 恢复。V2 interrupt 还区分 root/self 与合法 child 目标。[取消清理](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/agent/control/spawn_guard.rs#L56-L103)、[恢复](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/agent/control/resume.rs#L14-L35)、[中断边界](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/agent/control/interrupt.rs#L14-L50)。

**设计判断：** 子任务不能只有一个后台 Promise；需要归属、运行状态和取消时的清理规则。另一方面，child thread 是执行协作对象，不自动具有 Paperclip 式的长期岗位、预算、待办和汇报线。

## 事件、落盘和成功的含义

会话事件路径会尝试先持久化 `RolloutItem::EventMsg`，然后发给 listener；append 失败会记录错误并返回 false，事件仍可能继续投递。rollout JSONL 带时间和序号。[事件持久化与投递](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/session/mod.rs#L2680-L2698)、[持久化失败处理](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/core/src/session/mod.rs#L4745-L4752)、[JSONL 写入](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/rollout/src/recorder.rs#L2074-L2100)。

**推论：** 收到 UI 事件不能独立证明已落盘，更不能证明外部动作恰好执行一次。本项目对任务认领、审批决定和副作用回执应有自己的持久状态合同；这不是把 Codex 的所有展示事件强行做成数据库事务。

## 记忆、工具与扩展

固定提交有独立记忆流水线：只有相应 feature 启用、非 ephemeral、root agent 等条件满足时才启动；状态库不可用或限流可以跳过。候选会话按来源、年龄、空闲时间、扫描数量和 lease 限制处理，再分阶段整理。[启动与跳过](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/memories/write/src/start.rs#L20-L91)、[候选领取](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/memories/write/src/phase1.rs#L120-L155)。

整理结果可物化为摘要与本地文件；专门的记忆读取工具受配置控制，并约束在指定根目录内，拒绝目录逃逸和 symlink。不能由此推断每个 child 都有同样的记忆写入权限，也不能把这份实现直接称为完整岗位记忆体系。[摘要文件](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/memories/write/src/storage.rs#L12-L78)、[读工具注册](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/ext/memories/src/extension.rs#L135-L159)、[读取边界](https://github.com/openai/codex/blob/95273b84fbff40f9df7d71d50f1ca52c6489b25d/codex-rs/ext/memories/src/local.rs#L32-L81)。

官方插件格式可以作为导入资源清单的参考，但包能被识别、技能能被读取、MCP 能连接、云端应用获授权和平台特有能力能运行是不同兼容级别。[插件构建文档](https://developers.openai.com/plugins/build/plugins)。本次没有运行第三方插件兼容实验，也没有据此宣称所有模型厂商均可用。

## 对本项目的取舍与后续验证

建议借鉴：thread/turn/item 分层；执行状态和审批的具体关联；父子任务持久关系及取消清理；记忆后台提炼、范围限制和跳过原因。

不照搬：所有 UI 事件都作为业务成功依据；将公开 app-server 等同整个桌面生态；把所有持久对话都当作安全可重放工作流。Rust 实现可作为合同参考，不是 .NET/MAF/AOT 兼容性的证据。

下一轮若做运行体验，应测审批拒绝、审批期间客户端断线、服务重启、工具完成前后终止、child 创建中途取消和记忆关闭；分别检查 transcript、可见状态、真实文件效果和恢复后的输入。以上本次未执行。
