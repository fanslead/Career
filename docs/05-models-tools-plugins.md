# 模型接入、技能、MCP 与插件

更新：2026-10-14。以下为接入方案和目标兼容范围，结合 DeepSeek Harness 研究补充配置与运行边界；尚未执行供应商真实调用测试，本轮不改代码。

## 1. 模型接入按协议组织

用户提到的 gork、ollma 在界面中使用供应商正式名称 Grok、Ollama。连接配置和具体模型分开：同一个供应商可配置多个 endpoint／账号，一个连接可登记多个模型。

基础协议适配：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Gemini 原生内容生成；Ollama 原生协议按功能需要补充。每个适配器接入 MEAI，交由 MAF 调用，不在业务层散落请求拼装。

| 供应商 | 建议接入路线 | 查证与限制 |
| --- | --- | --- |
| OpenAI | Responses 优先，Chat Completions 兼容 | 两者的请求、输出项和会话状态不同，不能只更换路径。[迁移文档](https://developers.openai.com/api/docs/guides/migrate-to-responses) |
| Claude | Anthropic Messages | 单独处理内容块、工具结果与流式事件。[API 文档](https://platform.claude.com/docs/en/api/overview) |
| Gemini | 原生适配，另提供 OpenAI 兼容选项 | 兼容入口存在，不据此保证所有原生能力等价。[兼容文档](https://ai.google.dev/gemini-api/docs/openai) |
| Grok | Responses 路线，按目标模型验证兼容接口 | 厂商文档区分 Responses 与 Chat Completions，不能给全模型统一能力。[协议比较](https://docs.x.ai/developers/model-capabilities/text/comparison) |
| GLM | 提供独立供应商预设，按具体 endpoint 选择协议 | 本轮未取得充分可读取的官方协议详情；实现时核对普通 API 与套餐 endpoint，不能标为已验证 |
| DeepSeek | OpenAI 兼容接入，Responses 独立适配验证 | 官方有 Responses 文档，模型与字段兼容范围需逐项测。[文档](https://api-docs.deepseek.com/guides/responses_api/) |
| MiniMax | OpenAI 兼容；其他路线按需求增加 | 多轮工具调用需保留供应商要求的协议字段。[文档](https://platform.minimax.cn/docs/api-reference/text-openai-api) |
| Kimi | OpenAI 兼容，可选其他官方兼容路线 | 官方入口列 Chat Completions、Responses 与 Anthropic 格式，不能仅凭格式名保证完全互换。[文档](https://platform.kimi.com/docs/get-api-key) |
| Ollama | 本地 OpenAI 兼容或原生 | 官方明确为 OpenAI API 子集；本地和云端能力需区分。[文档](https://docs.ollama.com/api/openai-compatibility) |
| 自定义 API | 四类目标协议 + Base URL/header/model 配置 | 完全自定义请求／响应格式需适配器插件；不承诺填写任意 URL 即可兼容 |

不在设计中写死“最新最佳模型”或 token 单价。版本与价格作为可更新目录；每次 Run 保存实际模型 ID、连接版本和价格估计依据。

## 2. 连接配置与能力检测

普通界面：服务商 → API 地址 → 密钥 → 选择或填写模型 → 测试连接。默认预填地址，模型列表获取失败仍允许手填。

高级设置折叠：协议、API 版本、附加 header、超时、代理、请求扩展参数和可用特性。不允许用户字段覆盖认证、受控工具集、预算上限等核心策略。

能力矩阵按“连接 + 模型 + 适配器版本”保存，至少区分：文本、流式、工具调用、多个工具、JSON 输出、图片输入、上下文大小、取消行为、用量返回与服务端状态保存。状态为已验证／声明支持／不支持／未知，显示最近检测时间。

“连接成功”只代表认证与基础请求成功；工具任务必须通过工具回合测试。不能把 schema 被接受等同于模型实际正确调用工具。

连接目录、用户声明和实际检测分别存储；一次模型列表查询不设置工具/图片能力为已验证。DeepSeek Harness 的内置目录、自定义 endpoint 探测和逐请求配置快照提供了具体参考，见 [深度研究](research/products/deepseek-harness.md)。本项目固定 Run 所用的模型/连接配置版本，编辑默认影响后续 Run；需要中途更换时先停在安全点，再建立关联的新 Run 和新配置快照接管；沿用已知成果/回执，不能静默改变待审批动作或重做已完成操作。撤权仍立即阻止新派发，旧快照不保留永久权限。

连接配置与密钥发布可能分两步完成；Host 读回两者状态，任一步失败显示待补齐并保留表单，不能只因配置文件保存成功就标为可用。运行中的密钥仅按 secretRef 解析，不返回浏览器明文。

保留规范化消息和必要的 provider opaque payload，用于同供应商工具回合重放；不同供应商切换时重新构建可兼容上下文。不能把 A 厂商响应 ID 当成 B 厂商的会话状态。用户本地历史不能只存远端 conversation ID。

温度、推理强度、输出上限等按模型能力决定是否发送，不能所有模型统一塞同一组参数。重试按错误类别：无副作用模型请求可有限退避；流式中断标记为中断 attempt，不拼接成一条完整成功回答；工具写操作按副作用账本恢复。

## 3. Embedding 独立设置

Embedding 与聊天模型分离，支持不同供应商和凭据。界面填写：名称、协议／地址、密钥、模型、维度（检测后确认）、批大小、最大输入长度、超时；高级项包含 query/document 前缀或 task type、归一化与维度缩减参数。

“测试”至少检查单条、多条、中文、向量维度、有限数值及返回顺序。非 OpenAI 格式通过专门适配器接入。可选 reranker 是另一种配置，不能把 Embedding 服务假设成重排服务。

更换模型、地址背后的模型版本、维度、前缀或分块规则，会创建新的索引代次并重建；不能混用不同向量空间。重建期间继续用旧索引及其匹配的 query embedding，完成后原子切换。见 [知识库设计](06-memory-and-knowledge.md)。

## 4. Skills、MCP、插件分工

| 类型 | 是什么 | 如何管理 |
| --- | --- | --- |
| Skill | 告诉 Agent 何时、如何做事的说明和资源包 | 读取 SKILL.md 元数据，按需载入正文；脚本须经过工具策略 |
| MCP | 与外部工具／数据服务交互的协议 | 配置连接、鉴权、能力发现、工具列表、超时与状态 |
| 插件 | 一次安装的一组技能、工具、模板或运行组件 | 版本、来源、依赖、权限声明、兼容性、升级与卸载 |

Agent Skills 有标准目录与 `SKILL.md` 格式，支持 scripts/references/assets；它不赋予脚本天然执行权。[Agent Skills 规范](https://agentskills.io/specification)。

技能渐进加载：目录只给名称和描述，使用时读取正文，相关附件再按需读取。技能中写“忽略审批”不能改变 Host 策略。生成的新技能先作为草稿，经过示例任务验证后再发布到共享库。

## 5. MCP 管理

首版支持 stdio 和 Streamable HTTP。固定 SDK 和经过测试的协议版本，清楚显示支持范围；较旧 SSE 仅在有实际需求时适配。当前官方规范已出现 2026-07-28 版本，不应照搬旧文档的初始化、通知或会话语义。[规范](https://modelcontextprotocol.io/specification/2026-07-28)、[C# SDK transports](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html)。

UI 分开显示“已配置”“可连接”“已发现工具”“已分配给成员”“需要登录”。启用流程展示实际工具与权限，不只让用户粘贴 JSON。

工具集合按 Agent 绑定和 Run 快照筛选；MCP 服务新增工具不能自动获得执行授权。工具 schema 变动触发重新验证。凭据按连接注入，不进入模型上下文。使用 namespace 避免工具同名。

处理断连、超时、取消、部分失败、输出过大和凭据过期；非幂等工具不可因网络错误自动重发。第三方工具描述、网页与返回内容作为不可信资料，不能覆盖系统策略。

## 6. 插件兼容分级

Codex 官方资料目前描述根 `plugin.json`、`skills/`、`mcp.json` 的可移植包，以及 `.codex-plugin/plugin.json` 兼容入口。OpenAI 专有设置可以在 `extensions.com.openai`；不能把旧 `.mcp.json` 仅改名就当成新格式。[官方打包说明](https://developers.openai.com/plugins/build/plugins)。

| 兼容级别 | 第一版处理 | 明确边界 |
| --- | --- | --- |
| Agent Skills | 导入、校验、绑定 | 外部脚本依赖与实际工具名仍需映射 |
| 可移植 Agent Plugins | 导入 manifest、skills、可支持的 MCP 配置 | 按固定 schema 版本验证；不支持的字段出报告 |
| Codex 兼容包 | 转换支持的元数据、技能和 MCP 组件 | 保留原包，展示转换结果；不得标为完全兼容 |
| 生命周期 hooks | 首版默认不执行，显示未启用原因 | 后续增加受控 runner 与明确语义映射 |
| Codex Apps／已注册云连接、专属 UI、OAuth 平台绑定 | 不能直接复用平台授权；需要本产品连接适配 | 本地包不包含其他平台的登录态与服务权限 |
| Pi／OpenClaw 自定义运行时代码 | 暂不直接运行 | 可借用技能和 MCP；TypeScript／宿主扩展 API 不自动兼容 |
| DeepSeek Cordis profile／插件 | 不直接作为本产品运行组件加载 | 可参考能力清单和受支持 Skills/MCP；profile、Host 插件、UI bundle 及其 API 需分别适配 |

安装流程：选择本地包或可信来源 → 校验路径与 manifest → 检查依赖和所需权限 → 展示兼容报告 → 安装固定版本 → 指定岗位／Agent → 测试。

资源包不能借压缩路径穿越写到工作区外。插件版本以 hash 固定；更新先在新目录安装，运行任务继续使用旧版本，除非旧版被紧急停用。卸载先断开绑定，成果与历史保留。

第一版不加载任意插件 React 代码到主页面。插件设置用声明式表单呈现，复杂 UI 后续采用独立隔离面板；进程插件和页面插件分别设计安全边界。

## 7. 文件、网页与终端执行

文件工具提供限定目录读取、查找、创建、补丁和差异预览；支持非文本成果的产生与校验。浏览器执行器使用独立会话，可读网页、下载资料、执行获准交互；保存登录态与站点授权范围。研发终端明确 shell、工作目录、超时、输出上限和进程树管理。

同一浏览器会话或可写目录一次只由一个执行者控制。需要并行浏览时创建独立会话；需要共同编辑时通过任务交接与集成，避免多个 Agent 互相覆盖。

普通子进程和 MCP 不自动形成安全沙箱。受信本地模式与隔离执行模式应在界面如实标注；不支持隔离的高风险插件不能显示为“已安全隔离”。

副作用执行统一经过 [13 恢复合同](13-restart-and-recovery.md)的 Operation 账本；工具插件声明的可重试必须由具体目标的幂等/查询能力验证。插件升级或运行器切换不能移除写前持久化、复用旧审批扩大范围，或自动重放未知动作。
