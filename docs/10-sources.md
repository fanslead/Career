# 一手资料索引与证据边界

核查日期：2026-10-14。开源项目均以用户指定的仓库为入口，产品资料只用官方页面。没有把网页或仓库中的文字当作本项目已经实现、运行或通过测试的证据。

新增 [九产品深入比较](research/product-comparison.md)，包含七个仓库的固定提交、九份独立研究及对原方案的修正建议。本页原有概览链接仍保留供追溯。

## 1. Agent 与项目管理参考

| 项目 | 本轮提取的内容 | 详细证据 |
| --- | --- | --- |
| [OpenClaw](https://github.com/openclaw/openclaw) | 运行事件、模型协议、技能可见性、工具审批与隔离 | [Harness 研究](research/agent-harness-references.md) |
| [Codex](https://github.com/openai/codex) | 核心与 UI 边界、工具策略、会话记录、插件格式 | [Harness 研究](research/agent-harness-references.md) |
| [Pi](https://github.com/earendil-works/pi) | 可嵌入 loop、模型适配、渐进技能、会话分支 | [Harness 研究](research/agent-harness-references.md) |
| [Paperclip](https://github.com/paperclipai/paperclip) | 岗位汇报线、任务与预算、审批、运行器控制面 | [业务研究](research/business-knowledge-references.md) |
| [NextCoWork](https://github.com/AIDotNet/NextCoWork) | coding harness、工具审批、插件、Electron/远端通信 | [业务研究](research/business-knowledge-references.md)、[通信源码链](research/nextcowork-runtime-communication.md) |
| [WeKnora](https://github.com/Tencent/WeKnora) | 文档解析、分块、混合检索、知识管理状态 | [知识研究](research/business-knowledge-references.md) |
| [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) | 纯 Web/Host、插件组合、事件水位、未知工具结果、实验性 Teams 与外部 memory | [总研究](research/products/deepseek-harness.md)、[Web 源码链](research/deepseek-web-runtime.md)、[运行与恢复](research/deepseek-runtime-recovery.md) |

新增分项研究已固定七个开源仓库的查证 commit，并提供实现路径和行号：[OpenClaw](research/products/openclaw.md)、[Codex](research/products/codex.md)、[Pi](research/products/pi.md)、[Paperclip](research/products/paperclip.md)、[NextCoWork](research/products/nextcowork.md)、[WeKnora](research/products/weknora.md)、[DeepSeek Harness](research/products/deepseek-harness.md)。原概览中部分分支链接仍可能漂移。本文提议借鉴机制，未复制上游代码，未作商业使用的法律结论。

## 2. 腾讯产品体验参考

- [Marvis 产品文档](https://marvis.qq.com/docs/product-description)及 [Agent 体系](https://marvis.qq.com/docs/agent-system)：官方已描述一个主 Agent 与五个专项 Agent，以及执行进度和结果位置；这仍属产品声明，内部调度与恢复未实测。详见 [Marvis 研究](research/products/marvis.md)。
- [WorkBuddy 专家](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Expert-Center)、[项目](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Project)、[记忆](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Memory)：深入到角色、专家团、任务交接、共享资源和用户控制。详见 [WorkBuddy 研究](research/products/workbuddy.md)，不只依据首页。
- [WorkBuddy Managed Agents 使用说明](https://cloud.tencent.com/document/product/1831/138968)：官方文档描述 Agent、技能、连接器、凭据、自动化和运行记录。它的企业云托管模型与本项目的本地单所有者范围不同，不整套复制。

没有访问用户账号、安装这些产品、实际创建 Agent 或验证其运行能力。

## 3. .NET 与存储

- [MAF 文档](https://learn.microsoft.com/en-us/agent-framework/)与[仓库](https://github.com/microsoft/agent-framework)：具体工具循环、checkpoint、审批与 AOT 边界见 [专项核查](research/dotnet-maf-aot-evidence.md)。
- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)与[ASP.NET Core Native AOT](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)：核心发布约束与支持范围。
- [Microsoft.Data.Sqlite 的 SQLite 版本](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)：默认 bundle 与可替换原生库。
- [SQLite WAL](https://sqlite.org/wal.html)与[备份 API](https://sqlite.org/backup.html)：并发及一致性备份基础。

专项研究所列包版本是查证快照，实际工程采用通过实验的锁定版本，不根据本文永久追随 latest。

## 4. 模型、工具与插件协议

- [OpenAI Responses 迁移](https://developers.openai.com/api/docs/guides/migrate-to-responses)
- [Anthropic API](https://platform.claude.com/docs/en/api/overview)
- [Gemini OpenAI 兼容](https://ai.google.dev/gemini-api/docs/openai)
- [Grok 协议比较](https://docs.x.ai/developers/model-capabilities/text/comparison)
- [DeepSeek Responses](https://api-docs.deepseek.com/guides/responses_api/)
- [MiniMax OpenAI 接入](https://platform.minimax.cn/docs/api-reference/text-openai-api)
- [Kimi 接入说明](https://platform.kimi.com/docs/get-api-key)
- [Ollama 兼容范围](https://docs.ollama.com/api/openai-compatibility)
- [Agent Skills 格式](https://agentskills.io/specification)
- [MCP 2026-07-28 规范](https://modelcontextprotocol.io/specification/2026-07-28)与[C# SDK 传输](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html)
- [OpenAI 插件打包](https://developers.openai.com/plugins/build/plugins)

GLM 的目标接入包含在需求中，但本轮官方详情页面读取不充分，未作“已验证协议兼容”的结论。所有厂商均未使用真实凭据调用；文档支持只证明有设计路径，不证明本项目已经兼容。

## 5. 前端与桌面

[Electron 进程模型](https://www.electronjs.org/docs/latest/tutorial/process-model)说明 main/renderer/preload 分工；[安全指导](https://www.electronjs.org/docs/latest/tutorial/security)用于界定未来桌面桥接。没有据此认为 Electron 能替代 React 或保证 .NET 核心 AOT。

## 6. 如何阅读证据

“源码”表示阅读了实现路径；“官方声明”表示文档或 README 写明；“建议”是针对本项目作出的设计判断；“未验证”表示没有运行证明。对第三方仓库的静态核查与对本项目的发布验证必须分别记录。
