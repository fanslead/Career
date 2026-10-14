# 存储、工作区与共享能力

更新：2026-10-14。依据用户澄清与本轮设计修订：配置、已发布记忆和知识使用文件；任务、对话、执行归属与恢复记录使用 SQLite 主存。详细语义分别由 [04](04-agent-harness-and-projects.md)、[06](06-memory-and-knowledge.md)、[13](13-restart-and-recovery.md) 定义。

## 1. 每类数据只有一个权威位置

| 数据 | 权威位置 | SQLite 中的角色 | 是否可由文件重建 |
| --- | --- | --- | --- |
| 工作区、模式、岗位、Agent 配置 | JSON + Markdown 文件 | 配置索引和生效版本缓存 | 是 |
| Skills、MCP 配置、插件安装包与绑定 | 对应目录文件 | 能力目录与连接状态 | 配置可重建，运行历史不可 |
| 需求、项目业务状态、任务、依赖及计划/范围决定 | `state/app.db` | 主存储 | **否** |
| 会话、消息、Run/Attempt、Operation、审批、等待与恢复案例 | `state/app.db` | 主存储 | **否** |
| 执行租约、预算账本、定时触发、通知队列 | `state/app.db` | 主存储 | **否** |
| 原始知识文档与资料清单 | `knowledge/` | 提取与检索索引 | 是，但可能需重新解析和付费嵌入 |
| 已发布的长期事实、岗位经验与经历摘要 | `.memory/` 或 Agent 私有目录中的清单及不可变版本 | 检索索引；发布操作有审计记录 | 从权威文件重建索引；不能从索引可靠还原权威内容 |
| 工作记忆和未发布记忆候选 | Run／会话状态、主数据库 | 主存储 | 否 |
| 记忆撤回/删除抑制、恢复时的禁用规则 | `state/app.db` 的控制与审计记录；备份包含对应导出水位 | 决定哪些内容禁止再次使用，不替代记忆正文 | 否；旧备份缺少较新删除记录时须禁用自动注入并审查 |
| 成果正文、附件 | `artifacts/` 内容文件 | 归属、版本、验收状态在主数据库 | 正文可找回，完整业务关联不可 |
| 全文／向量索引 | `state/index.db` | 可重建派生数据 | 是 |
| 凭据 | 本地加密凭据库；主密钥由系统凭据设施或部署密钥提供 | 只存引用，不存明文 | 必须单独安排恢复 |

本地文件系统包含 SQLite 数据库文件，因此数据整体仍可保存在工作区。不要把“本地”误读为“所有文件都可以删除后自动重建”。`app.db` 是必须备份的业务事实。

对话导出、任务 Markdown 报告和 JSONL 诊断导出都是导出物，不允许与数据库双向自动覆盖。第一版采用关系表加同事务事件记录，不实施完整事件溯源系统。

## 2. 建议目录

```text
workspace/
  workspace.json
  .models/
    providers/*.json
    embeddings/*.json
  .modes/{office,engineering,business}/mode.json
  .roles/<role-id>/role.json
  .roles/<role-id>/instructions.md
  .skills/<skill-id>/SKILL.md
  .mcp/<server-id>/server.json
  .plugins/<plugin-id>/<version>/...
  .plugins/installed.json
  .memory/
    facts/<memory-id>/{entry.json,versions/1.md,...}
    episodes/<memory-id>/{entry.json,versions/1.md,...}
    experience/<memory-id>/{entry.json,versions/1.md,...}
  .agents/<agent-id>/
    agent.json
    identity.md
    skills/<binding-id>/link.json
    mcp/<binding-id>/link.json
    plugins/<binding-id>/link.json
    memory/{facts,episodes,experience}/
  knowledge/<library-id>/
    library.json
    sources/<document-id>/<version>/...
  projects/<project-id>/
    workspace.json
    inputs/
    work/
  artifacts/<artifact-id>/<version>/...
  state/
    app.db
    index.db
    config-snapshots/<content-hash>/...
    checkpoints/<content-hash>/...
    staging/
    vault.enc
  exports/
  backups/
```

路径使用稳定 ID，显示名称存配置，重命名岗位或 Agent 不改 ID。目录名统一用 `.mcp`；导入 `mcp/` 或其他生态布局时转换，不同时维护两套权威目录。模式是小型模板包，不复制整套 `.agents` 和数据库。

记忆清单使用 JSON，正文版本为 Markdown；项目/岗位/工作区 scope 写在清单中，冷热也写为元数据，归档不搬目录。临时执行身份保存在 child Run 快照，不自动增加 `.agents` 目录。已确认的长期成员才有稳定配置目录。

`projects/<id>/workspace.json` 只描述工作目录、资料引用与局部配置；项目进度、任务与验收状态仍在数据库。研发模式可把外部 Git 仓库作为经授权的工作目录，不要求把仓库搬进系统目录。

## 3. 共享与独立配置

采用**显式引用**，默认不依赖操作系统 symlink。这样 Windows、压缩包、Docker 挂载和备份恢复有一致语义。

例如 Agent 的一个技能绑定：

```json
{
  "schemaVersion": 1,
  "target": "workspace://skills/research",
  "enabled": true,
  "versionPolicy": "workspace-current"
}
```

以上是本项目建议格式，不是 Agent Skills 或 Codex 的标准文件。

