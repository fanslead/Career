import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createLatestReadGate } from './latestReadGate.mjs';

type Health = {
  status: string;
  nativeAot: boolean;
  sqliteVersion: string | null;
  fts5: boolean;
  processId: number | null;
  workerConfigured: boolean;
};

type RunStatus =
  | 'Queued'
  | 'Running'
  | 'WaitingApproval'
  | 'Approved'
  | 'Executing'
  | 'Cancelling'
  | 'Cancelled'
  | 'UnknownOutcome'
  | 'FinalizationPending'
  | 'Succeeded'
  | 'Denied'
  | 'Failed'
  | string;

type RunView = {
  id: string;
  status: RunStatus;
  prompt: string;
  operationId: string | null;
  argumentsHash: string | null;
  workerPid: number | null;
  modelCalls: number | null;
  toolCalls: number | null;
  text: string | null;
  error: string | null;
  receiptCount: number | null;
  // Present on every RunView; populated only while approval is pending.
  argumentsJson: string | null;
};

type RunReadState = {
  id: string;
  status: 'idle' | 'loading' | 'ready' | 'error';
  message: string;
};

type RunEvent = {
  seq: number;
  runId: string;
  type: string;
  payload: unknown;
  createdUtc: string;
};

const statusLabels: Record<string, string> = {
  Queued: '排队中',
  Running: '运行中',
  WaitingApproval: '等待审批',
  Approved: '已批准',
  Executing: '执行中',
  Cancelling: '取消中',
  Cancelled: '已取消',
  UnknownOutcome: '结果待核对',
  FinalizationPending: '动作已完成，等待收尾',
  Succeeded: '已完成',
  Denied: '已拒绝',
  Failed: '失败',
};

const terminalStatuses = new Set(['Succeeded', 'Denied', 'Failed', 'Cancelled']);
const cancellableStatuses = new Set([
  'Queued', 'Running', 'WaitingApproval', 'Approved', 'Executing', 'FinalizationPending',
]);

async function readResponse<T>(response: Response): Promise<T> {
  const text = await response.text();
  let body: unknown = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }
  if (!response.ok) {
    const detail = typeof body === 'object' && body !== null && 'error' in body
      ? String((body as { error: unknown }).error)
      : typeof body === 'string' ? body : response.statusText;
    throw new HttpError(response.status, detail || `HTTP ${response.status}`);
  }
  return body as T;
}

class HttpError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

async function getJson<T>(path: string): Promise<T> {
  return readResponse<T>(await fetch(path, { headers: { Accept: 'application/json' } }));
}

async function postJson<T>(path: string, body: unknown): Promise<T> {
  return readResponse<T>(await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-P0-Client': '1', Accept: 'application/json' },
    body: JSON.stringify(body),
  }));
}

function showValue(value: string | number | boolean | null | undefined): string {
  if (value === null || value === undefined || value === '') return '—';
  return String(value);
}

function hasReceiptConflict(error: unknown): boolean {
  return error instanceof Error && error.message.includes('receipt_corrupt_or_conflicting');
}

