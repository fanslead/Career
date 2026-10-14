# DeepSeek Harness：纯 Web 执行工作台、插件组合与恢复边界

核查：2026-10-14；固定提交 [`d743267388641bc76f17c45ce8b4c231aed1d32c`](https://github.com/deepseek-ai/deepseek-harness/tree/d743267388641bc76f17c45ce8b4c231aed1d32c)。本次读取官方文档与源码，没有安装依赖、启动产品、连接模型或运行上游测试。下文严格区分源码事实、官方声明及对本项目的设计判断。

本文是总研究；两份专项报告给出逐段源码路径和完整调用链：[Web/Host、部署、审批与插件](../deepseek-web-runtime.md)、[Agent loop、日志、恢复、子 Agent 与记忆](../deepseek-runtime-recovery.md)。

## 1. 定位与成熟度

DeepSeek Harness 是 Node/TypeScript 生态的可组合执行框架，浏览器工作台是随附产品形态之一。**无需 Electron 即可使用 Web 版本**：官方命令 `npx @deepseek-ai/dsh web` 默认启动本地 `127.0.0.1:3080` 服务。仓库也有复用同一 Web 应用的 Electron Desktop；所以“有纯 Web 形态”成立，“只能以 Web 运行”不成立。[启动说明](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/README.zh.md#L21-L45)、[Desktop 边界](../deepseek-web-runtime.md)

官方将其标为开发者预览，并明确会有破坏性变更。安全文档声明尚未经过安全审计，不将当前版本视作生产安全保证。根仓库标注 MIT，同时另列第三方许可证。这里记录来源状态，不作法律适用性结论，也不据其开源身份推断可靠性。[预览状态](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/README.zh.md#L11-L15)、[安全声明](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/SAFETY.zh.md#L5-L15)、[许可证入口](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/README.zh.md#L92-L96)

## 2. 架构：一切皆插件，产品由 profile 组合

Cordis 管理插件依赖、作用域、事件和生命周期；模型适配器、工具、日志、Agent loop、UI 都能作为组件组装。profile 是具名的运行配置，bundle 提供配置层，用户 patch 再覆盖。Web、Headless、SDK、ACP 共用基础组合；`sdk-minimal` 是显式单独组合。它的 profile/preset 主要表达运行环境与能力集合，不能直接等同于我们产品中的长期岗位与业务责任人。[架构与组合规则](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/architecture.zh.md#L7-L29)

能力分成接口、提供者与消费者。例如工具消费文件/进程服务，替换提供者可改变执行位置；子 Agent 后端可以是本地执行，也可以委托其他产品。这比让所有工具直接访问宿主 API 更便于替换，但替换点越多，产品越需要启动审计和明确能力合同。[能力分工](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/architecture.zh.md#L134-L140)

对本项目的判断：保留固定业务内核、可替换 Worker/工具/解析器适配面即可。我们不照搬“任何核心行为都能动态替换”：审批记账、写前持久化、任务责任和恢复判定必须由 Host 强制执行。Node 插件树也不能证明 .NET Native AOT 兼容。

## 3. Web 到运行器的真实链路

```mermaid
sequenceDiagram
  participant UI as 浏览器 Composer
  participant API as Host HTTP RPC
  participant LOOP as Session / Agent loop
  participant LOG as Session 日志
  participant WS as WebSocket Remote 流
  UI->>API: POST api/session/prompt
  API->>LOOP: 接纳输入
  LOOP->>LOG: 持久事件与已结算消息
  LOOP->>WS: 实时 assistant stream
  LOG->>WS: follow 快照、cursor 与增量事件
  WS-->>UI: 合并状态和输出
  LOOP->>WS: approval/request
  WS-->>UI: 待决定交互
  UI->>API: $events/result
  API->>LOOP: 核对交互并返回决定
```

图来自固定源码调用链：普通请求是 HTTP RPC，长流共用 `/api/remote.mux` WebSocket，分逻辑 stream ID。首次跟随包含快照、cursor 和可能存在的实时输出基线；重连按领域水位重开并补历史，不是重新执行提交过的任务。[完整调用链及源码](../deepseek-web-runtime.md)

实时 token 与已持久结算的消息不同：若进程在 settlement 前硬终止，已经在屏幕上看见的部分输出未必进入最终日志。恢复 UI 必须以持久水位为准，不把浏览器缓存视作成功结果。[日志与实时增量边界](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/architecture.zh.md#L122-L130)

对本项目的判断：这支持“独立服务 + 浏览器”的路线，但不要求我们换成 WebSocket。当前以命令与服务器进度推送为主，HTTP/SSE 足够；未来交互终端等双向连续通道再加 WS。无论用什么传输，快照、持久 cursor、重复决定和未知结果的语义都必须保持一致，见 [通信合同](../../11-frontend-runtime-protocol.md)。

## 4. Agent loop、会话日志与持久化边界

源码中的循环先接纳输入、准备实际模型路由、组装并冻结请求，再消费模型流；工具调用和结果通过日志形成模型可读历史。`deriveMessages()` 是日志投影，UI、恢复和模型上下文可围绕同一历史解释。Session 的写句柄有归属控制，格式迁移发布新的 generation，不直接覆盖旧持久代次。[循环与持久日志分析](../deepseek-runtime-recovery.md)

会话检查点策略在关键边界 flush：模型请求前、外层工具执行前以及后续步骤边界。它是组合中的策略组件：Web、Headless、SDK、ACP 继承 base，默认启用；`sdk-minimal` 有 JSONL 持久化但没有此策略。更高优先级 patch 还能禁用或覆盖它，不能把默认保证外推到所有组装。**记录了工具调用但没有结果时，恢复只能知道结果未知；没有调用记录能否证明尚未执行，取决于写前屏障是否确实生效。** 默认配置核查和精确边界见 [运行恢复专项](../deepseek-runtime-recovery.md)。

这一点直接影响我们的 [13 恢复合同](../../13-restart-and-recovery.md)：Host 必须先提交 Operation 意图及执行资格，提交失败就停止派发。写前屏障是产品保证，不可由普通插件关闭。

## 5. 恢复并不等于无缝继续原执行

| 问题 | 此提交可确认的机制 | 不能据此声称 |
| --- | --- | --- |
| 重新打开历史 | 持久 Session、投影、版本迁移 | 所有屏幕实时 token 均已持久 |
| 浏览器重连 | follow 快照、水位、流代次和缺口补齐 | Host 中的内存交互等待自动跨进程复活 |
| 未完成 turn | 写入 interrupted closure，补齐缺失工具结果 | 接着原模型 socket 继续生成 |
| 工具已调用但无结果 | 标记 `TOOL_OUTCOME_UNKNOWN` | 工具失败或可以再次执行 |
| 可继续子 Agent | 持久描述符、父子关系；后续 message 可触发冷恢复 | 重启自动启动所有子 Agent 或继续所有业务任务 |
| 审批 | asked/decided 留日志，未获有效答复则关闭许可 | 仅靠日志就能复原运行期 Promise、授权派发和完成对账 |

上述为源码结论；具体文件与分支见 [运行恢复专项](../deepseek-runtime-recovery.md)及[审批往返链](../deepseek-web-runtime.md)。源码中有相关测试文件，本次没有运行，不把它们计为验证通过。

值得学习的是未知结果的诚实表示；仍需我们实现的是 Operation 账本、目标读回核对、持久待决对象、取消意图、旧执行器停止与重新领取资格。SMTP 等没有可靠幂等或查询保证的动作，不能因为 Harness 能加载历史而自动再发。

## 6. 子 Agent 与实验性 Agent Teams

基础 subagent 能力支持不同执行后端、一次性调用与可继续子会话；父子信息和工具结果留在会话记录。它解决执行分工，和稳定岗位、持续责任及成果验收有不同生命周期。[子 Agent 实现证据](../deepseek-runtime-recovery.md)

Agent Teams 位于实验模块，随附 profile 默认关闭，需显式启用。它建立在可继续 subagent 上，增加持久 roster、任务状态和 direct inbox。已 flush 的任务 claim 在投影恢复后仍保留 owner/status，但退出或不活跃不会自动释放 claim，也不表示任务执行随重启继续；消息进入 inbox 不等于已经处理完成，Team 层没有因此获得 durable outbox 的重试和去重保证。[Agent Teams 配置与源码核查](../deepseek-runtime-recovery.md)

因此，“有团队任务表”不能替代“谁此刻持有执行资格、旧写者是否停止、父任务如何等待、结果如何验收”。我们的设计分别保存长期责任人、assignmentVersion、Run/Attempt 租约、child 等待条件和成果版本，见 [04 岗位协作](../../04-agent-harness-and-projects.md)。

## 7. 上下文、记忆与知识

源码可确认指令文件注入、模型可见上下文和压缩摘要等机制。它们主要解决当前 Session 的输入组织，不是自动形成可治理的长期事实。官方还提供三个默认关闭的 MCP memory 参考配置：Memorix、MCP Reference Memory、Engram。安装、存储及服务能力属于外部服务器；Harness 负责把服务接成工具。[上下文和 memory 核查](../deepseek-runtime-recovery.md)

Reference Memory 示例使用外部 JSONL 知识图谱，需要模型实际调用保存与读取；该示例 search 为字符串匹配，不能写成 Harness 内建的自动语义记忆。没有证据据此声称它具备我们需要的岗位私有/共享发布、候选确认、来源撤回、冷热分层及旧备份防复活机制。[官方 MCP memory 说明与源码证据](../deepseek-runtime-recovery.md)

本项目继续独立管理工作上下文、经历、事实、岗位经验和知识资料。MCP memory 可以作为明确受限的外部数据源或工具，不能绕开本地记忆的作用域、发布和遗忘合同。[06 记忆设计](../../06-memory-and-knowledge.md)

## 8. 模型、技能、MCP 与插件体验

官方模型页提供内置供应商目录和自定义 API。自定义 API 明确选择 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 三种协议，一个 provider 一种协议；模型列表探测只是便利功能，失败时可手填。API 密钥只写并返回脱敏描述符，settings 保存凭据引用。高级兼容参数仍在 profile 文件中；当前文档不支持 Codex 一类 OAuth provider 登录。[模型页与协议选择](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/user/guide/providers.zh.md#L5-L48)

源码进一步确认：模型探测经 `remote.llm.discoverModels`；内置 provider 返回已安装目录，自定义 provider 才向 endpoint 查询 `/models` 或 `/v1/models`，探测不保存表单。保存配置与保存密钥是先后两个动作，UI 不能把第一步成功视为整个连接可用。每次模型调用捕获不可变配置，后续改动在下一次请求生效。[探测实现](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/llm/llm-pi-ai/src/discovery.ts#L269-L362)、[保存顺序](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/client/ui-settings-models/src/client/CustomProviderCard.tsx#L153-L191)、[配置快照](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/llm/llm-pi-ai/src/adapter.ts#L330-L370)

模型输入能力与兼容开关是对 endpoint 的声明，不是已经做过能力探测。这支持我们把“声明支持”和“实测通过”分开，也说明“能配置很多厂商”不能直接等于“所有工具循环都已兼容”。[能力声明边界](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/user/guide/providers.zh.md#L95-L109)、[兼容开关边界](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/user/guide/providers.zh.md#L155-L190)

技能注册表合并 host 与 preset scope，邻近作用域可覆盖重名技能；目录只提供摘要，正文按需加载。技能可见性是上下文装配规则，不能据此推断 shell/文件已经隔离。[技能分层与渐进读取](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/subsystems/skills.zh.md#L9-L17)、[摘要与正文](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/docs/subsystems/skills.zh.md#L92-L128)

MCP 每个客户端实例配置一台服务器，支持命令启动的 stdio 和连接 URL 的 Streamable HTTP；服务器名称受注册 scope 约束。环境变量清洗、显式 headers 和可见范围都有边界，但启动子进程本身不构成隔离。Web 的全局技能发现被关闭，由 standard/ptc/cordis 等 preset 挂载，不能把全局安装直接理解成每个 Agent 都可见。[MCP 配置](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/mcp/mcp-client/src/index.ts#L43-L100)、[MCP 传输](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/mcp/mcp-client/src/transport.ts#L31-L45)、[Web 技能分层](https://github.com/deepseek-ai/deepseek-harness/blob/d743267388641bc76f17c45ce8b4c231aed1d32c/packages/bundle/web-app/cordis.patch.yml#L493-L506)

插件页管理 profile 全局组合，Host 执行包安装；依赖构建脚本被阻止时，界面列出精确包名并申请许可。浏览器插件可进入同一页面的模块与插件树；生命周期隔离没有自动变成 iframe 或操作系统沙箱。我们只借鉴明确来源、配置覆盖和安装状态的体验，首版仍以 Skills/MCP 为主，不直接加载任意第三方 React 代码。[插件管理源码链](../deepseek-web-runtime.md)、[本项目兼容分级](../../05-models-tools-plugins.md)

## 9. 部署与授权边界

默认 loopback、初次 token 换取 cookie、Host/Origin 准入和反向代理部署都有明确实现。`--public-url` 仅公告访问地址，不自动开放信任、配置反向代理或改写 cookie 路径；远程浏览器访问还需按文档配置 authority、代理前缀和 WebSocket。Desktop 又增加本地主进程代理，不能把该路径混为普通浏览器请求。[部署与认证详细证据](../deepseek-web-runtime.md)

对我们而言，本地浏览器和 Docker 共享界面并不意味着只改监听地址就可安全部署。默认本地绑定、同源认证、反向代理路径、重连与密钥不下发浏览器仍要有独立验收。首次/高风险确认与已授权固定流程自动化继续按用户已确认范围设计，不复制上游所有权限默认值。

## 10. 对本方案的具体影响

| 借鉴点 | 本项目采用方式 | 保留的边界 |
| --- | --- | --- |
| 独立 Web Host | React + .NET Host 为首版；Electron 后加 | 不变更用户选定技术栈，不照搬 Node 插件内核 |
| 命令、实时流和持久历史分开 | HTTP/SSE + 权威快照/cursor；需要双向流时单独加 WS | 界面重连不创建新运行 |
| 日志驱动上下文、工具未知结果 | RunSnapshot/ContextManifest、Operation 与结果账本 | 未知副作用先核对，不能重跑全部历史 |
| 团队任务与子会话分层 | 长期责任人 + 临时执行身份 + 持久等待 | task claim 不等于执行租约，消息接纳不等于完成 |
| 外部记忆与上下文分开 | 内建记忆发布/版本/撤回，外部 MCP 作为受控来源 | 不以“接了 memory 工具”代替完整记忆治理 |
| 配置 UI、能力目录和高级覆盖 | 普通流程少字段，高级兼容项可视编辑 | 声明能力与实测结果分开，正在运行的快照不静默变化 |
| 插件生命周期与组合审计 | 版本固定、来源可见、能力缺失可解释 | Host 的恢复/审批屏障不可由插件禁用 |

落实位置为 [02 架构](../../02-architecture.md)、[04 协作](../../04-agent-harness-and-projects.md)、[05 扩展](../../05-models-tools-plugins.md)、[06 记忆](../../06-memory-and-knowledge.md)、[11 通信](../../11-frontend-runtime-protocol.md)、[13 恢复](../../13-restart-and-recovery.md)。这些是本项目设计判断，尚未实现。

## 11. 后续可复现但尚未执行的验证

1. 在隔离目录启动固定版本 Web，提交带工具的小任务；记录 HTTP、WS、日志水位和最终文件，断网再连只恢复观察。
2. 在工具前 flush、目标执行后和回执落盘后分别终止 Host；核对 interrupted closure、unknown 标记及是否发生重做。
3. 分别验证浏览器断线和 Host 重启时的待审批行为，不能将两者结果互相外推。
4. 显式启用 Teams，创建/认领/完成任务；在 claim 后重启、退出成员，检查 owner、活跃执行、inbox 和父任务是否继续。
5. 启用一个外部 MCP memory 配置，记录存储归属；新 Session 显式召回，再删除来源，观察是否存在自动撤回传播。
6. 用自定义模型 API 验证文本、图片、工具和流式取消；模型目录列出一个 ID 不计为这些能力通过。
7. 验证反向代理前缀、cookie、WS upgrade 和错误 authority；插件安装仅使用受控测试包，记录批准范围。

这些验证需单独获得环境与实际执行证据。本轮交付的是固定版本的源码研究及设计修订，不是第三方运行质量评测。