- 父目录存共享资源，Agent 私有目录可以保存独立资源或 `link.json`。
- 共享资源存在不等于所有 Agent 都可使用；必须有显式绑定，或由岗位模板创建绑定。
- 引用按稳定资源 ID 解析，禁止循环、越界和指向凭据目录。
- 同名不自动覆盖：UI 展示来源、版本及作用域，显式选择要启用的资源。
- 改动共享技能影响下次 Run；进行中的 Run 使用已固定版本。紧急停用和撤销授权即时生效。
- 插件及其导出的技能／MCP 工具必须统一计算权限；单独引用不能绕过插件停用。
- 删除共享资源前展示引用者，默认停用或软删除；保留历史运行所需的快照。
- 物理 symlink 可作为高级功能，但必须校验真实路径；没有 symlink 权限时仍能正常使用。

Agent 提议建立长期成员只进入经 Host 校验的配置操作，不让模型任意改 `.agents`。临时委派使用受限的 Run 身份，不创建长期配置；任务责任人与执行身份分开。能力不得超过当前任务授权。

## 4. 文件更新与数据库一致性

文件与 SQLite 不是一个原子事务。采用可恢复的操作记录，明确每类文件的权威性。

配置 UI 保存：校验 schema、比较原版本 → 同目录临时文件 → 持久化并原子替换 → 重读 hash → 更新目录索引和审计。索引失败时重扫文件；文件保存失败时不能告诉用户已完成。原子替换与持久化行为要在 Windows/macOS/Linux 验证。

外部编辑：监听器只负责发现变化，不作为可靠日志；启动与周期性扫描做补偿。新文件验证失败保留上次有效配置，界面提示“磁盘配置有错误，当前仍使用版本 X”。修复后再生效。

文件型成果：数据库先记录发布意图，生成同卷暂存文件并校验完整性，再原子发布不可变版本目录，最后由数据库登记成果与候选结果。已发布但未登记的文件按意图补记；只有确认无引用且不在恢复中的孤儿文件才能回收。不因文件出现就把任务标 Done，验收另行记录。

文件型记忆发布：数据库保存发布意图，先写不可变正文版本，再条件更新并原子发布 entry.json，重读 hash 后登记结果。重启按同一操作 ID 对账；索引只在发布确认后构建。撤回命令先保存抑制规则，使旧索引和旧 checkpoint 不能继续注入，再完成文件与派生数据清理。

所有关键写入带 `expectedVersion` 或内容 hash。两个窗口编辑同一配置发生冲突时展示差异，不静默覆盖。

## 5. SQLite 数据模型与并发

主要表组建议：

- 项目：`projects`、`requirements`、`tasks`、`task_revisions`、`task_decisions`、`task_dependencies`、`task_assignments`、`handoffs`、`acceptance_records`。
- 会话与执行：`conversations`、`messages`、`runs`、`run_attempts`、`run_steps`、`checkpoints`、`context_manifests`、`execution_events`。
- 调度与外部动作：`leases`、`resource_claims`、`operations`、`operation_receipts`、`approvals`、`waits`、`recovery_cases`、`outbox`、`notifications`、`webhook_receipts`、`schedule_occurrences`。
- 成果与预算：`artifacts`、`artifact_versions`、`usage_ledger`、`budget_reservations`。
- 配置与知识的运行信息：`config_versions`、`file_operations`、`ingestion_jobs`、`memory_candidates`、`memory_suppressions`。

这些是逻辑表组建议，不要求为每个名词建服务或仓储。关键约束包括：Task 进入 Ready 前落实一个当前责任人及 assignmentVersion，草稿可记录责任人缺项；默认一个主执行租约；每 Run/step/tool-slot 一个逻辑 Operation；同键不同载荷拒绝；计划/范围决定绑定 taskRevision 和固定提案 hash；每次验收绑定 taskRevision 与不可变成果版本；每个发布/提炼意图有去重键。

检索文本、向量及其索引版本存入 `index.db`。如果对话全文也放这里，原消息仍在 `app.db`，索引丢失不丢对话。

启用外键、WAL、合理 busy timeout；主数据库以 `synchronous=FULL` 为持久性基线，尤其在提交审批和外部操作意图时。可重建索引可独立评估较宽松设置。使用短写事务与受控写入队列，事务内不等待 LLM、网络、人工批准或文件解析。读取可以并发，不能把 WAL 误认为允许无限写并发。[SQLite WAL 说明](https://sqlite.org/wal.html)。

领取任务使用事务条件更新与版本检查，领取、Attempt、租约、预算预留和启动事件同时提交。Operation 派发意图必须先提交成功再执行；结果回执独立于 Worker 最终回复持久化。事件有单调序号供 UI 断线续传，重要状态与相应事件同事务；数据库写失败停止派发，不能只发 UI 成功事件。

## 6. 备份、迁移与恢复

第一版优先停写一致性备份：暂停接单与调度 → 将活跃写文件 Run 暂停到安全边界，冻结工作区写入 → 等待短事务结束和文件操作对账 → 使用 SQLite 备份机制生成一致快照 → 拷贝权威文件及配置版本 → 写备份 manifest 与校验和 → 恢复调度。无法暂停的外部写者须等待结束，不能声称快照一致。不能在运行中只拷贝 `app.db` 而遗漏 WAL。[SQLite Online Backup](https://sqlite.org/backup.html)。

密钥有两种恢复路径：可迁移的加密导出包，或在新机器重新填写密钥。系统钥匙串中的主密钥不能靠复制工作区自动恢复，UI 必须提前说明。

恢复时先校验版本与文件 hash，在新目录演练；原目录保留。schema 迁移前生成备份，迁移失败不半启动；不承诺新版数据库可直接被旧程序读写。

`index.db` 可单独删除重建；`app.db` 不提供“当缓存清理”的按钮。旧备份恢复后默认停止自动执行与外发，先核查备份之后可能发生的动作和更新的删除抑制记录；不能因备份里看不到回执就重复发送。若最新删除记录已不可得，先关闭记忆注入并提示审查，不能保证仅凭旧备份恢复所有后来的遗忘选择。
