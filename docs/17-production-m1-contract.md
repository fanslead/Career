# M0/M1 实施合同

2026-10-15 · 首个本地闭环已实现，证据见 [18](18-production-m1-delivery.md)。范围沿用 14/15；单所有者、单执行槽的工作台 → 计划 → MAF 执行 → 固定工具审批 → 不可变成果 → 版本验收。真实模型测试依用户本轮选择延期；生产默认不装载示例任务或确定性模型。

## 本批工程与接口

正式工程在 `src/`，React 在 `apps/web/`。P0 保留。Host 是唯一状态和数据库写者；Worker 只调用已配置模型并提出发布请求。第一项受控工具为 `publish_document`：预览固定文件名与正文，批准才在本任务成果区原子发布 Markdown。拒绝当前动作保留已有工作，允许改稿；成果验收是另外的决定。

JSON 均为 camelCase。状态字段是下面的固定英文值，界面使用中文；消息和错误提供可采取的下一步。所有命令携带 UUID `requestId`；任务相关命令携带 `expectedVersion`。同键不同路由/载荷 409，相同键返回首次接纳结果。冲突返回 `current` 快照。

- `GET /api/bootstrap`，`POST /api/auth/pair {code}`，`POST /api/auth/refresh`，`POST /api/auth/logout`。访问令牌仅在内存；刷新凭据为 HttpOnly、SameSite=Strict Cookie，轮换和退出可撤销；本机首次配对码在 Host 启动输出，限时/限次。

`requiresPairing` 表示工作区需要所有者会话，不表示匿名 bootstrap 已判断浏览器是否登录；`hasOwner` 只表示工作区已有所有者。浏览器先尝试合法刷新，失败才显示配对入口。每次启动的新一次性配对码可用于重新连接，不能用 hasOwner 绕过认证。
- `GET /api/workspace` → `WorkspaceResponse`，`GET /api/tasks/{id}` → `TaskResponse`。快照包含 `snapshotSeq`。`GET /api/events?after={seq}` 使用带 bearer 的 fetch SSE；重连先取快照再续流，不能启动任务。
- `GET /api/connection`，`PUT /api/connection` → `ConnectionResponse`，`POST /api/connection/test {requestId}`。首条支持 OpenAI 兼容 Chat Completions，包含 DeepSeek；保存与能力测试分开，设置变化使旧测试失效。不回传密钥，不把密钥写入事件/日志。回环模型须单连接显式允许。
- `POST /api/tasks` → 201 `TaskResponse`。字段：requestId/title/goal/criteria[]/inputText/mode(office/development/business)/project?。无模型也能保存任务；计划与输入先固定。
- `PATCH /api/tasks/{id}`：requestId/expectedVersion/title/goal/criteria[]/inputText。只允许待确认、已暂停或失败任务编辑，形成新修订并使旧未处理许可失效；活跃 Run 不原地改快照。
- `POST /api/tasks/{id}/confirm-plan`：requestId/expectedVersion/taskRevision/proposalHash。模型未通过能力测试不能派发；返回草稿并给出设置入口。
- `POST /api/tasks/{id}/pause|resume|cancel|retry`：requestId/expectedVersion。请求暂停后先显示 Stopping，执行器停止确认后才 Paused；未确认计划不能 resume。失败重试沿用已有操作和成果，未决效果不能盲重发。
- `POST /api/approvals/{id}/decision`：requestId/expectedVersion/payloadHash/decision(approve|reject)/note?。批准固定正文、目标和版本，有效期 30 分钟，派发时重新校验。失效/已处理 409。
- `POST /api/tasks/{id}/acceptances`：requestId/expectedVersion/taskRevision/artifactId/artifactVersion/contentHash/decision(accept|return)/note?。通过必须对应当前修订及固定版本；退回至少一项修改意见，建立新 Run，旧成果/决定保留。
- `GET /api/artifacts/{id}/versions/{version}/download`：鉴权读取、附件 Markdown、固定安全文件名。`GET /api/commands/{requestId}` 查询不确定写入的接纳记录。
- `POST /api/workspace/pause-intake|resume-intake`：requestId。停止新派发与已运行任务控制分别处理。

## 状态和前端合同

Task：AwaitingPlan、Queued、Running、WaitingApproval、Stopping、Paused、Blocked、Failed、InReview、Done、Cancelled。Run：Queued、Running、WaitingApproval、Stopping、Paused、Reconciling、Failed、Succeeded、Cancelled。Operation：Proposed、Approved、Rejected、Executing、Succeeded、UnknownOutcome。审批 Accepted/Rejected 与成果验收相互独立。Task Done 必须有验收记录；Run Succeeded 只说明候选成果已交付。

前端独立保存选择、非敏感草稿、权威快照、提交中的稳定 requestId 和连接状态。不因导航改状态；A 的回包不能覆盖 B。失联/补齐时保留草稿和最后快照并禁写。409 展示固定提交依据、保留的输入和冲突版本，不自动重提；编辑计划须明确比较后再选用新基准。成功写入后读取权威状态；结果不明查询原 requestId。sessionStorage 仅存未决 requestId/key/route/method，绝不存正文、密钥或令牌；刷新后同 key 等待回执，不能换 ID 重提。手机关键操作至少 44px；关闭弹窗返回原按钮，按钮已消失时返回稳定 main 焦点。

Worker 使用上述 C# 类型，以 JSON-RPC 2.0 换行 JSON 通信：Host stdin 写一条 `agent.run` 请求（params 为 WorkerRequest），Worker 首先发 `worker.hello`，最后返回 id=run、result=WorkerResponse；函数发布通过 id=publish、method=tool.publish、params=WorkerToolRequest，Host 返回对应 ToolResult。每条含协议/Run/Attempt/epoch/fencing 身份，1MiB 上限。stdout 仅协议。每次模型请求前 Worker 发送 `model.reserve`（params 为 ModelCallRequest），等待 `modelResult` 的许可；完成后发送 `model.settle`，Host 保存迟到用量到原 callId。最大调用次数跨 Attempt 连续，价格未知单独显示，不报告零费用。MAF 的 session 与 ToolApprovalRequestContent 原样存检查点；批准后新 Attempt 续接，不另造操作 ID。

验证使用仅在 tests 下的本地模型协议服务。不会将测试服务或固定脚本作为生产默认。已通过的身份、幂等、旧版本、零效果、暂停/重启、SSE 和 UX 子项见 18；真实模型、完整发布故障/退回质量与后续 W11–W21 保留各自门槛，不提前宣称完成。
