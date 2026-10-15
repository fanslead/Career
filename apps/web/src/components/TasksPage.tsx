import type { ArtifactResponse, TaskResponse, WorkspaceResponse } from '../contracts.ts';
import type { PendingCommandMetadata } from '../state/commandTracker.ts';
import { taskStatusLabels } from '../statusLabels.ts';
import { TaskDetail } from './TaskDetail.tsx';

export function TasksPage({
  workspace,
  tasks,
  selectedTaskId,
  online,
  executionAvailable,
  commandBusy,
  recoveryRequestId,
  recoveryCommand,
  prepareCommandRecovery,
  runCommand,
  onSelectTask,
  onDownload,
}: {
  workspace: WorkspaceResponse;
  tasks: ReadonlyMap<string, TaskResponse>;
  selectedTaskId: string | null;
  online: boolean;
  executionAvailable: boolean;
  commandBusy: boolean;
  recoveryRequestId: string | null;
  recoveryCommand: PendingCommandMetadata | null;
  prepareCommandRecovery(key: string): void;
  runCommand<T>(key: string, route: string, makeBody: (requestId: string) => Record<string, unknown>, method?: string): Promise<{ ok: boolean; value?: T; error?: string }>;
  onSelectTask(id: string): void;
  onDownload(artifact: ArtifactResponse): Promise<void>;
}) {
  const selected = selectedTaskId ? tasks.get(selectedTaskId) : undefined;
  return (
    <section className="stack">
      <header className="page-heading"><div><p className="eyebrow">工作区 · {workspace.tasks.length} 项</p><h1>任务</h1><p className="lede">每个详情显示任务修订、计划、运行、审批和成果版本。</p></div></header>
      {workspace.tasks.length === 0 ? <div className="panel empty-state"><h2>这里还没有任务</h2><p>回到工作台，提交你的第一项任务。</p></div> : (
        <div className="tasks-layout">
          <nav className="panel task-picker" aria-label="选择任务">
            <h2 className="sr-only">任务列表</h2>
            {workspace.tasks.map((summary) => <button type="button" key={summary.id} className={`task-picker__item ${summary.id === selectedTaskId ? 'task-picker__item--active' : ''}`} onClick={() => onSelectTask(summary.id)}>
              <strong>{summary.title}</strong><span>{taskStatusLabels[summary.status] ?? summary.status} · v{summary.version}</span>
            </button>)}
          </nav>
          <div className="task-detail-column">
            {!selected && <div className="panel empty-state"><h2>正在读取任务</h2><p>加载完成后会显示最新进展。</p></div>}
            {selected && <TaskDetail key={selected.id} task={selected} online={online} executionAvailable={executionAvailable} commandBusy={commandBusy} recoveryRequestId={recoveryRequestId} recoveryCommand={recoveryCommand} prepareCommandRecovery={prepareCommandRecovery} runCommand={runCommand} onDownload={onDownload} />}
          </div>
        </div>
      )}
    </section>
  );
}
