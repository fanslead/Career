# WeKnora 产品与知识生命周期核查

核查日期：2026-10-14。固定上游提交：[`6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6`](https://github.com/Tencent/WeKnora/commit/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6)。源码链接固定到该提交并标注行号。本次只读检查源码、前端和仓库文档；未安装、运行、配置模型或连接外部服务。下文“源码确认”不等于本次端到端运行验证。

## 产品定位与证据边界

**官方 README 自述：** WeKnora 是知识问答/RAG 平台，并把 Agent、Wiki、工具箱和长期记忆列作产品能力。[README 功能概览](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/README.md#L41-L65)。以下基于代码核实其中可追踪的文档知识、检索引用与 memory 管理路径；项目自述中的工具数量、外部模型供应商兼容程度及实际答案质量没有在此验证。

## 用户流程：导入、处理、检索、引用和维护

1. **导入：** 文件上传时先把原文件写入知识库存储，再创建 `pending` 的 knowledge 记录，并把文档处理任务排入 Asynq；URL 导入也有独立入队路径。此处确认的是后端代码路径，不是浏览器上传体验。[文件记录与处理任务](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_create.go#L165-L244) [URL 导入任务](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_create.go#L418-L476)
2. **解析、切分、索引：** worker 解析文件并生成 chunks；chunk 会先写入数据库，即便当前知识库没有启用向量/关键词索引。若知识库配置了 embedding model，则再生成 embedding 并写入索引；父块保留作上下文，子/平铺块承担向量索引。之后设置可检索状态，图像等多模态和摘要等后处理可以继续异步运行。[持久化 chunks 与可选 indexing](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_process.go#L640-L808) [索引后状态和异步后处理](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_process.go#L214-L248) [任务继续分派](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_process.go#L810-L864)
3. **检索和聊天：** `HybridSearch` 将候选检索、融合、数量截断与结果后处理串起来；它允许检索共享相同 embedding 模型的多个 KB。聊天 pipeline 将来源 chunk 变成 model context，并登记 citation handles；请求级引用设置关闭时，public citations 不展开。[HybridSearch 与多 KB 范围](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledgebase_search.go#L117-L149) [聊天上下文与 citation registry](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/chat_pipeline/references.go#L13-L80)
4. **核对来源：** citation expansion 将合并后的检索结果映射回具体 child chunk ID 和内容；Web UI 的引用 drawer 显示文档标题/片段，允许展开片段或打开来源/知识库文档。[chunk 来源展开](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/chat_pipeline/citation_sources.go#L96-L167) [引用 drawer 和打开来源](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/components/ChatReferencesDrawer.vue#L74-L135)
5. **查看状态并维护文档：** 文档列表呈现 queued/stalled、processing、finalizing、failed、cancelled、completed 等状态；行操作包括查看 trace、重建解析、取消解析、移动目录/知识库、批量管理和删除。处理中的取消需确认；重建和删除也提供确认提示。[列表显示的处理状态](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/knowledge/components/DocumentListView.vue#L118-L179) [文档操作菜单](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/knowledge/components/DocumentActionMenu.vue#L60-L124)
6. **替换、重解析、删除：** 替换源文件会保留 knowledge ID 和关联信息，先保存新文件并更新记录，再触发重解析；清理或重新解析出错时有补偿和恢复旧源字段的路径。[文件替换顺序与失败补偿](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_replace.go#L17-L40) [替换执行路径](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_replace.go#L148-L213) 普通 reparse 会清除旧 chunks/索引等资源并排入新的处理任务；用户取消会标记 cancelled、终止或跳过后续任务。[重解析](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_process.go#L2791-L2867) [取消处理](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_process.go#L3179-L3225) 删除路径先标记 deleting，再清理 embedding/vector 内容与关联资源。[删除状态和 vector 清理](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledge_delete.go#L475-L560)

## 可配置模型、Agent 工具和独立的长期记忆

- **模型不是一个写死的全局常量：** KB 的设置界面分开选择 KnowledgeQA、Embedding 和可选 Wiki synthesis model；当已有 RAG 文件时，embedding 选择器被锁定，避免后续切换造成索引不一致。[KB 模型选择](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/knowledge/settings/KBModelConfig.vue#L8-L79) README 列举的 provider 数量是仓库自述，不是本次对各 provider 进行 API 调用的兼容性结论。[README 模型能力声明](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/README.md#L56-L65)
- **Agent 是可配置工具面的对话模式：** 内建工具目录含搜索/阅读知识库、列出文档、查图谱/数据库、读写 Wiki、数据分析等工具；Wiki 写、替换、重命名、删除明确标记为危险工具。内建列表没有暴露通用 delegate/spawn-agent tool；这只说明这个工具目录的内建面，不足以排除插件或产品其它位置扩展编排能力。[内建 agent 工具目录](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/agent/tools/definitions.go#L102-L160)
- **长期记忆不是知识库文档：** memory 有 profile、preference、fact、task、interest 等类型；stable profile/preferences 可进常驻 block，situational fact/task 按当前查询召回。后台提取出的推断项状态为 pending，未获用户确认前不注入 prompt；用户也可选择 `explicit_only` 写入策略。[Memory 状态、类型和写入策略](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/types/memory.go#L21-L65) Memory 依照服务端的 user/workspace scope 读取，管理界面提供状态 tabs 及逐条确认/拒绝；这是个人画像/偏好等短条目，不等同于可引用的长篇来源文档。[服务端 memory scope](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/memory/scope.go#L20-L42) [Memory 状态列表与确认/拒绝](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/settings/MemorySettings.vue#L154-L164) [逐条确认/拒绝操作](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/settings/MemorySettings.vue#L280-L293) [Memory API](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/api/memory.ts#L62-L110)

## 对 Career 可借鉴与不宜照搬

**可借鉴：**

1. 用用户可见的处理阶段、trace、失败/取消状态解释长文档何时可被检索；保留重解析与取消入口，减少“上传成功却不知道是否可用”的盲区。[状态展示和动作](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/knowledge/components/DocumentActionMenu.vue#L60-L94)
2. 引用落到准确的来源 chunk，允许用户从答案跳回原文或展开片段；回答可信度需要有可核验的来源路径，而不仅是一个文档标题。[来源 chunk 对齐](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/chat_pipeline/citation_sources.go#L96-L167) [引用 UI](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/components/ChatReferencesDrawer.vue#L100-L135)
3. 将“可检索的原始知识”和“需用户确认的短期/长期个人记忆”分开存取，并让 inferred memory 在确认前保持不生效。[推断 memory 的 pending 合同](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/types/memory.go#L44-L65)

**不宜照搬：**

1. 不要将项目 README 中的 Agent、Wiki、供应商数量或长期记忆描述视为已满足业务需求的证据；先对齐用户是否要回答带出处的档案问题，还是在自动生成/改写知识。
2. 不要把配置多个模型选择器或可选工具，误解成已跑通多模型/agent 协同。来源代码显示调用会依赖具体知识库的 embedding model 与用户配置；本次未联调任何模型、向量库或多 agent 任务。[KB embedding 配置与锁定](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/frontend/src/views/knowledge/settings/KBModelConfig.vue#L28-L58) [Hybrid search 多 KB 的模型边界](https://github.com/Tencent/WeKnora/blob/6c3730a6adfd9e77e3472c3f0e3d10fcd805bda6/internal/application/service/knowledgebase_search.go#L117-L149)

## 后续体验验收（本次未执行）

1. 用文本、PDF、URL 各导入一个文档，记录实际 parse/chunk/embed/index 状态、trace、可检索时间，以及格式错误/模型失败反馈。
2. 对已有文档分别重解析、改 chunk、替换源文件、取消解析和删除；核实确认提示、稳定 ID/引用、后台任务竞态及原文/索引/对象存储清理结果。
3. 提问一个可由单段回答的问题并从答案打开引用；确认所示片段、页码/定位与实际来源一致，并检查 citation 关闭时用户和模型分别看到什么。
4. 对 auto memory 创建一条明确事实和一条系统推断；在 Memory UI 确认/拒绝后重新对话，核对 scope 隔离和 prompt 注入时机。
5. 更换 Embedding 模型，观察现有文件的设置锁与索引迁移/重建行为；用实际供应商 endpoint 进行成功、鉴权失败和维度不匹配测试。

**未验证：** 本文没有启动服务或浏览器，没有实测 PDF/URL 解析、模型调用、检索质量、引用定位、向量库一致性、Memory UI 或后台删除的实际效果。代码和界面组件证明了声明路径存在，不证明部署配置有效或体验达标。
