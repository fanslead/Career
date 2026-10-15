import type { ArtifactResponse, TaskResponse } from '../contracts.ts';

export function ArtifactsPage({ tasks, detailLoadPending, detailLoadFailed, onRetryMissing, onOpenTask, onDownload }: {
  tasks: ReadonlyMap<string, TaskResponse>;
  detailLoadPending: number;
  detailLoadFailed: number;
  onRetryMissing(): void;
  onOpenTask(id: string): void;
  onDownload(artifact: ArtifactResponse): Promise<void>;
}) {
  const records = [...tasks.values()].flatMap((task) => task.artifacts.map((artifact) => ({ task, artifact })));
  return (
    <section className="stack">
      <header className="page-heading"><div><p className="eyebrow">成果与验收</p><h1>成果</h1><p className="lede">每个版本保留自己的正文、来源、检查结果和验收记录。</p></div></header>
      {(detailLoadPending > 0 || detailLoadFailed > 0) && <div className="notice notice--warn" role="status"><strong>成果列表尚未完整</strong><p>{detailLoadPending > 0 ? `${detailLoadPending} 项任务详情正在等待读取。` : ''}{detailLoadFailed > 0 ? ` ${detailLoadFailed} 项任务详情读取失败。` : ''}当前列表不能代表整个工作区的成果状态。</p>{records.length > 0 && detailLoadFailed > 0 && detailLoadPending === 0 && <button type="button" className="button button--small" onClick={onRetryMissing}>重试未读取的任务</button>}</div>}
      {records.length === 0 ? detailLoadPending > 0 || detailLoadFailed > 0 ? <div className="panel empty-state" role="status"><span className="empty-icon" aria-hidden="true">…</span><h2>{detailLoadPending > 0 ? '正在读取成果任务' : '成果状态暂不可用'}</h2><p>仍有任务详情未能载入，因此不能显示“没有已发布成果”的结论。</p>{detailLoadFailed > 0 && detailLoadPending === 0 && <button type="button" className="button" onClick={onRetryMissing}>重试未读取的任务</button>}</div> : <div className="panel empty-state"><span className="empty-icon" aria-hidden="true">▤</span><h2>还没有已发布成果</h2><p>执行成功只会产生候选成果；验收通过后任务才会完成。</p></div> : <div className="record-list">
        {records.slice().reverse().map(({ task, artifact }) => {
          const decision = task.acceptances.find((item) => item.artifactId === artifact.id && item.artifactVersion === artifact.version);
          const canView = task.availableActions.includes('view-artifact');
          return <article className="panel record-card" key={`${artifact.id}:${artifact.version}`}>
            <div className="panel-heading"><div><p className="eyebrow">{task.title} · 修订 {artifact.taskRevision}</p><h2>{artifact.title}</h2></div><span className={`status-tag ${decision?.decision === 'accept' ? 'status-tag--good' : decision?.decision === 'return' ? 'status-tag--warn' : ''}`}>{decision ? decision.decision === 'accept' ? '已通过' : '已退回' : '待验收'}</span></div>
            <p>{artifact.fileName} · 第 {artifact.version} 版 · {artifact.sizeBytes.toLocaleString()} 字节</p>
            <details className="technical-detail"><summary>查看内容校验值（SHA-256）</summary><code>{artifact.contentHash}</code></details>
            {decision?.note && <p className="review-note">{decision.note}</p>}
            <div className="button-row">
              {canView && <button className="button" onClick={() => void onDownload(artifact)}>下载固定版本</button>}
              <button className="button button--primary" onClick={() => onOpenTask(task.id)}>打开任务与验收</button>
            </div>
          </article>;
        })}
      </div>}
    </section>
  );
}