function App() {
  const [health, setHealth] = useState<Health | null>(null);
  const [healthError, setHealthError] = useState('');
  const [runs, setRuns] = useState<RunView[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [run, setRun] = useState<RunView | null>(null);
  const [runReadState, setRunReadState] = useState<RunReadState>({ id: '', status: 'idle', message: '' });
  const [prompt, setPrompt] = useState('P0 验证：保存这条测试备注，确认审批和重启恢复正常。');
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [notice, setNotice] = useState('');
  const [quarantinedRunId, setQuarantinedRunId] = useState('');
  const [events, setEvents] = useState<RunEvent[]>([]);
  const [eventState, setEventState] = useState<'connecting' | 'connected' | 'reconnecting' | 'idle'>('idle');
  const eventSeq = useRef(0);
  const selectedIdRef = useRef(selectedId);
  const runReadStateRef = useRef(runReadState);
  const runReadGate = useRef(createLatestReadGate());
  const runListGeneration = useRef(0);

  const refreshHealth = useCallback(async () => {
    try {
      const value = await getJson<Health>('/health');
      setHealth(value);
      setHealthError('');
    } catch (error) {
      setHealthError(error instanceof Error ? error.message : '无法读取服务状态');
    }
  }, []);

  const updateRunReadState = useCallback((state: RunReadState) => {
    runReadStateRef.current = state;
    setRunReadState(state);
  }, []);

  const selectRunId = useCallback((id: string) => {
    selectedIdRef.current = id;
    runReadGate.current.select(id);
    setSelectedId(id);
    updateRunReadState({ id, status: id ? 'loading' : 'idle', message: '' });
  }, [updateRunReadState]);

  const readRun = useCallback(async (id: string, markLoading = true) => {
    if (!id || selectedIdRef.current !== id) return false;
    if (markLoading) updateRunReadState({ id, status: 'loading', message: '' });
    return runReadGate.current.read(
      id,
      async () => {
        const value = await getJson<RunView>(`/api/runs/${encodeURIComponent(id)}`);
        if (value.id !== id) throw new Error('服务返回的 run ID 与请求不匹配');
        return value;
      },
      (value) => {
        setRun(value);
        setRuns((current) => current.map((item) => item.id === value.id ? { ...item, ...value } : item));
        updateRunReadState({ id, status: 'ready', message: '' });
      },
      (error) => {
        updateRunReadState({
          id,
          status: 'error',
          message: error instanceof Error ? error.message : '无法读取 run 状态',
        });
      },
    );
  }, [updateRunReadState]);

  const refreshRuns = useCallback(async (preferredId?: string) => {
    const listGeneration = ++runListGeneration.current;
    const selectionAtStart = selectedIdRef.current;
    if (selectionAtStart) {
      runReadGate.current.select(selectionAtStart);
      updateRunReadState({ id: selectionAtStart, status: 'loading', message: '' });
    }
    let items: RunView[];
    try {
      items = await getJson<RunView[]>('/api/runs');
    } catch (error) {
      if (listGeneration === runListGeneration.current && selectedIdRef.current === selectionAtStart && selectionAtStart) {
        updateRunReadState({
          id: selectionAtStart,
          status: 'error',
          message: error instanceof Error ? error.message : '无法读取 run 列表',
        });
      }
      throw error;
    }
    if (listGeneration !== runListGeneration.current) return null;
    setRuns(items);
    const targetId = selectedIdRef.current !== selectionAtStart
      ? selectedIdRef.current
      : preferredId ?? selectionAtStart;
    if (targetId && items.some((item) => item.id === targetId)) {
      if (selectedIdRef.current !== targetId) selectRunId(targetId);
      else updateRunReadState({ id: targetId, status: 'loading', message: '' });
      await readRun(targetId, false);
      return items;
    }
    if (items.length > 0) {
      const newest = items[0];
      selectRunId(newest.id);
      await readRun(newest.id, false);
      return items;
    }
    selectRunId('');
    setRun(null);
    return items;
  }, [readRun, selectRunId, updateRunReadState]);

  const reloadAll = useCallback(async () => {
    setRefreshing(true);
    setNotice('');
    await refreshHealth();
    try {
      await refreshRuns();
    } catch (error) {
      setNotice(error instanceof Error ? error.message : '刷新 run 列表失败');
    } finally {
      setRefreshing(false);
    }
  }, [refreshHealth, refreshRuns]);

  useEffect(() => {
    void reloadAll();
  }, [reloadAll]);

  useEffect(() => {
    if (!run || run.id !== selectedId || terminalStatuses.has(run.status)) return;
    let active = true;
    let timer = 0;
    const poll = async () => {
      await readRun(run.id, false);
      if (active) timer = window.setTimeout(() => { void poll(); }, 1800);
    };
    timer = window.setTimeout(() => { void poll(); }, 1800);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [readRun, run?.id, run?.status, selectedId]);

  useEffect(() => {
    if (!selectedId) {
      setEvents([]);
      eventSeq.current = 0;
      setEventState('idle');
      return;
    }
    let disposed = false;
    let source: EventSource | null = null;
    let reconnectTimer = 0;
    setEvents([]);
    eventSeq.current = 0;
    setEventState('connecting');

    const connect = () => {
      if (disposed || selectedIdRef.current !== selectedId) return;
      source = new EventSource(`/api/runs/${encodeURIComponent(selectedId)}/events?after=${eventSeq.current}`);
      source.onopen = () => {
        if (disposed || selectedIdRef.current !== selectedId) return;
        setEventState('connected');
      };
      source.onmessage = (message) => {
        if (disposed || selectedIdRef.current !== selectedId) return;
        try {
          const item = JSON.parse(message.data) as RunEvent;
          if (typeof item.seq !== 'number' || item.runId !== selectedId) return;
          eventSeq.current = Math.max(eventSeq.current, item.seq);
          setEvents((current) => current.some((event) => event.seq === item.seq)
            ? current
            : [...current, item].sort((a, b) => a.seq - b.seq).slice(-100));
        } catch {
          // Ignore malformed probe events; keep the stream alive for later events.
        }
      };
      source.onerror = () => {
        if (disposed || selectedIdRef.current !== selectedId) return;
        setEventState('reconnecting');
        source?.close();
        reconnectTimer = window.setTimeout(connect, 1800);
      };
    };
    connect();
    return () => {
      disposed = true;
      source?.close();
      window.clearTimeout(reconnectTimer);
    };
  }, [selectedId]);

  const statusText = useMemo(() => run ? statusLabels[run.status] ?? run.status : '尚未选择 run', [run]);

  async function createRun() {
    const cleanPrompt = prompt.trim();
    if (!cleanPrompt || busy) return;
    const oldIds = new Set(runs.map((item) => item.id));
    const selectionAtStart = selectedIdRef.current;
    setBusy(true);
    setNotice('');
    try {
      const created = await postJson<RunView>('/api/runs', { prompt: cleanPrompt, scenario: 'normal' });
      if (created?.id) {
        if (selectedIdRef.current === selectionAtStart) selectRunId(created.id);
        setNotice('服务已接受示例 run，正在读取权威状态。');
        await refreshRuns(selectedIdRef.current === created.id ? created.id : undefined);
      } else {
        setNotice('服务已响应，但没有返回 run ID；正在核对列表，不会重复提交。');
        const items = await refreshRuns();
        const discovered = items?.find((item) => !oldIds.has(item.id)) ?? items?.[0];
        if (discovered && selectedIdRef.current === selectionAtStart) {
          selectRunId(discovered.id);
          await readRun(discovered.id, false);
        }
      }
    } catch (error) {
      const isConflict = error instanceof HttpError && error.status === 409;
      setNotice(isConflict
        ? `请求冲突（409）：${error.message}。已读取当前 run 列表。`
        : '创建请求结果未知；正在只读核对 run 列表，不会自动重发。');
      try {
        const items = await refreshRuns();
        const discovered = items?.find((item) => !oldIds.has(item.id));
        if (discovered && selectedIdRef.current === selectionAtStart) {
          selectRunId(discovered.id);
          await readRun(discovered.id, false);
        }
      } catch {
        // Preserve the unknown result notice until the user manually refreshes.
      }
    } finally {
      setBusy(false);
    }
  }

  function isRunActionable(candidate: RunView | null): candidate is RunView {
    return Boolean(candidate &&
      selectedIdRef.current === candidate.id &&
      runReadStateRef.current.id === candidate.id &&
      runReadStateRef.current.status === 'ready');
  }

  async function makeDecision(approved: boolean) {
    if (!isRunActionable(run) || !run.argumentsHash || busy) return;
    setBusy(true);
    setNotice('');
    try {
      await postJson(`/api/runs/${encodeURIComponent(run.id)}/decision`, {
        approved,
        expectedHash: run.argumentsHash,
      });
      setNotice(approved ? '审批已提交，正在读取权威状态。' : '拒绝已提交，正在读取权威状态。');
      await refreshRuns();
    } catch (error) {
      if (error instanceof HttpError && error.status === 409) {
        setNotice(`审批版本冲突（409）：${error.message}。已重新读取 run，检查最新参数后再操作。`);
      } else {
        setNotice('审批请求结果未知；正在核对当前 run，不会自动重发。');
      }
      try { await refreshRuns(); } catch { /* Keep notice for manual recovery. */ }
    } finally {
      setBusy(false);
    }
  }

  async function reconcile() {
    if (!isRunActionable(run) || busy) return;
    setBusy(true);
    setNotice('正在向服务核对既有操作结果…');
    try {
      await postJson(`/api/runs/${encodeURIComponent(run.id)}/reconcile`, {});
      setNotice('核对请求已接受，正在读取权威状态。');
      await refreshRuns();
    } catch (error) {
      if (hasReceiptConflict(error)) {
        setQuarantinedRunId(run.id);
        setNotice('回执已隔离，需人工处理；不会自动重试。');
      } else if (error instanceof HttpError && error.status === 409) {
        setNotice(`服务尚不能确认操作结果（409）：${error.message}。稍后可再次核对。`);
      } else {
        setNotice('核对请求结果未知；请刷新状态后再决定是否重新核对。');
      }
      try { await refreshRuns(); } catch { /* Keep notice for manual recovery. */ }
    } finally {
      setBusy(false);
    }
  }

  async function resumeFinalization() {
    if (!isRunActionable(run) || busy || receiptQuarantined) return;
    setBusy(true);
    setNotice('正在继续整理已完成动作的结果…');
    try {
      await postJson(`/api/runs/${encodeURIComponent(run.id)}/resume`, {});
      setNotice('续接请求已接受；正在读取权威状态。只复用已成功回执，不会再次产生副作用。');
      await refreshRuns();
    } catch (error) {
      if (hasReceiptConflict(error)) {
        setQuarantinedRunId(run.id);
        setNotice('回执已隔离，需人工处理；不会自动重试。');
      } else if (error instanceof HttpError && error.status === 409) {
        setNotice(`续接请求冲突（409）：${error.message}。正在只读核对当前状态。`);
      } else {
        setNotice('续接请求结果未知；正在只读核对当前状态，不会自动重发。');
      }
      try { await refreshRuns(); } catch { /* Keep the notice; only retry the read manually. */ }
    } finally {
      setBusy(false);
    }
  }

  async function cancelRun() {
    if (!isRunActionable(run) || busy || !cancellableStatuses.has(run.status)) return;
    setBusy(true);
    setNotice('正在请求取消；已发生的外部副作用不会回滚。');
    try {
      await postJson(`/api/runs/${encodeURIComponent(run.id)}/cancel`, {});
      setNotice('取消请求已接受，正在读取权威状态。');
      await refreshRuns();
    } catch (error) {
      if (error instanceof HttpError && error.status === 409) {
        setNotice(`取消请求冲突（409）：${error.message}。正在只读核对当前状态。`);
      } else {
        setNotice('取消请求结果未知；正在只读核对当前状态，不会自动重发。');
      }
      try { await refreshRuns(); } catch { /* Keep the notice; only retry the read manually. */ }
    } finally {
      setBusy(false);
    }
  }

  async function selectRun(id: string) {
    selectRunId(id);
    setNotice('');
    await readRun(id, false);
  }

  const approvalPending = run?.status === 'WaitingApproval';
  const outcomeUnknown = run?.status === 'UnknownOutcome';
  const finalizationPending = run?.status === 'FinalizationPending';
  const cancellable = Boolean(run && cancellableStatuses.has(run.status));
  const receiptQuarantined = Boolean(run && (run.error?.includes('receipt_corrupt_or_conflicting') || quarantinedRunId === run.id));
  const runReadCurrent = runReadState.id === selectedId;
  const runSelectionMismatch = Boolean(run && run.id !== selectedId);
  const runReadNotice = !selectedId ? '' : runSelectionMismatch
    ? runReadState.id === selectedId && runReadState.status === 'error'
      ? `读取所选 run 失败：${runReadState.message}。详情仍显示 ${run?.id}，写操作已禁用。`
      : `正在读取所选 run ${selectedId}；详情暂时显示 ${run?.id}，写操作已禁用。`
    : !runReadCurrent || runReadState.status === 'loading'
      ? '正在读取当前 run 状态，写操作已暂时禁用。'
      : runReadState.status === 'error'
      ? run?.id === selectedId
        ? `读取 run 状态失败：${runReadState.message}。当前内容是上次成功读取的数据，写操作已禁用。`
        : `读取所选 run 失败：${runReadState.message}。当前详情不是所选 run，写操作已禁用。`
      : '';
  const runActionsAvailable = isRunActionable(run);

  return (
    <main className="page-shell">
      <header className="topbar">
        <div className="brand-mark" aria-hidden="true">P0</div>
        <div className="heading-copy">
          <p className="eyebrow">AGENT RUNTIME PROBE</p>
          <h1>P0 技术验证</h1>
          <p className="subtitle">真实 MAF · 可控模型替身 · 独立 .NET 服务</p>
        </div>
        <button className="button button-light refresh-button" onClick={() => void reloadAll()} disabled={refreshing}>
          <span aria-hidden="true">↻</span> {refreshing ? '刷新中…' : '刷新状态'}
        </button>
      </header>

      <section className="health-card panel" aria-labelledby="health-title">
        <div className="section-heading">
          <div>
            <p className="eyebrow">SERVICE HEALTH</p>
            <h2 id="health-title">服务与存储</h2>
          </div>
          <span className={`health-indicator ${health && !healthError ? 'is-up' : 'is-down'}`}>
            <span className="indicator-dot" />{health && !healthError ? '服务在线' : '未连接'}
          </span>
        </div>
        {healthError && <p className="inline-error">{healthError}。请确认 .NET 服务已启动。</p>}
        <div className="health-grid">
          <Metric label="运行状态" value={health?.status} />
          <Metric label="Native AOT" value={health ? (health.nativeAot ? '是' : '否') : '—'} />
          <Metric label="SQLite" value={health?.sqliteVersion} />
          <Metric label="FTS5" value={health ? (health.fts5 ? '可用' : '不可用') : '—'} />
          <Metric label="服务 PID" value={health?.processId} />
          <Metric label="Worker 已配置" value={health ? (health.workerConfigured ? '是' : '否') : '—'} />
        </div>
      </section>

      <section className="workspace-grid">
        <div className="left-column">
          <section className="panel create-card" aria-labelledby="create-title">
            <div className="section-heading">
              <div>
                <p className="eyebrow">REAL MAF TOOL LOOP</p>
                <h2 id="create-title">新建示例 run</h2>
              </div>
              <span className="step-number">01</span>
            </div>
            <label className="field-label" htmlFor="prompt">给 Agent 的任务</label>
            <textarea id="prompt" value={prompt} onChange={(event) => setPrompt(event.target.value)} rows={4} />
            <p className="field-hint">真实 MAF Agent 与工具链搭配可控模型替身运行，不会调用真实模型服务。</p>
            <button className="button button-primary" onClick={() => void createRun()} disabled={busy || !prompt.trim()}>
              {busy ? '处理中…' : '创建并运行'} <span aria-hidden="true">→</span>
            </button>
          </section>

          <section className="panel runs-card" aria-labelledby="runs-title">
            <div className="section-heading">
              <div>
                <p className="eyebrow">PERSISTED RUNS</p>
                <h2 id="runs-title">运行记录</h2>
              </div>
              <span className="count-badge">{runs.length}</span>
            </div>
            {runs.length === 0 ? (
              <p className="empty-state">还没有 run。创建一个示例任务开始验证。</p>
            ) : (
              <div className="run-list">
                {runs.map((item) => (
                  <button key={item.id} className={`run-item ${item.id === selectedId ? 'selected' : ''}`} onClick={() => void selectRun(item.id)}>
                    <span className="run-item-main">
                      <span className="run-prompt">{item.prompt || '(无提示词)'}</span>
                      <span className="run-id">{item.id}</span>
                    </span>
                    <span className={`status-pill status-${item.status.toLowerCase()}`}>{statusLabels[item.status] ?? item.status}</span>
                  </button>
                ))}
              </div>
            )}
          </section>
        </div>

        <section className="panel detail-card" aria-labelledby="detail-title">
          <div className="section-heading detail-heading">
            <div>
              <p className="eyebrow">RUN INSPECTOR</p>
              <h2 id="detail-title">执行与审批</h2>
            </div>
            {run && <span className={`status-pill status-${run.status.toLowerCase()}`}>{statusText}</span>}
          </div>
          {runReadNotice && <p className="run-read-notice" role="status" aria-live="polite">{runReadNotice}</p>}
          {!run ? (
            <div className="empty-detail">
              <span className="empty-symbol" aria-hidden="true">◎</span>
              <p>{selectedId ? '正在读取运行记录…' : '选择一条运行记录，查看执行状态与事件。'}</p>
            </div>
          ) : (
            <>
              <div className="run-summary">
                <div><span className="summary-label">Run ID</span><code>{run.id}</code></div>
                <div><span className="summary-label">Worker PID</span><strong>{showValue(run.workerPid)}</strong></div>
                <div><span className="summary-label">模型调用</span><strong>{showValue(run.modelCalls)}</strong></div>
                <div><span className="summary-label">工具调用</span><strong>{showValue(run.toolCalls)}</strong></div>
                <div><span className="summary-label">恢复凭据</span><strong>{showValue(run.receiptCount)}</strong></div>
              </div>
              <p className="usage-note">计数仅含 Worker 已返回的统计；中断尝试未计入，也不代表费用。</p>
              <div className="prompt-preview">
                <span className="summary-label">任务内容</span>
                <p>{run.prompt}</p>
              </div>

              {cancellable && (
                <div className="cancel-row">
                  <p>取消后若结果待核对，请核对已有回执。已发生的外部副作用不会回滚，已有回执会保留。</p>
                  <button className="button button-danger" disabled={busy || !runActionsAvailable} onClick={() => void cancelRun()}>取消运行</button>
                </div>
              )}

              {(run.status === 'Cancelling' || run.status === 'Cancelled') && (
                <p className="cancel-state-note">取消不会撤回已发生的外部副作用；结果若待核对，请核对已有回执，已有回执会保留。</p>
              )}

              {approvalPending && (
                <section className="approval-box" aria-labelledby="approval-title">
                  <div className="approval-title-row">
                    <span className="approval-icon" aria-hidden="true">!</span>
                    <div>
                      <p className="eyebrow">HUMAN APPROVAL</p>
                      <h3 id="approval-title">工具执行等待审批</h3>
                    </div>
                  </div>
                  <div className="approval-meta">
                    <div><span className="summary-label">操作 ID</span><code>{showValue(run.operationId)}</code></div>
                    <div><span className="summary-label">参数正文 SHA-256</span><code className="hash-value">{showValue(run.argumentsHash)}</code></div>
                  </div>
                  {run.argumentsJson && (
                    <pre className="arguments-body">{run.argumentsJson}</pre>
                  )}
                  <p className="approval-note">审批会绑定上面的 hash。若参数已变化，服务应以 409 拒绝过期审批。</p>
                  <div className="action-row">
                    <button className="button button-danger" disabled={busy || !run.argumentsHash || !runActionsAvailable} onClick={() => void makeDecision(false)}>拒绝</button>
                    <button className="button button-primary" disabled={busy || !run.argumentsHash || !runActionsAvailable} onClick={() => void makeDecision(true)}>批准并继续</button>
                  </div>
                </section>
              )}

              {outcomeUnknown && (
                <section className="unknown-box" aria-labelledby="unknown-title">
                  <div>
                    <p className="eyebrow">RECOVERY CHECK</p>
                    <h3 id="unknown-title">工具结果不确定</h3>
                    <p>先核对已有回执；只有确认专用本地目标未发布，才允许继续。若回执损坏或冲突，结果会隔离并需人工处理。</p>
                    {receiptQuarantined && <p className="quarantine-note">回执已隔离（receipt_corrupt_or_conflicting），需人工处理。</p>}
                  </div>
                  <button className="button button-primary" disabled={busy || receiptQuarantined || !runActionsAvailable} onClick={() => void reconcile()}>
                    {receiptQuarantined ? '已隔离，需人工处理' : '核对已有回执'}
                  </button>
                </section>
              )}

              {finalizationPending && (
                <section className="unknown-box finalization-box" aria-labelledby="finalization-title">
                  <div>
                    <p className="eyebrow">FINALIZE RESULT</p>
                    <h3 id="finalization-title">动作已完成，等待收尾</h3>
                    <p>继续整理只复用已成功回执，不会再次产生副作用。</p>
                    {receiptQuarantined && <p className="quarantine-note">回执已隔离（receipt_corrupt_or_conflicting），需人工处理。</p>}
                  </div>
                  <button className="button button-primary" disabled={busy || receiptQuarantined || !runActionsAvailable} onClick={() => void resumeFinalization()}>
                    {receiptQuarantined ? '已隔离，需人工处理' : '继续整理结果'}
                  </button>
                </section>
              )}

              {run.error && <div className="result-box error-box"><span className="summary-label">错误</span><p>{run.error}</p></div>}
              {run.text && <div className="result-box"><span className="summary-label">Agent 输出</span><p>{run.text}</p></div>}

              <div className="events-section">
                <div className="events-heading">
                  <div>
                    <p className="eyebrow">SERVER-SENT EVENTS</p>
                    <h3>事件流 <span className="event-total">{events.length}</span></h3>
                  </div>
                  <span className={`stream-state stream-${eventState}`}><span className="indicator-dot" />{eventState === 'connected' ? '实时连接' : eventState === 'connecting' ? '连接中' : eventState === 'reconnecting' ? '重连中' : '已停止'}</span>
                </div>
                {events.length === 0 ? (
                  <p className="empty-state event-empty">等待服务端事件…</p>
                ) : (
                  <ol className="event-list">
                    {events.map((event) => (
                      <li key={`${event.runId}-${event.seq}`} className="event-item">
                        <span className="event-seq">{String(event.seq).padStart(3, '0')}</span>
                        <div className="event-body">
                          <div className="event-line"><strong>{event.type}</strong><time>{formatTime(event.createdUtc)}</time></div>
                          <pre>{formatPayload(event.payload)}</pre>
                        </div>
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            </>
          )}
        </section>
      </section>

      {notice && <div className="notice" role="status" aria-live="polite">{notice}</div>}
      <footer className="footer-note"><span className="footer-dot" /> P0 Harness · 轻量验证界面 · 所有内容均按文本显示</footer>
    </main>
  );
}

function Metric({ label, value }: { label: string; value: string | number | boolean | null | undefined }) {
  return <div className="metric"><span>{label}</span><strong>{showValue(value)}</strong></div>;
}

function formatPayload(payload: unknown): string {
  if (payload === null || payload === undefined) return '';
  if (typeof payload === 'string') return payload;
  return JSON.stringify(payload, null, 2);
}

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleTimeString('zh-CN', { hour12: false });
}

export default App;
