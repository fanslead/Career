# .NET 10 / MAF / SQLite / Native AOT 技术核查

核查日期：2026-10-14。范围仅为公开技术边界与最小验证门槛；未在本仓库编译或运行 PoC。

## 已核实的事实

### MAF 与 Microsoft.Extensions.AI

- NuGet 当前列出的 `Microsoft.Agents.AI` 是 **1.24.0**（2026-10-07 发布）；它兼容 `net10.0`，并依赖 `Microsoft.Extensions.AI` 10.10.x。单看目标框架兼容性不等同于 Native AOT 兼容性。版本可能继续变化，实施时应锁定实际验证过的包版本。[NuGet 包页](https://www.nuget.org/packages/Microsoft.Agents.AI/)
- `ChatClientAgent` 默认把聊天客户端包装为支持 function-calling 的调用管线；`Microsoft.Extensions.AI.FunctionInvokingChatClient` 在返回函数调用时执行 `AIFunction`、把结果交还模型并继续请求，直到无待执行调用或达到停止条件。该层默认最多 40 轮；单个响应里的多个调用默认串行，且可配置并发。传入原始聊天客户端的选项 `UseProvidedChatClientAsIs=true` 会跳过默认包装，因此此时需明确由谁负责循环。[MAF agent pipeline](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline)；[MEAI FunctionInvokingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient?view=net-10.0-pp)
- 常规工具可用 `AIFunctionFactory.Create` 从方法创建；其 schema 与参数绑定基于方法信息和 JSON 类型元数据。`SerializerOptions` 可传入应用的源生成 `JsonSerializerContext` 选项。仅给工具参数设置 `JsonSerializerContext` 不会自动补齐模型供应商客户端、会话、工作流 checkpoint 等其他序列化路径。[AIFunctionFactory 源码](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.Abstractions/Functions/AIFunctionFactory.cs)；[SerializerOptions API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactoryoptions.serializeroptions?view=net-10.0-pp)
- 对运行时 schema，MEAI 的 `AIFunction` 抽象允许自行提供描述和执行实现；MEAI 维护者也说明可直接派生 `AIFunction`，不必用 factory 或动态生成 C# 方法。可行的 AOT 形态是固定 dispatcher + `JsonElement`/预定义参数类型 + 从文件/MCP 描述构造的 schema。AOT **不能**在进程内动态加载任意托管插件程序集，也不能依赖 `Reflection.Emit` 生成执行代码；可插拔的外部实现需走进程/协议边界。[AIFunction 源码](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.Abstractions/Functions/AIFunction.cs)；[维护者答复](https://github.com/dotnet/extensions/discussions/6526)；[Native AOT 限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- 官方 .NET 工作流提供图式执行、fan-out/fan-in、事件和 superstep checkpoint。MAF 列出的多 Agent 模式包括 sequential、concurrent、handoff、group chat、Magentic；Magentic 从预先提供的 participant 集合里动态选择执行者，文档同时提醒其在原始 Magentic-One 设计之外尚未验证。它不等同于运行时加载任意新 agent/插件，也不是任务管理数据库。[Workflow 概念](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/)；[编排模式](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/)；[Magentic 说明与限制](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/magentic)
- 工具审批可用 `ApprovalRequiredAIFunction`，由调用方接收请求并回传批准/拒绝；`ToolApprovalAgent` 可将“始终允许”规则保存到 agent session。工作流 HITL 通过 request/response 暂停，checkpoint 会保存待处理请求，恢复时重新发出。[工具审批](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval)；[工作流 HITL](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop)
- 工作流 JSON checkpoint 可用 `CheckpointManager.CreateJson(ICheckpointStore<JsonElement>, JsonSerializerOptions)` 接自定义存储。公开 API 有文件系统 JSON store；未找到 MAF 内置 SQLite store。接 SQLite 需要实现 store，或自行持久化其 JSON envelope。MAF hosting 文档明确说没有通用的 durable agent-session store，宿主必须实现 `AgentSessionStore`。[CheckpointManager.CreateJson](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.checkpointmanager.createjson?view=agent-framework-dotnet-latest)；[Checkpoint store API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.workflows.checkpointing.icheckpointstore-1?view=agent-framework-dotnet-latest)；[Hosting session persistence](https://learn.microsoft.com/en-us/agent-framework/hosting/self-hosting/)

### AOT、ASP.NET Core 与序列化

- .NET 10 Native AOT 可按目标 OS/CPU 分别发布；官方限制包括没有运行时托管程序集动态加载、没有 `Reflection.Emit`，并且依赖裁剪。ASP.NET Core Native AOT 以 Minimal APIs 为主要 HTTP 入口，Minimal APIs 标为部分支持；MVC 与 Blazor Server 不支持，SignalR 部分支持。React 作为静态构建产物可由静态文件端点提供，但应把 SSE/流式响应和实际中间件纳入发布验证。[Native AOT 总览](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)；[ASP.NET Core AOT 支持矩阵](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)
- MAF 仓库的 `AotCheckpointing` 样例展示 declarative workflow checkpoint 的反射关闭路径：`DeclarativeWorkflowJsonOptions.Default` 为包内类型提供源生成元数据，应用自己的输入、结果及非原始审批参数须把应用的 resolver 加进去。**该样例没有设置 `PublishAot=true`**，只设置 `JsonSerializerIsReflectionEnabledByDefault=false`；样例还明确指出 Foundry 等传递依赖自己的 JSON 路径不在其保证范围内。因此它证明的是指定 checkpoint 类型路径的 source-gen 用法，不是全 MAF / 全 provider 的 AOT 认证。[样例 README](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/03-workflows/Declarative/AotCheckpointing/README.md)；[官方 AOT checkpoint 指引](https://learn.microsoft.com/en-us/agent-framework/workflows/declarative)
- 有一条历史 issue 报告 approval checkpoint round-trip 后 `ToolApprovalRequestContent.ToolCall` 退化为抽象基类，导致恢复后审批无法继续；issue 显示已关闭，但页面没有关联修复 PR 或修复说明。不能据关闭状态断言当前版本仍有或已无问题，应在锁定版本上实测。[MAF issue #5350](https://github.com/microsoft/agent-framework/issues/5350)
- ASP.NET AOT 使用 Minimal API 的 Request Delegate Generator 与 System.Text.Json 源生成元数据；官方要求发布时检查 warnings 并运行发布后的应用。没有警告也不能覆盖应用自定义动态路径的功能测试。[RDG 文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/aot/request-delegate-generator/rdg?view=aspnetcore-10.0)；[AOT 验证指导](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0)

### SQLite

- NuGet 当前列出的 `Microsoft.Data.Sqlite` 为 **10.0.13**；它经由 `SQLitePCLRaw` 调用原生 SQLite。默认 bundle 文档明确包含 FTS4、FTS5、JSON1、R*Tree，因此默认配置下 FTS5 是内置能力，正常无需 `LoadExtension`。若替换 native provider/SQLite 构建，这项能力须重新检查。[NuGet 包页](https://www.nuget.org/packages/Microsoft.Data.Sqlite/)；[自定义 SQLite 版本与 bundle](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)；[互操作层](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/interop)
- 没找到 Microsoft 对 `Microsoft.Data.Sqlite` 全部组合的 Native AOT 兼容承诺。其 managed facade 调原生 SQLite；AOT 发布是否带齐所选 RID 的 SQLite native asset、容器/单文件目录能否加载，必须实际 `publish` 并运行。`LoadExtension` 另依赖操作系统动态库加载与正确库路径；官方建议必要时处理 `PATH`、`LD_LIBRARY_PATH`、`DYLD_LIBRARY_PATH`。若目标是把 SQLite 静态链接进 AOT 可执行文件，.NET 支持通过 `DirectPInvoke` 和 `NativeLibrary` 显式配置，但默认 Microsoft.Data.Sqlite bundle 未承诺这一打包形态。[扩展加载限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/extensions)；[AOT 原生互操作/静态链接](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/interop)

## 技术结论与边界

- **可行性判断：** `.NET 10 + SQLite + MAF/MEAI + React` 可采用 AOT Core Host 搭配普通 .NET MAF Worker。Host 的 Minimal API、源生成 JSON、React 静态资源和 SQLite 原生依赖属于发布门槛；MAF Worker 的默认交付门槛是普通 .NET 运行时端到端验证，MAF/各 provider 是否能整体 Native AOT 是后续可选探索，不作为首版阻塞项。
- **不能当成现成保证的部分：** MAF 全依赖树 Native AOT；所有 provider 客户端和 SDK 的 JSON/AOT 兼容；工具运行时 schema 的供应商差异；graph checkpoint 内所有消息/审批多态类型的 AOT round-trip；SQLite native library 的单文件/静态链接；运行中加载任意 .NET 插件。必须由实发版验证。
- **推荐先定门槛：** AOT 是固定核心执行面；插件代码如需独立发版则以隔离子进程或 MCP 服务对接。声明式/JSON 插件 schema 可在核心 AOT 进程中通过固定 dispatcher 执行，但仍需对参数做白名单校验与 source-gen/`JsonElement` 处理。

## 最小可执行验证实验

交付门槛分为 Core Host AOT 发布、普通 .NET MAF Worker 端到端运行和 Docker 部署；MAF Worker 是否能 AOT 发布另列为可选探索。所有实验锁定并记录 MAF/MEAI/SQLite 包版本，避免把 JIT Worker 的通过误报成 MAF AOT 兼容。

1. **Core Host AOT Web 基线（必须）：** 建 `net10.0` Minimal API、源生成请求/响应 DTO、React build 静态文件端点；对 `win-x64`、`osx-arm64`、`linux-x64` 各执行 `dotnet publish -c Release -r <RID> -p:PublishAot=true`，运行 CRUD、SSE/流式响应和静态资源请求。门槛：处理所有 AOT warning，并验证发布产物行为。
2. **MAF/MEAI tool loop（必须，普通 .NET Worker）：** 用 fake `IChatClient` 按脚本先返回 2 次 `FunctionCallContent`，再返回 final；通过 `ChatClientAgent` 调工具，再用一个真实模型 provider 做烟雾验证。门槛：普通 .NET Worker 中参数绑定、调用次序/并行策略、结果回传、取消和迭代上限正确；不要求 Worker Native AOT。
3. **运行时 JSON Schema 工具（必须，普通 .NET Worker）：** 从临时 JSON 清单造工具 schema，经自定义 `AIFunction` 进入 agent；验证添加/撤销 schema 后下一轮工具列表生效，未知工具/字段/畸形参数被拒绝。插件运行时按选定的进程或 MCP 边界单独验证；本项不要求动态托管程序集能进入 AOT Host。
4. **审批与 checkpoint 恢复（必须，普通 .NET Worker）：** 将审批工具放入 workflow，触发待批准请求，写 checkpoint，退出进程；新进程重建 workflow、恢复 pending request、批准后完成工具执行。门槛：往返保留 `ToolApprovalRequestContent`、具体 `FunctionCallContent`、参数及 call id；未批准时工具副作用为零。
5. **普通图工作流与 SQLite checkpoint store（必须，普通 .NET Worker）：** 由 `WorkflowBuilder` 建含自定义 DTO 的 2+ superstep graph，使用 SQLite-backed `ICheckpointStore<JsonElement>`；完成一 superstep 后退出并恢复。门槛：状态、pending messages、事件顺序、旧 checkpoint 读取可复现。源生成序列化可作为裁剪/AOT 预备；Worker 普通 .NET 发布不要求关闭反射或 Native AOT。
6. **多 Agent 与进度事件（必须，普通 .NET Worker）：** 使用 fake model 验 sequential、concurrent、handoff、group chat；验证事件 participant 归属、失败/取消可观测及审批暂停可恢复。Magentic 的预定义 participants、轮数上限和 plan approval 作为单独扩展验证；其 API 实验性且文档提示原设计外尚未验证，不应成为普通核心路径的交付阻塞。
7. **SQLite/FTS5 与 Host AOT（必须）：** 对 Windows/macOS/Linux AOT Host 发布件打开真实磁盘 DB，插入任务/消息，执行事务与重启恢复，再运行 `CREATE VIRTUAL TABLE ... USING fts5(...)`、`MATCH` 和排名查询。门槛：各 RID 原生库从发布布局正确加载，FTS5 能力一致，WAL/并发写入和备份策略可用。
8. **Docker 端到端（必须）与 MAF AOT 探索（可选）：** 必须门槛是在 Docker 中运行 AOT Core Host + 普通 .NET MAF Worker，验证 UI/API、worker 通信、持久化和重启恢复；需要的非内置 SQLite 扩展另验证库路径、架构和权限。可选探索是在锁定版本下将 MAF/MEAI tool loop、动态 schema、approval/workflow checkpoint 分别尝试 `PublishAot=true`，记录 warnings 与失败边界；探索失败不阻止首版，只用于判断未来是否收敛 Worker 到 AOT。
