import type { TaskResponse, TaskSummaryResponse, WorkspaceResponse } from '../contracts.ts';
import { TaskComposer } from './TaskComposer.tsx';
import { taskStatusLabels } from '../statusLabels.ts';

export function Workbench({
  workspace,
  tasks,
  selectedTaskId,
  online,
  busy,
  error,
  recoveryRequestId,
  runCommand,
  onCreated,
  onSelectTask,
  onSettings,
}: {
  workspace: WorkspaceResponse;
  tasks: ReadonlyMap<string, TaskResponse>;
  selectedTaskId: string | null;
  online: boolean;
  busy: boolean;
  error: string;
  recoveryRequestId: string | null;
  runCommand<T>(key: string, route: string, makeBody: (requestId: string) => Record<string, unknown>, method?: string): Promise<{ ok: boolean; value?: T; error?: string }>;
  onCreated(task: TaskResponse): Promise<void>;
  onSelectTask(id: string): void;
  onSettings(): void;
}) {
  const summaries = workspace.tasks;
  const running = summaries.filter((task) => task.status === 'Running' || task.status === 'Queued').length;
  const waiting = summaries.reduce((count, task) => count + task.pendingApprovals + task.pendingArtifacts, 0);
  const latest = summaries.slice().sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)).slice(0, 5);

  return (
    <section className="stack">
      <header className="page-heading">
        <div><p className="eyebrow">{workspace.name}</p><h1>工作台</h1><p className="lede">查看真实任务状态，确认计划，处理待审批和待验收事项。</p></div>
        <span className={`connection-chip ${online ? 'connection-chip--online' : 'connection-chip--offline'}`}><span aria-hidden="true" />{online ? '已连接工作区' : '离线 · 仅可查看'}</span>
      </header>

      {workspace.intakePaused && <div className="notice notice--warn" role="status">工作区已暂停接收新任务，现有任务状态仍可查看。</div>}

      {!workspace.connection && (
        <div className="notice notice--info">
          <strong>先配置模型连接</strong>
          <p>空工作区不会使用示例模型或示例任务。你可以先交办任务并查看计划；执行前需在设置中保存连接并通过能力测试。</p>
          <button className="button" onClick={onSettings}>打开模型设置</button>
        </div>
      )}

      <div className="metric-grid" aria-label="工作区统计">
        <div className="metric-card"><span>任务总数</span><strong>{summaries.length}</strong><small>当前工作区任务</small></div>
        <div className="metric-card"><span>执行中</span><strong>{running}</strong><small>排队与运行中的任务</small></div>
        <div className="metric-card"><span>待处理</span><strong>{waiting}</strong><small>审批与成果验收</small></div>
      </div>

      <TaskComposer online={online} busy={busy} error={error} recoveryRequestId={recoveryRequestId} runCommand={runCommand} onCreated={onCreated} />

      <section className="panel">
        <div className="panel-heading"><div><p className="eyebrow">任务进展</p><h2>最近任务</h2></div></div>
        {latest.length === 0 ? (
          <div className="empty-state"><span className="empty-icon" aria-hidden="true">◇</span><h3>还没有任务</h3><p>从上方填写目标、输入资料和验收条目，创建第一项真实任务。</p></div>
        ) : <div className="task-list">
          {latest.map((summary: TaskSummaryResponse) => {
            const detail = tasks.get(summary.id);
            const selected = selectedTaskId === summary.id;
            return <button type="button" className={`task-row ${selected ? 'task-row--selected' : ''}`} key={summary.id} onClick={() => onSelectTask(summary.id)}>
              <span className="task-row__main"><strong>{summary.title}</strong><small>{summary.progress || detail?.goal || `修订 ${summary.revision} · ${summary.owner}`}</small></span>
              <span className="task-row__side"><span className="status-tag">{taskStatusLabels[summary.status] ?? summary.status}</span><small>{summary.pendingApprovals ? `${summary.pendingApprovals} 项待审批` : summary.pendingArtifacts ? `${summary.pendingArtifacts} 项待验收` : new Date(summary.updatedAt).toLocaleDateString()}</small></span>
            </button>;
          })}
        </div>}
      </section>
      <p className="phase-footnote">项目与团队管理、文件资料、知识检索和外部集成会在后续阶段开放。当前工作台只显示实际工作状态。</p>
    </section>
  );
}
