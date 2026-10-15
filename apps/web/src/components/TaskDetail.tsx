import { useEffect, useState, type FormEvent } from 'react';
import type { AcceptanceResponse, ApprovalResponse, ArtifactResponse, TaskResponse } from '../contracts.ts';
import type { PendingCommandMetadata } from '../state/commandTracker.ts';
import { canRebaseDecision, type DecisionSnapshot } from '../state/decisionRebase.ts';
import { DecisionDialog } from './DecisionDialog.tsx';
import { planStatusLabels, statusLabels as workflowStatusLabels, toolNameLabel, usageStatusLabel } from '../statusLabels.ts';

const dateTime = (value: string) => new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });

type TaskEditDraft = Pick<TaskResponse, 'title' | 'goal' | 'inputText'> & { criteriaText: string };
type DecisionState = DecisionSnapshot;

const editDraftFrom = (task: TaskResponse): TaskEditDraft => ({
  title: task.title,
  goal: task.goal,
  criteriaText: task.criteria.join('\n'),
  inputText: task.inputText,
});

export function TaskDetail({
  task,
  online,
  executionAvailable,
  commandBusy,
  recoveryRequestId,
  recoveryCommand,
  prepareCommandRecovery,
  runCommand,
  onDownload,
}: {
  task: TaskResponse;
  online: boolean;
  executionAvailable: boolean;
  commandBusy: boolean;
  recoveryRequestId: string | null;
  recoveryCommand: PendingCommandMetadata | null;
  prepareCommandRecovery(key: string): void;
  runCommand<T>(key: string, route: string, makeBody: (requestId: string) => Record<string, unknown>, method?: string): Promise<{ ok: boolean; value?: T; error?: string; status?: number }>;
  onDownload(artifact: ArtifactResponse): Promise<void>;
}) {
  const [dialog, setDialog] = useState<DecisionState | null>(null);
  const [decisionConflict, setDecisionConflict] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);
  const [note, setNote] = useState('');
  const [noteDraftKey, setNoteDraftKey] = useState('');
  const [error, setError] = useState('');
  const [editingPlan, setEditingPlan] = useState(false);
  const [editDraft, setEditDraft] = useState<TaskEditDraft | null>(null);
  const [editBase, setEditBase] = useState<TaskResponse | null>(null);
  const [editConflictNeedsConfirmation, setEditConflictNeedsConfirmation] = useState(false);
  const [editError, setEditError] = useState('');
  const can = (action: string) => task.availableActions.includes(action as TaskResponse['availableActions'][number]);

  useEffect(() => {
    if (!recoveryRequestId) return;
    const routeApproval = task.approvals.find((approval) => {
      const route = `/api/approvals/${encodeURIComponent(approval.id)}/decision`;
      return route === recoveryCommand?.route && approval.status === 'Pending' && task.availableActions.includes('view-approval');
    });
    if (routeApproval) {
      openApproval(routeApproval, recoveryCommand?.key.endsWith(':reject') ? 'reject' : 'approve');
      return;
    }
    if (recoveryCommand?.route === `/api/tasks/${encodeURIComponent(task.id)}` && recoveryCommand.key === `task:${task.id}:edit-plan` && can('edit-plan')) {
      openPlanEditor();
      return;
    }
    if (recoveryCommand?.route === `/api/tasks/${encodeURIComponent(task.id)}/acceptances`) {
      const artifactId = recoveryCommand.key.split(':')[2];
      const artifact = task.artifacts.find((candidate) => candidate.id === artifactId);
      const decision = recoveryCommand.key.endsWith(':return') ? 'return' : 'accept';
      const latestReviewArtifact = task.artifacts.filter((candidate) => candidate.taskRevision === task.revision).slice().sort((left, right) => right.version - left.version)[0];
      const alreadyDecided = artifact && task.acceptances.some((acceptance) => acceptance.artifactId === artifact.id && acceptance.artifactVersion === artifact.version);
      if (artifact && task.status === 'InReview' && can(decision) && latestReviewArtifact?.id === artifact.id && !alreadyDecided) openAcceptance(artifact, decision);
    }
    // Recovery reconstructs a body only after the user opens the matching task.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [recoveryRequestId, recoveryCommand, task.id]);

  async function control(action: 'confirm-plan' | 'pause' | 'resume' | 'cancel' | 'retry') {
    setError('');
    const path = action === 'confirm-plan' ? 'confirm-plan' : action;
    const key = `task:${task.id}:${action}`;
    const result = await runCommand<TaskResponse>(key, `/api/tasks/${encodeURIComponent(task.id)}/${path}`, (requestId) => action === 'confirm-plan'
      ? { requestId, expectedVersion: task.version, taskRevision: task.revision, proposalHash: task.plan.proposalHash }
      : { requestId, expectedVersion: task.version });
    if (!result.ok && result.error) setError(result.error);
  }

  function openPlanEditor() {
    if (!editDraft) {
      setEditDraft(editDraftFrom(task));
      setEditBase(task);
    }
    setEditError('');
    setEditingPlan(true);
  }

  function updateEditDraft<K extends keyof TaskEditDraft>(key: K, value: TaskEditDraft[K]) {
    setEditError('');
    setEditDraft((current) => current ? { ...current, [key]: value } : current);
  }

  async function savePlanEdit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!editDraft) return;
    const criteria = editDraft.criteriaText.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    if (!editDraft.title.trim() || !editDraft.goal.trim()) {
      setEditError('请填写任务名称和目标要求。');
      return;
    }
    if (criteria.length === 0) {
      setEditError('至少填写一条可检查的验收条目。');
      return;
    }
    setEditError('');
    const result = await runCommand<TaskResponse>(`task:${task.id}:edit-plan`, `/api/tasks/${encodeURIComponent(task.id)}`, (requestId) => ({
      requestId,
      expectedVersion: editBase?.version ?? task.version,
      title: editDraft.title.trim(),
      goal: editDraft.goal.trim(),
      criteria,
      inputText: editDraft.inputText,
    }), 'PATCH');
    if (result.ok) {
      setEditingPlan(false);
      setEditDraft(null);
      setEditBase(null);
      setEditConflictNeedsConfirmation(false);
    } else if (result.error) {
      setEditError(result.error);
      if (result.status === 409) setEditConflictNeedsConfirmation(true);
    }
  }

  function openApproval(item: ApprovalResponse, decision: 'approve' | 'reject') {
    const key = `approval:${item.id}:${decision}`;
    if (noteDraftKey !== key) { setNote(''); setNoteDraftKey(key); }
    setError(''); setDecisionConflict(false); setDialog({ kind: 'approval', item, decision, taskVersion: task.version, taskRevision: task.revision });
  }

  function openAcceptance(item: ArtifactResponse, decision: 'accept' | 'return') {
    const key = `acceptance:${task.id}:${item.id}:${decision}`;
    if (noteDraftKey !== key) { setNote(''); setNoteDraftKey(key); }
    setError(''); setDecisionConflict(false); setDialog({ kind: 'acceptance', item, decision, taskVersion: task.version, taskRevision: task.revision });
  }

  async function submitDecision() {
    if (!dialog) return;
    if (dialog.kind === 'acceptance' && dialog.decision === 'return' && !note.trim()) {
      setError('退回时请写明至少一项修改意见。');
      return;
    }
    setError('');
    const isApproval = dialog.kind === 'approval';
    const item = dialog.item;
    const route = isApproval ? `/api/approvals/${encodeURIComponent(item.id)}/decision` : `/api/tasks/${encodeURIComponent(task.id)}/acceptances`;
    const key = isApproval ? `approval:${item.id}:${dialog.decision}` : `acceptance:${task.id}:${item.id}:${dialog.decision}`;
    const result = await runCommand<unknown>(key, route, (requestId) => isApproval
      ? { requestId, expectedVersion: dialog.taskVersion, payloadHash: (item as ApprovalResponse).payloadHash, decision: dialog.decision, note: note.trim() || null }
      : {
          requestId, expectedVersion: dialog.taskVersion, taskRevision: dialog.taskRevision,
          artifactId: (item as ArtifactResponse).id, artifactVersion: (item as ArtifactResponse).version,
          contentHash: (item as ArtifactResponse).contentHash, decision: dialog.decision, note: note.trim() || null,
        });
    if (result.ok) setDialog(null);
    else if (result.error) {
      setError(result.error);
      if (result.status === 409) setDecisionConflict(true);
    }
  }

  const pendingApprovals = task.approvals.filter((approval) => approval.status === 'Pending');
  const acceptedFor = (artifact: ArtifactResponse): AcceptanceResponse | undefined => task.acceptances.find((acceptance) => acceptance.artifactId === artifact.id && acceptance.artifactVersion === artifact.version);
  const currentReviewArtifact = task.artifacts.filter((artifact) => artifact.taskRevision === task.revision).slice().sort((left, right) => right.version - left.version)[0];
  const currentApprovalForDecision = dialog?.kind === 'approval' ? task.approvals.find((approval) => approval.id === dialog.item.id) : undefined;
  const hasEditVersionConflict = Boolean(editBase && task.version !== editBase.version);
  const decisionNeedsComparison = Boolean(dialog && (decisionConflict || dialog.taskVersion !== task.version || dialog.taskRevision !== task.revision));
  const decisionCanRebase = Boolean(dialog && canRebaseDecision(dialog, task));
  const decisionDisabledReason = !dialog ? undefined
    : decisionNeedsComparison ? '任务版本已变化。先比较固定审批或成果内容，再明确选择是否使用当前版本。备注草稿已保留。'
      : !online ? '工作区离线或正在同步，确认已暂停。你的备注会保留；连接恢复后可继续。'
      : ((dialog.kind === 'approval' && dialog.decision === 'approve') || (dialog.kind === 'acceptance' && dialog.decision === 'return')) && !executionAvailable
        ? '工作区尚未确认执行器恢复，暂不能提交会启动或继续执行的决定。你的备注会保留；恢复完成后可继续。'
        : undefined;

  function rebaseDecisionToCurrentTask() {
    if (!dialog || !canRebaseDecision(dialog, task)) return;
    const key = dialog.kind === 'approval'
      ? `approval:${dialog.item.id}:${dialog.decision}`
      : `acceptance:${task.id}:${dialog.item.id}:${dialog.decision}`;
    prepareCommandRecovery(key);
    setDialog({ ...dialog, taskVersion: task.version, taskRevision: task.revision });
    setDecisionConflict(false);
    setError('');
  }

  return (
    <article className="task-detail">
      <header className="page-heading page-heading--task">
        <div className="task-title-wrap">
          <p className="eyebrow">任务 · 修订 {task.revision} · 版本 {task.version}</p>
          <h1>{task.title}</h1>
          <p className="lede">{task.goal}</p>
        </div>
        <span className={`status-tag ${task.status === 'Done' ? 'status-tag--good' : task.status === 'Failed' || task.status === 'Blocked' ? 'status-tag--warn' : 'status-tag--pending'}`}>
          {workflowStatusLabels[task.status] ?? task.status}
        </span>
      </header>

      {error && <div className="notice notice--error" role="alert">{error}</div>}
      {recoveryRequestId && recoveryCommand && <div className="notice notice--warn" role="status"><strong>原命令正在等待你重填并确认</strong><p>浏览器只保存了原 requestId、路径和方法（{recoveryCommand.method} {recoveryCommand.route}），没有保存命令正文或备注：<code>{recoveryRequestId}</code></p><p>{recoveryCommand.route.includes('/approvals/') ? '请重新核对这份固定审批，并重新填写备注。' : recoveryCommand.route.endsWith('/acceptances') ? '请重新核对当前成果版本，并重新填写备注。' : recoveryCommand.key.endsWith(':edit-plan') ? '请检查或重新填写任务范围与输入资料。' : '请重新核对当前任务状态和操作影响后再提交。'}</p></div>}

      <section className="panel">
        <div className="panel-heading">
          <div><p className="eyebrow">范围固定</p><h2>执行计划</h2></div>
          <span className="status-tag">{planStatusLabels[task.plan.status] ?? task.plan.status}</span>
        </div>
        <p className="scope-copy">{task.plan.scopeDescription || '暂时没有计划说明。'}</p>
        {can('edit-plan') && !editingPlan && <div className="button-row"><button className="button" disabled={!online || commandBusy} onClick={openPlanEditor}>{editDraft ? '继续编辑未提交修改' : '编辑任务范围'}</button></div>}
        {task.status === 'Running' && !can('edit-plan') && <p className="notice notice--info">任务执行中时不能修改固定输入。请先请求暂停；任务状态变为“已暂停”后即可编辑。</p>}
        {editingPlan && editDraft && (
          <form className="task-edit-form" onSubmit={(event) => void savePlanEdit(event)}>
            <div className="panel-heading"><div><p className="eyebrow">生成新修订</p><h3>编辑任务范围</h3></div><span className="status-tag">提交时版本 v{editBase?.version ?? task.version}</span></div>
            {editBase && task.version !== editBase.version && <details className="conflict-comparison" open={editConflictNeedsConfirmation}>
              <summary>版本已变化 · 比较开始编辑时、当前任务版本与本地修改</summary>
              <div className="edit-conflict-grid">
                <section><strong>开始编辑时 · v{editBase.version}</strong><p>{editBase.title}</p><small>{editBase.status} · 修订 {editBase.revision}</small><pre>{editBase.goal}{'\n\n验收条目：\n'}{editBase.criteria.join('\n')}{'\n\n输入资料：\n'}{editBase.inputText || '（空）'}</pre></section>
                <section><strong>当前任务 · v{task.version}</strong><p>{task.title}</p><small>{workflowStatusLabels[task.status] ?? task.status} · 修订 {task.revision}</small><pre>{task.goal}{'\n\n验收条目：\n'}{task.criteria.join('\n')}{'\n\n输入资料：\n'}{task.inputText || '（空）'}</pre></section>
                <section><strong>你的未提交修改</strong><p>{editDraft.title || '（未填写名称）'}</p><pre>{editDraft.goal}{'\n\n验收条目：\n'}{editDraft.criteriaText}{'\n\n输入资料：\n'}{editDraft.inputText || '（空）'}</pre></section>
              </div>
            </details>}
            {hasEditVersionConflict && <div className="notice notice--warn" role="status"><p>当前任务版本已变化。比较左右版本与未提交修改后，才能选择是否按当前版本再次提交。</p><button type="button" className="button button--small" disabled={commandBusy} onClick={() => { prepareCommandRecovery(`task:${task.id}:edit-plan`); setEditBase(task); setEditConflictNeedsConfirmation(false); setEditError(''); }}>我已比较，以当前版本重新提交</button></div>}
            <div className="form-grid">
              <label className="field field--wide"><span>任务名称</span><input value={editDraft.title} onChange={(event) => updateEditDraft('title', event.currentTarget.value)} required maxLength={160} /></label>
              <label className="field field--wide"><span>目标与要求</span><textarea value={editDraft.goal} onChange={(event) => updateEditDraft('goal', event.currentTarget.value)} required maxLength={4000} rows={4} /></label>
              <div className="field field--wide"><label htmlFor="task-edit-criteria">验收条目</label><textarea id="task-edit-criteria" aria-describedby="task-edit-criteria-hint" value={editDraft.criteriaText} onChange={(event) => updateEditDraft('criteriaText', event.currentTarget.value)} required rows={4} /><small id="task-edit-criteria-hint" className="hint">每行一项</small></div>
              <div className="field field--wide"><label htmlFor="task-edit-input-text">输入资料文本</label><textarea id="task-edit-input-text" aria-describedby="task-edit-input-text-hint" value={editDraft.inputText} onChange={(event) => updateEditDraft('inputText', event.currentTarget.value)} maxLength={100000} rows={6} /><small id="task-edit-input-text-hint" className="hint">{editDraft.inputText.length.toLocaleString()} 字符 · 保存会创建新修订</small></div>
            </div>
            {editError && <p className="form-error" role="alert">{editError}</p>}
            {!can('edit-plan') && <p className="notice notice--warn">任务状态已变化，目前不能编辑。你的输入仍保留；请按最新状态处理任务。</p>}
            <div className="button-row button-row--end"><p className="hint">若版本冲突，当前输入会保留。成功保存后请重新检查并确认新计划。</p><button type="button" className="button" disabled={commandBusy} onClick={() => setEditingPlan(false)}>暂存草稿并返回</button><button className="button button--primary" disabled={!online || commandBusy || !can('edit-plan') || hasEditVersionConflict}>{commandBusy ? '正在保存…' : hasEditVersionConflict ? '比较并确认后才能保存' : online ? '保存为新修订' : '离线时不能保存'}</button></div>
          </form>
        )}
        <dl className="meta-grid">
          <div><dt>输入资料</dt><dd>{task.inputText ? `${task.inputText.length.toLocaleString()} 字符` : '未提供文本资料'}</dd></div>
          <div><dt>验收条目</dt><dd>{task.criteria.length} 项</dd></div>
          <div><dt>执行方式</dt><dd>{task.mode === 'development' ? '研发' : task.mode === 'business' ? '经营' : '办公'}</dd></div>
        </dl>
        <ul className="criteria-list">{task.criteria.map((criterion, index) => <li key={`${index}-${criterion}`}><span aria-hidden="true">{index + 1}</span>{criterion}</li>)}</ul>
        {task.inputText && <details className="input-review"><summary>查看固定输入资料</summary><pre>{task.inputText}</pre></details>}
        {task.status === 'AwaitingPlan' && (
          <div className="button-row button-row--end">
            <p className="hint">确认后才会开始执行。若模型尚未通过能力测试，请先完成连接设置。</p>
            {can('confirm-plan') && <button className="button button--primary" disabled={!online || commandBusy || !executionAvailable} onClick={() => void control('confirm-plan')}>{executionAvailable ? '确认计划并开始' : '等待执行器恢复'}</button>}
          </div>
        )}
      </section>

      <section className="panel">
        <div className="panel-heading"><div><p className="eyebrow">实际执行状态</p><h2>执行与恢复</h2></div><span>{task.runs.length} 次执行</span></div>
        {task.runs.length === 0 ? <p className="muted">计划尚未确认，因此还没有执行记录。</p> : (
          <ol className="run-list">
            {task.runs.map((run) => <li key={run.id}>
              <div className="run-list__top"><strong>{workflowStatusLabels[run.status] ?? run.status}</strong><time>{dateTime(run.createdAt)}</time></div>
              <p>{run.progress || run.failureMessage || `第 ${run.attemptNumber || 1} 次尝试`}</p>
              {run.failureCode && <small className="muted">错误代码：{run.failureCode}</small>}
              <small className="muted">模型调用 {run.modelCalls} 次 · {usageStatusLabel(run.usageState)}</small>
            </li>)}
          </ol>
        )}
        <div className="button-row">
          {can('pause') && <button className="button" disabled={!online || commandBusy} onClick={() => void control('pause')}>请求暂停</button>}
          {can('resume') && <button className="button button--primary" disabled={!online || commandBusy || !executionAvailable} onClick={() => void control('resume')}>{executionAvailable ? '继续执行' : '等待执行器恢复'}</button>}
          {can('retry') && <button className="button" disabled={!online || commandBusy || !executionAvailable} onClick={() => void control('retry')}>{executionAvailable ? '按原计划重试' : '等待执行器恢复'}</button>}
          {can('cancel') && <button className="button button--danger" disabled={!online || commandBusy} onClick={() => { setError(''); setCancelOpen(true); }}>取消任务</button>}
          {!task.availableActions.length && <span className="hint">当前没有可用操作。重新连接后会读取最新状态。</span>}
        </div>
      </section>

      <section className="panel" aria-labelledby="approvals-title">
        <div className="panel-heading"><div><p className="eyebrow">固定正文与目标</p><h2 id="approvals-title">工具审批</h2></div><span className="count-pill">{pendingApprovals.length} 待处理</span></div>
        {pendingApprovals.length === 0 ? <p className="muted">没有待处理的工具操作。</p> : !can('view-approval') ? <p className="notice notice--warn">当前无法显示审批正文，请刷新后重试。</p> : pendingApprovals.map((approval) => (
          <div className="approval-review" key={approval.id}>
            <div className="approval-review__heading"><div><h3>{toolNameLabel(approval.toolName)}</h3>{toolNameLabel(approval.toolName) !== approval.toolName && <details className="technical-detail"><summary>查看技术标识</summary><code>{approval.toolName}</code></details>}<p>目标：{approval.target}</p></div><span className="status-tag status-tag--warn">待你审批</span></div>
            <p>{approval.reason}</p>
            <pre className="fixed-content">{approval.content}</pre>
            <dl className="meta-grid"><div><dt>有效至</dt><dd>{dateTime(approval.expiresAt)}</dd></div></dl>
            <details className="technical-detail"><summary>查看固定内容校验值（SHA-256）</summary><code>{approval.payloadHash}</code></details>
            <div className="button-row">
              <button className="button button--primary" disabled={!online || commandBusy} onClick={() => openApproval(approval, 'approve')}>批准这份正文</button>
              <button className="button button--danger" disabled={!online || commandBusy} onClick={() => openApproval(approval, 'reject')}>拒绝本次操作</button>
            </div>
          </div>
        ))}
      </section>

      <section className="panel" aria-labelledby="artifacts-title">
        <div className="panel-heading"><div><p className="eyebrow">不可变版本</p><h2 id="artifacts-title">成果与验收</h2></div><span>{task.artifacts.length} 个版本</span></div>
        {task.artifacts.length === 0 ? <p className="muted">执行器尚未发布成果。</p> : task.artifacts.slice().reverse().map((artifact) => {
          const acceptance = acceptedFor(artifact);
          return <div className="artifact-review" key={`${artifact.id}:${artifact.version}`}>
            <div className="artifact-review__heading"><div><h3>{artifact.title}</h3><p>{artifact.fileName} · v{artifact.version} · {artifact.sizeBytes.toLocaleString()} 字节</p></div>
              <span className={`status-tag ${acceptance?.decision === 'accept' ? 'status-tag--good' : acceptance?.decision === 'return' ? 'status-tag--warn' : 'status-tag--pending'}`}>
                {acceptance ? acceptance.decision === 'accept' ? '已验收' : '已退回' : '待验收'}
              </span>
            </div>
            <details className="technical-detail"><summary>查看内容校验值（SHA-256）</summary><code>{artifact.contentHash}</code></details>
            <div className="button-row">
              {can('view-artifact') && <button className="button" disabled={!online} onClick={() => void onDownload(artifact)}>下载固定版本</button>}
              {can('accept') && task.status === 'InReview' && currentReviewArtifact?.id === artifact.id && !acceptance && <button className="button button--primary" disabled={!online || commandBusy} onClick={() => openAcceptance(artifact, 'accept')}>通过验收</button>}
              {can('return') && task.status === 'InReview' && currentReviewArtifact?.id === artifact.id && !acceptance && <button className="button button--danger" disabled={!online || commandBusy} onClick={() => openAcceptance(artifact, 'return')}>退回并写明修改项</button>}
            </div>
            {can('view-artifact') && <details className="artifact-reader"><summary>阅读成果正文</summary><pre>{artifact.content}</pre></details>}
            {artifact.checks.length > 0 && <div><strong className="subheading">已完成检查</strong><ul className="plain-list">{artifact.checks.map((item) => <li key={item}>{item}</li>)}</ul></div>}
            {artifact.unverified.length > 0 && <div className="notice notice--warn"><strong>尚未验证</strong><ul>{artifact.unverified.map((item) => <li key={item}>{item}</li>)}</ul></div>}
            {acceptance?.note && <p className="review-note">{acceptance.note}</p>}
          </div>;
        })}
      </section>

      <section className="panel">
        <div className="panel-heading"><div><p className="eyebrow">已提交事件</p><h2>活动记录</h2></div></div>
        {task.activity.length === 0 ? <p className="muted">目前没有任务活动记录。</p> : <ol className="activity-list">{task.activity.slice().reverse().map((item) => <li key={item.id}><time>{dateTime(item.occurredAt)}</time><p>{item.message}</p></li>)}</ol>}
      </section>

      <DecisionDialog
        open={dialog !== null}
        title={dialog?.kind === 'approval' ? dialog.decision === 'approve' ? '批准固定工具操作' : '拒绝本次工具操作' : dialog?.decision === 'accept' ? '验收此成果版本' : '退回此成果版本'}
        description={dialog?.kind === 'approval'
          ? '决定只绑定当前显示的工具名称、目标、固定正文与固定内容校验值。新内容需要重新审批。'
          : '验收决定绑定当前任务修订、成果版本和内容校验值。'}
        primaryLabel={dialog?.kind === 'approval' ? dialog.decision === 'approve' ? '确认批准' : '确认拒绝' : dialog?.decision === 'accept' ? '确认通过' : '确认退回'}
        danger={dialog?.decision === 'reject' || dialog?.decision === 'return'}
        busy={commandBusy}
        disabled={Boolean(decisionDisabledReason)}
        disabledReason={decisionDisabledReason}
        onConfirm={() => void submitDecision()}
        onClose={() => { if (!commandBusy) setDialog(null); }}
      >
        {decisionNeedsComparison && dialog && <section className="notice notice--warn" role="status">
          <strong>版本冲突 · 尚未提交决定</strong>
          <p>对话框打开时任务为 v{dialog.taskVersion} / 修订 {dialog.taskRevision}；当前快照为 v{task.version} / 修订 {task.revision}。备注仍保留在下方。</p>
          <details className="conflict-comparison" open>
            <summary>比较原固定内容与当前内容</summary>
            <div className="comparison-grid">
              {dialog.kind === 'approval' ? <>
                <section><strong>原审批 · {toolNameLabel(dialog.item.toolName)}</strong><p>目标：{dialog.item.target}</p><small>{dialog.item.status} · SHA-256 {dialog.item.payloadHash}</small><pre>{dialog.item.content.slice(0, 2400)}</pre></section>
                <section><strong>当前审批</strong>{currentApprovalForDecision ? <><p>{toolNameLabel(currentApprovalForDecision.toolName)} · 目标：{currentApprovalForDecision.target}</p><small>{currentApprovalForDecision.status} · SHA-256 {currentApprovalForDecision.payloadHash}</small><pre>{currentApprovalForDecision.content.slice(0, 2400)}</pre></> : <p>此审批已不在当前任务中。</p>}</section>
              </> : <>
                <section><strong>原成果 · {dialog.item.title}</strong><p>{dialog.item.fileName} · v{dialog.item.version} · 修订 {dialog.item.taskRevision}</p><small>SHA-256 {dialog.item.contentHash}</small><pre>{dialog.item.content.slice(0, 2400)}</pre></section>
                <section><strong>当前修订的最新成果</strong>{currentReviewArtifact ? <><p>{currentReviewArtifact.title} · {currentReviewArtifact.fileName} · v{currentReviewArtifact.version} · 修订 {currentReviewArtifact.taskRevision}</p><small>SHA-256 {currentReviewArtifact.contentHash}</small><pre>{currentReviewArtifact.content.slice(0, 2400)}</pre></> : <p>当前任务修订没有可验收成果。</p>}</section>
              </>}
            </div>
          </details>
          {decisionCanRebase ? <button type="button" className="button button--small" disabled={!online || commandBusy} onClick={rebaseDecisionToCurrentTask}>保留备注，使用当前版本</button> : <p>当前审批正文或成果版本已变化、已处理或不可用；无法把这份决定重定向到新内容。请返回重新检查最新任务。</p>}
        </section>}
        {dialog?.kind === 'approval' && <div className="dialog-fixed-review"><strong>{toolNameLabel(dialog.item.toolName)} · {dialog.item.target}</strong><pre>{dialog.item.content}</pre></div>}
        {dialog?.kind === 'acceptance' && <div className="dialog-fixed-review"><strong>{dialog.item.title} · v{dialog.item.version}</strong><p>任务修订 {dialog.item.taskRevision}</p><details className="technical-detail"><summary>查看内容校验值（SHA-256）</summary><code>{dialog.item.contentHash}</code></details><pre>{dialog.item.content.slice(0, 2400)}{dialog.item.content.length > 2400 ? '\n…（正文其余部分仍可在成果卡片中查看）' : ''}</pre></div>}
        <label className="field">
          <span>{dialog?.kind === 'acceptance' && dialog.decision === 'return' ? '修改意见（必填）' : '备注（可选）'}</span>
          <textarea value={note} onChange={(event) => { setNote(event.currentTarget.value); setError(''); }} rows={3} maxLength={4000} />
        </label>
        {error && <p className="form-error" role="alert">{error}</p>}
      </DecisionDialog>
      <DecisionDialog
        open={cancelOpen}
        title="确认取消任务"
        description="取消会停止后续执行，但不会撤销已完成的效果。工作区确认后会更新任务状态。"
        primaryLabel="确认取消任务"
        danger
        busy={commandBusy}
        onConfirm={() => { setCancelOpen(false); void control('cancel'); }}
        onClose={() => { if (!commandBusy) setCancelOpen(false); }}
      >
        <div className="dialog-fixed-review"><strong>{task.title}</strong><p>当前状态：{workflowStatusLabels[task.status] ?? task.status} · 版本 {task.version}</p></div>
      </DecisionDialog>
    </article>
  );
}
