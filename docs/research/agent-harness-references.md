# Agent Harness 开源项目机制核查

查证日期：2026-10-14

## 证据边界

只读查看指定项目的官方仓库源码与官方文档；没有安装、运行或测试这些项目。下文把来源明确写出的行为标为“事实”，把可迁移的设计启示标为“借鉴”。仓库 `main` 分支会变化，链接未固定 commit；不能据此确认任一项目在特定版本或环境中的运行结果，也不能推断 `.NET 10`、MAF 或 Native AOT 兼容性。

## OpenClaw

1. **按 session 执行、流式报告终态。事实：** `agent` 请求解析 session 后立即返回 `runId`；运行阶段按 session 和全局队列串行化，推送 assistant、tool 和 lifecycle 事件，支持超时取消；`agent.wait` 读取终态。**借鉴：** harness 应区分已接收、运行中、工具执行和终态，并用事件流驱动面板。来源：[Agent loop](https://github.com/openclaw/openclaw/blob/main/docs/concepts/agent-loop.md)。

2. **模型供应商与执行 runtime 分开。事实：** 支持供应商插件和自定义 provider/base URL；资料列出 OpenAI Responses、Chat Completions、Anthropic Messages、Google Generative AI 等协议路由。**借鉴：** 将协议适配和 agent loop 分离；“兼容某 API”需按端点与请求能力核实。来源：[Custom providers](https://github.com/openclaw/openclaw/blob/main/docs/concepts/model-providers/custom-providers.md)、[provider transport mapping](https://github.com/openclaw/openclaw/blob/main/docs/providers/clawrouter.md)。

3. **技能共享与 Agent 可见集合。事实：** 可加载额外 skill roots、控制 symlink 目标，并在 Agent 级设置技能 allowlist；文档说明该 allowlist 只控制发现和暴露，不是 shell 授权边界。**借鉴：** 工具分派与实际执行权限要分别控制。来源：[Skills config](https://github.com/openclaw/openclaw/blob/main/docs/tools/skills-config.md)。

4. **MCP 同时作为客户端与服务端。事实：** `openclaw mcp serve` 可暴露 Gateway 对话；其他命令管理出站 MCP server。`status`/`doctor` 检查配置，`probe` 才建立连接探测能力；Control UI 可编辑 MCP 配置。**借鉴：** 展示配置、连接和工具发现等不同健康状态。来源：[MCP CLI](https://github.com/openclaw/openclaw/blob/main/docs/cli/mcp.md)。

5. **Sandbox 和审批是分层策略。事实：** sandbox mode、scope、backend 分开配置；host exec 还独立应用策略、allowlist 与人工审批，并可按 agent 配置。**借鉴：** 审批策略不能替代文件/进程/网络隔离。来源：[Sandbox mode/scope/backend](https://github.com/openclaw/openclaw/blob/main/docs/gateway/sandboxing/modes-scope-and-backend.md)、[Exec approvals](https://github.com/openclaw/openclaw/blob/main/docs/tools/exec-approvals.md)。

## Codex

1. **核心逻辑与 UI 解耦。事实：** `codex-core` README 将核心业务逻辑定位为供多个 Rust UI 使用的 crate。**借鉴：** 将 turn loop、工具执行策略和 UI 呈现放在独立层。来源：[codex-core README](https://github.com/openai/codex/blob/main/codex-rs/core/README.md)。

2. **审批结果有三种状态。事实：** 工具策略会给出 `Skip`、`NeedsApproval` 或 `Forbidden`；是否需审批会结合审批策略和文件 sandbox 状态计算，session 可记住获批规则。**借鉴：** 将“无需询问”“等待确认”“策略禁止”作为不同状态保存和呈现。来源：[sandboxing.rs](https://github.com/openai/codex/blob/main/codex-rs/core/src/tools/sandboxing.rs)。

3. **Session rollout 可恢复与检查。事实：** Codex 将 rollout 记录为 JSONL，记录器支持从已有 rollout 恢复；源码也有相应 rollout 路径测试辅助代码。**借鉴：** 持久事件轨迹有利于会话恢复和审计。**边界：** 这些来源没有证明 JSONL 是 Codex 所有状态的唯一事实源。来源：[recorder.rs](https://github.com/openai/codex/blob/main/codex-rs/rollout/src/recorder.rs)、[rollout test helper](https://github.com/openai/codex/blob/main/codex-rs/app-server/tests/common/rollout.rs)。

4. **MCP 可按 server 与 tool 管理。事实：** 当前配置参考列出 server 启停、工具 allow/deny list、默认审批模式、单工具审批覆盖，以及启动和工具超时。**借鉴：** MCP 授权粒度可以细化到单个工具。来源：[Codex config reference](https://learn.chatgpt.com/docs/config-file/config-reference)。

5. **模型协议有边界。事实：** 同一份当前配置参考将自定义 provider 的 `wire_api` 列为 `responses`，并说明这是唯一支持值。**借鉴：** Codex 的 provider 配置不应被视为 Messages、Gemini 或 Chat Completions 的通用适配器；此处结论只针对该当前文档描述的版本。来源：[Codex config reference](https://learn.chatgpt.com/docs/config-file/config-reference)。

## Pi

1. **Loop 将内部消息结构和供应商调用隔离。事实：** loop 以自己的 `AgentMessage` 运作，只在 LLM 调用边界转换；代码包含事件、取消、工具参数校验、steering/follow-up 和 turn 继续/结束路径。**借鉴：** 通过独立协议适配器避免各模型 API 泄漏进 loop。来源：[agent-loop.ts](https://github.com/earendil-works/pi/blob/main/packages/agent/src/agent-loop.ts)。

2. **常见协议可配置，特殊 provider 可扩展。事实：** `models.json` 支持 OpenAI Completions、OpenAI Responses、Anthropic Messages、Google Generative AI；需要自定义流式、模型发现或认证时使用 provider extension。**借鉴：** 将常见接入作为数据配置，保留扩展点处理协议差异。来源：[Custom models](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/models.md)。

3. **会话支持树形分支与压缩。事实：** session 保存为树，可继续、浏览、切分支、fork 或 clone；compaction 和 branch summary 用于减少发送给模型的旧历史。**借鉴：** 把“历史分支”作为一等会话操作。来源：[Sessions](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/sessions.md)、[Compaction](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/compaction.md)。

4. **技能按需读取。事实：** 启动时向模型提供技能名、描述和路径；匹配任务后再读 `SKILL.md`，技能可来自用户、项目或 Agent Skills 目录。**借鉴：** 大型指引不必常驻每轮上下文。来源：[Skills](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/skills.md)。

5. **扩展能力强，但默认不提供安全隔离。事实：** Extension API 能注册工具、provider、MCP 和生命周期事件；官方安全文档明确说 Pi 没有内置 sandbox，内置工具和扩展与 Pi 进程共享操作系统权限。**借鉴：** 插件确认逻辑不能替代 OS 级隔离。来源：[Extensions](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/extensions.md)、[Security](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/security.md)。

## Codex 插件格式兼容矩阵

| 项目内容 | 可移植 Agent Plugins 包 | Codex 兼容格式 | 兼容边界 |
|---|---|---|---|
| 身份文件 | 插件根目录 `plugin.json` | `.codex-plugin/plugin.json` | 官方文档保留 Codex 格式作兼容 fallback；不是两个 manifest 自动合并。 |
| Skills | 根目录 `skills/` | manifest 可用 `skills: "./skills/"` | 路径相对插件根目录；通用 skill 可跨宿主复用。 |
| MCP | 根目录 `mcp.json`，按 Agent Plugins schema，server 声明 transport `type` | 常见为 `.mcp.json`，由 `mcpServers` 字段引用 | 不应只改文件名：两种 MCP 文件格式有区别。 |
| OpenAI 专属设置 | 放在 `plugin.json` 的 `extensions.com.openai` | 可放在兼容 overlay 中 | `extensions.com.openai` 存在时替代整个 Codex overlay 中的 OpenAI 专属设置；不合并。 |
| Hooks | 可带 hooks 配置，但公开目录有发布限制 | 可由 Codex 手动安装插件加载 | 安装/启用不等于信任 hooks；Codex 要求用户审核并信任当前定义。 |

格式来源：[官方 Package your plugin 文档](https://developers.openai.com/plugins/build/plugins)、[官方插件样例](https://github.com/openai/plugins)。**不要把文件格式兼容理解为完整运行时兼容。** OpenClaw 文档说明它会识别 Codex bundle，但只消费其支持的元数据、技能目录及特定组件，不会按 `openclaw.plugin.json` 原生 schema 校验；不能据此推断 Codex 的 apps、hook 权限或所有插件行为都能移植。来源：[OpenClaw plugin manifest](https://github.com/openclaw/openclaw/blob/main/docs/plugins/manifest.md)。
