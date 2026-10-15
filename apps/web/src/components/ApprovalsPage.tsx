import type { TaskResponse } from '../contracts.ts';
import { toolNameLabel } from '../statusLabels.ts';

export function ApprovalsPage({ tasks, detailLoadPending, detailLoadFailed, onRetryMissing, onOpenTask }: {
  tasks: ReadonlyMap<string, TaskResponse>;
  detailLoadPending: number;
  detailLoadFailed: number;
  onRetryMissing(): void;
  onOpenTask(id: string): void;
}) {
  const pending = [...tasks.values()].flatMap((task) => task.approvals
    .filter((approval) => approval.status === 'Pending')
    .map((approval) => ({ task, approval })));
  return (
    <section className="stack">
      <header className="page-heading"><div><p className="eyebrow">固定内容审批</p><h1>审批</h1><p className="lede">审批只授权一份固定正文与目标。拒绝操作会保留已完成的工作。</p></div></header>
      {(detailLoadPending > 0 || detailLoadFailed > 0) && <div className="notice notice--warn" role="status"><strong>审批列表尚未完整</strong><p>{detailLoadPending > 0 ? `${detailLoadPending} 项任务详情正在等待读取。` : ''}{detailLoadFailed > 0 ? ` ${detailLoadFailed} 项任务详情读取失败。` : ''}当前列表不能代表整个工作区的审批状态。</p>{pending.length > 0 && detailLoadFailed > 0 && detailLoadPending === 0 && <button type="button" className="button button--small" onClick={onRetryMissing}>重试未读取的任务</button>}</div>}
      {pending.length === 0 ? detailLoadPending > 0 || detailLoadFailed > 0 ? <div className="panel empty-state" role="status"><span className="empty-icon" aria-hidden="true">…</span><h2>{detailLoadPending > 0 ? '正在读取审批任务' : '审批状态暂不可用'}</h2><p>仍有任务详情未能载入，因此不能显示“没有待处理审批”的结论。</p>{detailLoadFailed > 0 && detailLoadPending === 0 && <button type="button" className="button" onClick={onRetryMissing}>重试未读取的任务</button>}</div> : <div className="panel empty-state"><span className="empty-icon" aria-hidden="true">✓</span><h2>当前没有待处理审批</h2><p>待审批项目会同步到这里。</p></div> : (
        <div className="record-list">{pending.map(({ task, approval }) => <article className="panel record-card" key={approval.id}>
          <div className="panel-heading"><div><p className="eyebrow">{task.title}</p><h2>{toolNameLabel(approval.toolName)}</h2>{toolNameLabel(approval.toolName) !== approval.toolName && <details className="technical-detail"><summary>查看技术标识</summary><code>{approval.toolName}</code></details>}</div><span className="status-tag status-tag--warn">待你决定</span></div>
          <p>目标：{approval.target}</p><p className="muted">{approval.reason}</p>
          {task.availableActions.includes('view-approval') ? <pre className="fixed-content fixed-content--clamp">{approval.content}</pre> : <p className="notice notice--warn">此审批暂不可查看，请刷新工作区状态后重试。</p>}
          <button className="button button--primary" onClick={() => onOpenTask(task.id)}>查看任务并处理</button>
        </article>)}</div>
      )}
    </section>
  );
}
