import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiProblem, ConnectionLostError, hasAccessToken, setAccessToken, setSessionExpiredHandler } from './api/client.ts';
import { runSyncLoop, type SyncState } from './api/sync.ts';
import type { BootstrapResponse, ConnectionResponse, TaskResponse, WorkspaceResponse } from './contracts.ts';
import { ApprovalsPage } from './components/ApprovalsPage.tsx';
import { ArtifactsPage } from './components/ArtifactsPage.tsx';
import { ConnectionSettings } from './components/ConnectionSettings.tsx';
import { PairingGate } from './components/PairingGate.tsx';
import { PlaceholderPage } from './components/PlaceholderPage.tsx';
import { TasksPage } from './components/TasksPage.tsx';
import { Workbench } from './components/Workbench.tsx';
import { CommandTracker, type PendingCommandMetadata } from './state/commandTracker.ts';
import { shouldApplyTaskSnapshot, shouldApplyWorkspaceSnapshot } from './state/snapshotCache.ts';
import { loadTaskDetailsBatch, taskDetailsNeedingRefresh } from './state/taskDetailsBatch.ts';
import { statusLabels } from './statusLabels.ts';

type Page = 'workbench' | 'projects' | 'team' | 'materials' | 'settings';
type WorkbenchSection = 'overview' | 'tasks' | 'approvals' | 'artifacts';
type AuthState = 'loading' | 'pairing' | 'ready' | 'unavailable';

function requiresExecutionAvailability(route: string, body?: Record<string, unknown>): boolean {
  return route === '/api/connection/test'
    || /^\/api\/tasks\/[^/]+\/(?:confirm-plan|resume|retry)$/.test(route)
    || (/^\/api\/approvals\/[^/]+\/decision$/.test(route) && body?.decision === 'approve')
    || (/^\/api\/tasks\/[^/]+\/acceptances$/.test(route) && body?.decision === 'return');
}

const pages: { id: Page; label: string; hint: string }[] = [
  { id: 'workbench', label: '工作台', hint: '待处理与近期成果' },
  { id: 'projects', label: '项目', hint: '项目目标与任务' },
  { id: 'team', label: '团队', hint: '成员与分工' },
  { id: 'materials', label: '资料', hint: '知识库与记忆' },
  { id: 'settings', label: '设置', hint: '模型与工作区' },
];

const workbenchSections: { id: WorkbenchSection; label: string }[] = [
  { id: 'overview', label: '总览' },
  { id: 'tasks', label: '任务' },
  { id: 'approvals', label: '审批' },
  { id: 'artifacts', label: '成果' },
];

function describeError(error: unknown): string {
  if (error instanceof ApiProblem) return error.status === 401
    ? '浏览器会话已失效，请使用工作区终端显示的配对码重新连接。'
    : error.detail;
  if (error instanceof ConnectionLostError) return error.message;
  return error instanceof Error ? error.message : '操作未完成。请刷新并查看工作区当前状态后再继续。';
}

function taskIdForRecovery(command: PendingCommandMetadata, tasks: ReadonlyMap<string, TaskResponse>): string | null {
  const taskRoute = command.route.match(/^\/api\/tasks\/([^/]+)/);
  if (taskRoute) {
    try { return decodeURIComponent(taskRoute[1]); } catch { return null; }
  }
  const approvalRoute = command.route.match(/^\/api\/approvals\/([^/]+)\/decision$/);
  if (approvalRoute) {
    const approvalId = decodeURIComponent(approvalRoute[1]);
    return [...tasks.values()].find((task) => task.approvals.some((approval) => approval.id === approvalId))?.id ?? null;
  }
  return null;
}

export function App() {
  const tracker = useMemo(() => {
    try { return new CommandTracker(window.sessionStorage); }
    catch { return new CommandTracker(); }
  }, []);
  const [auth, setAuth] = useState<AuthState>('loading');
  const [bootstrap, setBootstrap] = useState<BootstrapResponse | null>(null);
  const [workspace, setWorkspace] = useState<WorkspaceResponse | undefined>();
  const [taskSnapshots, setTaskSnapshots] = useState<Map<string, TaskResponse>>(new Map());
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [page, setPage] = useState<Page>('workbench');
  const [workbenchSection, setWorkbenchSection] = useState<WorkbenchSection>('overview');
  const [lastSyncedAt, setLastSyncedAt] = useState<number | null>(null);
  const [online, setOnline] = useState(navigator.onLine);
  const [syncState, setSyncState] = useState<SyncState>('connecting');
  const [pairingBusy, setPairingBusy] = useState(false);
  const [commandBusy, setCommandBusy] = useState(false);
  const [commandError, setCommandError] = useState('');
  const [commandConflict, setCommandConflict] = useState<TaskResponse | null>(null);
  const [conflictPrevious, setConflictPrevious] = useState<TaskResponse | null>(null);
  const [conflictSubmittedVersion, setConflictSubmittedVersion] = useState<number | null>(null);
  const [conflictHasDecisionDialog, setConflictHasDecisionDialog] = useState(false);
  const [unresolvedIds, setUnresolvedIds] = useState(tracker.unresolvedIds());
  const [receiptNotes, setReceiptNotes] = useState<Record<string, string>>({});
  const [recoveryRequestId, setRecoveryRequestId] = useState<string | null>(null);
  const [detailLoad, setDetailLoad] = useState<{ pending: string[]; failed: string[] }>({ pending: [], failed: [] });
  const [detailRetry, setDetailRetry] = useState(0);
  const [syncRestart, setSyncRestart] = useState(0);
  const workspaceRef = useRef(workspace);
  const tasksRef = useRef(taskSnapshots);
  const selectedRef = useRef(selectedTaskId);
  const syncGeneration = useRef(0);

  useEffect(() => { workspaceRef.current = workspace; }, [workspace]);
  useEffect(() => { tasksRef.current = taskSnapshots; }, [taskSnapshots]);
  useEffect(() => { selectedRef.current = selectedTaskId; }, [selectedTaskId]);

  useEffect(() => {
    const sessionExpired = () => {
      setAccessToken(null);
      setWorkspace(undefined);
      setTaskSnapshots(new Map());
      setAuth('pairing');
      setCommandError('');
      void api.bootstrap().then((value) => {
        setBootstrap(value);
        setCommandError(value.hasOwner ? '浏览器会话已失效，请使用工作区终端显示的配对码重新连接。' : '');
      }).catch((error) => setCommandError(describeError(error)));
    };
    setSessionExpiredHandler(sessionExpired);
    return () => setSessionExpiredHandler(null);
  }, []);

  const mergeWorkspace = useCallback((value: WorkspaceResponse) => {
    setWorkspace((current) => shouldApplyWorkspaceSnapshot(current, value) ? value : current);
    setLastSyncedAt(Date.now());
    setSelectedTaskId((current) => current ?? value.tasks[0]?.id ?? null);
  }, []);

  const mergeTask = useCallback((id: string, value: TaskResponse) => {
    setTaskSnapshots((current) => {
      const previous = current.get(id);
      if (!shouldApplyTaskSnapshot(id, previous, value)) return current;
      const next = new Map(current);
      next.set(id, value);
      return next;
    });
  }, []);

  const refreshTask = useCallback(async (id: string) => {
    const snapshot = await api.task(id);
    mergeTask(id, snapshot);
    return snapshot;
  }, [mergeTask]);

  const refreshWorkspace = useCallback(async (taskId?: string) => {
    const snapshot = await api.workspace();
    mergeWorkspace(snapshot);
    const id = taskId ?? selectedRef.current;
    if (id) {
      try { await refreshTask(id); } catch (error) { if (!(error instanceof ApiProblem && error.status === 404)) throw error; }
    }
    const connection = await api.connection();
    setWorkspace((current) => current ? { ...current, connection } : current);
  }, [mergeWorkspace, refreshTask]);

  useEffect(() => {
    let cancelled = false;
    async function initialize() {
      setAuth('loading');
      try {
        const value = await api.bootstrap();
        if (cancelled) return;
        setBootstrap(value);
        if (navigator.onLine && await api.refresh()) {
          if (cancelled) return;
          setAuth('ready');
        } else {
          setAuth('pairing');
        }
      } catch (error) {
        if (cancelled) return;
        setCommandError(describeError(error));
        setAuth('unavailable');
      }
    }
    void initialize();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    const onlineChanged = () => setOnline(navigator.onLine);
    window.addEventListener('online', onlineChanged);
    window.addEventListener('offline', onlineChanged);
    return () => {
      window.removeEventListener('online', onlineChanged);
      window.removeEventListener('offline', onlineChanged);
    };
  }, []);

  useEffect(() => {
    if (auth !== 'ready') return;
    const generation = ++syncGeneration.current;
    const controller = new AbortController();
    const sink = {
      get workspace() { return workspaceRef.current; },
      get tasks() { return tasksRef.current; },
      setWorkspace: mergeWorkspace,
      setTask: mergeTask,
      setState: (value: SyncState) => setSyncState(value),
      getSelectedTaskId: () => selectedRef.current,
    };
    void runSyncLoop(sink, controller.signal).finally(() => {
      if (syncGeneration.current === generation) setSyncState(navigator.onLine ? 'reconnecting' : 'offline');
    });
    return () => controller.abort();
  }, [auth, online, syncRestart, mergeWorkspace, mergeTask]);

  useEffect(() => {
    if (auth !== 'ready' || !selectedTaskId) return;
    void refreshTask(selectedTaskId).catch((error) => {
      if (!(error instanceof ApiProblem && error.status === 404)) setCommandError(describeError(error));
    });
  }, [auth, selectedTaskId, refreshTask]);

  useEffect(() => {
    if (auth !== 'ready' || !workspace || page !== 'workbench' || (workbenchSection !== 'approvals' && workbenchSection !== 'artifacts')) {
      return;
    }
    const missing = taskDetailsNeedingRefresh(workspace.tasks, tasksRef.current);
    if (missing.length === 0) {
      setDetailLoad({ pending: [], failed: [] });
      return;
    }
    if (syncState !== 'live') {
      setDetailLoad({ pending: missing, failed: [] });
      return;
    }
    const controller = new AbortController();
    const pending = new Set(missing);
    const failed = new Set<string>();
    const publish = () => setDetailLoad({ pending: [...pending], failed: [...failed] });
    publish();
    void loadTaskDetailsBatch(
      missing,
      4,
      controller.signal,
      (id) => api.task(id, controller.signal),
      (id, snapshot) => { mergeTask(id, snapshot); pending.delete(id); failed.delete(id); publish(); },
      (id) => { pending.delete(id); failed.add(id); publish(); },
    );
    return () => controller.abort();
  }, [auth, page, workbenchSection, workspace?.snapshotSeq, syncState, detailRetry, mergeTask]);

  const reconcileReceipt = useCallback(async (requestId: string) => {
    setReceiptNotes((current) => ({ ...current, [requestId]: '正在核对命令回执…' }));
    try {
      const receipt = await api.receipt(requestId);
      tracker.resolveReceipt(requestId);
      setUnresolvedIds(tracker.unresolvedIds());
      setRecoveryRequestId((current) => current === requestId ? null : current);
      setReceiptNotes((current) => ({ ...current, [requestId]: `已接纳 · ${receipt.route} · 版本 ${receipt.version}` }));
      await refreshWorkspace(receipt.taskId);
    } catch (error) {
      if (error instanceof ApiProblem && error.status === 404) {
        setReceiptNotes((current) => ({ ...current, [requestId]: '暂未找到回执。命令可能仍在处理中，请稍后再次查询。' }));
      } else {
        setReceiptNotes((current) => ({ ...current, [requestId]: describeError(error) }));
      }
    }
  }, [refreshWorkspace, tracker]);

  useEffect(() => {
    if (auth !== 'ready') return;
    for (const requestId of tracker.unresolvedIds()) void reconcileReceipt(requestId);
  }, [auth, reconcileReceipt, tracker]);

  const runCommand = useCallback(async <T,>(
    key: string,
    route: string,
    makeBody: (requestId: string) => Record<string, unknown>,
    method = 'POST',
  ): Promise<{ ok: boolean; value?: T; error?: string; status?: number }> => {
    const executionGuardPreview = route.endsWith('/acceptances') || /^\/api\/approvals\/[^/]+\/decision$/.test(route) ? makeBody('') : undefined;
    if (requiresExecutionAvailability(route, executionGuardPreview) && workspace?.executionAvailable !== true) {
      return { ok: false, error: '工作区完成上次任务检查前，暂不能开始新的模型执行。' };
    }
    const unknownPendingIds = tracker.unknownPendingIds();
    if (unknownPendingIds.length > 0) {
      return { ok: false, error: `有旧版命令 ${unknownPendingIds.join('、')} 尚未核对。请只查询这些 requestId 的回执，再提交新操作。` };
    }
    if (!navigator.onLine || syncState !== 'live' || !hasAccessToken()) {
      return { ok: false, error: '工作区尚未同步完成，操作未提交。请等待重新连接后查看最新状态。' };
    }
    const pending = tracker.pendingForKey(key);
    if (pending && !tracker.recoveryIsEnabled(pending.requestId)) {
      return { ok: false, error: `上次提交结果仍待核对。请先查询回执 ${pending.requestId}，不要重复提交。` };
    }
    let command;
    try {
      command = tracker.begin(key, route, makeBody, method);
    } catch (error) {
      return { ok: false, error: describeError(error) };
    }
    setCommandBusy(true);
    setCommandError('');
    setCommandConflict(null);
    setConflictPrevious(null);
    setConflictSubmittedVersion(null);
    setConflictHasDecisionDialog(false);
    try {
      const value = await api.command<T>(command.route, command.body, command.method);
      tracker.resolve(command);
      setUnresolvedIds(tracker.unresolvedIds());
      setRecoveryRequestId((current) => current === command.requestId ? null : current);
      setReceiptNotes((current) => { const next = { ...current }; delete next[command.requestId]; return next; });
      const possibleTask = value && typeof value === 'object' && 'id' in value && 'availableActions' in value ? value as unknown as TaskResponse : undefined;
      if (possibleTask) mergeTask(possibleTask.id, possibleTask);
      try {
        const taskId = possibleTask?.id ?? command.route.match(/^\/api\/tasks\/([^/]+)/)?.[1];
        await refreshWorkspace(taskId ? decodeURIComponent(taskId) : undefined);
      } catch {
        setSyncRestart((current) => current + 1);
      }
      return { ok: true, value };
    } catch (error) {
      if (error instanceof ApiProblem && error.current) {
        const submittedVersion = typeof command.body.expectedVersion === 'number' ? command.body.expectedVersion : null;
        const previous = tasksRef.current.get(error.current.id) ?? null;
        setConflictSubmittedVersion(submittedVersion);
        setConflictPrevious(previous?.version === submittedVersion ? previous : null);
        setConflictHasDecisionDialog(/^\/api\/approvals\/[^/]+\/decision$/.test(command.route) || command.route.endsWith('/acceptances'));
        mergeTask(error.current.id, error.current);
        setCommandConflict(error.current);
      }
      if (error instanceof ApiProblem && error.status === 409) {
        tracker.retainForRecovery(command);
        setReceiptNotes((current) => ({ ...current, [command.requestId]: '版本冲突。原 requestId 已保留；比较当前版本后可显式重填并重试。' }));
      } else if (error instanceof ApiProblem && error.status >= 400 && error.status < 500) {
        tracker.resolve(command);
        setRecoveryRequestId((current) => current === command.requestId ? null : current);
      }
      else tracker.markUncertain(command);
      setUnresolvedIds(tracker.unresolvedIds());
      const detail = describeError(error);
      setCommandError(detail);
      return { ok: false, status: error instanceof ApiProblem ? error.status : undefined, error: error instanceof ApiProblem && error.status === 409
        ? `版本已变化，输入内容仍保留。已载入任务 v${error.current?.version ?? '最新'}，请比较后再决定。`
        : error instanceof ApiProblem && error.status >= 500
          ? `${detail} 提交结果不确定，请先查询回执。`
          : detail };
    } finally {
      setCommandBusy(false);
    }
  }, [mergeTask, refreshWorkspace, syncState, tracker, workspace?.executionAvailable, workspace?.executionMessage]);

  const retryOriginal = useCallback(async (requestId: string) => {
    const command = tracker.activeById(requestId);
    if (command && requiresExecutionAvailability(command.route, command.body) && workspace?.executionAvailable !== true) {
      setReceiptNotes((current) => ({ ...current, [requestId]: workspace?.executionMessage || '工作区正在恢复执行状态，此命令暂不能重试。' }));
      return;
    }
    if (!command || !navigator.onLine || syncState !== 'live') return;
    setCommandBusy(true);
    setReceiptNotes((current) => ({ ...current, [requestId]: '正在以相同 requestId 查询原命令…' }));
    try {
      await api.command(command.route, command.body, command.method);
      tracker.resolveReceipt(requestId);
      setUnresolvedIds(tracker.unresolvedIds());
      setRecoveryRequestId((current) => current === requestId ? null : current);
      setReceiptNotes((current) => ({ ...current, [requestId]: '已收到原命令回应，正在读取权威状态。' }));
      await refreshWorkspace();
    } catch (error) {
      if (error instanceof ApiProblem && error.status >= 400 && error.status < 500) {
        tracker.resolveReceipt(requestId);
        setUnresolvedIds(tracker.unresolvedIds());
        setRecoveryRequestId((current) => current === requestId ? null : current);
      }
      setReceiptNotes((current) => ({ ...current, [requestId]: `${describeError(error)} 原 requestId 已保留，可再次查询。` }));
      setSyncRestart((current) => current + 1);
    } finally {
      setCommandBusy(false);
    }
  }, [refreshWorkspace, syncState, tracker, workspace?.executionAvailable, workspace?.executionMessage]);

  const beginReceiptRecovery = useCallback((requestId: string) => {
    const metadata = tracker.pendingById(requestId);
    if (!metadata || !tracker.enableRecovery(requestId)) return;
    setRecoveryRequestId(requestId);
    setReceiptNotes((current) => ({ ...current, [requestId]: '已进入重填流程；尚未提交。正文和备注未保存在浏览器中。' }));
    if (metadata.route === '/api/tasks' && metadata.method === 'POST') {
      setPage('workbench');
      setWorkbenchSection('overview');
      return;
    }
    if ((metadata.route === '/api/connection' && metadata.method === 'PUT') || (metadata.route === '/api/connection/test' && metadata.method === 'POST')) {
      setPage('settings');
      return;
    }
    const taskRoute = metadata.route.match(/^\/api\/tasks\/([^/]+)/);
    if (taskRoute) {
      try { setSelectedTaskId(decodeURIComponent(taskRoute[1])); } catch { /* Keep the recovery marker visible. */ }
      setPage('workbench');
      setWorkbenchSection('tasks');
      return;
    }
    if (/^\/api\/approvals\/[^/]+\/decision$/.test(metadata.route)) {
      setPage('workbench');
      setWorkbenchSection('approvals');
      return;
    }
    setReceiptNotes((current) => ({ ...current, [requestId]: '此命令类型不能在当前页面自动恢复，请先核对回执。原 requestId 仍保留。' }));
  }, [tracker]);

  const prepareCommandRecovery = useCallback((key: string) => {
    const pending = tracker.pendingForKey(key);
    if (pending) tracker.enableRecovery(pending.requestId);
  }, [tracker]);

  const pair = useCallback(async (code: string) => {
    if (!navigator.onLine) throw new Error('当前离线，无法完成配对。');
    setPairingBusy(true);
    try {
      const session = await api.pair(code);
      setAccessToken(session.accessToken);
      setCommandError('');
      setAuth('ready');
      await refreshWorkspace();
    } finally {
      setPairingBusy(false);
    }
  }, [refreshWorkspace]);

  const activeRecoveryMetadata = recoveryRequestId ? tracker.pendingById(recoveryRequestId) : undefined;
  const associatedRecoveryTaskId = activeRecoveryMetadata ? taskIdForRecovery(activeRecoveryMetadata, taskSnapshots) : null;
  useEffect(() => {
    if (!activeRecoveryMetadata || associatedRecoveryTaskId === null) return;
    setSelectedTaskId(associatedRecoveryTaskId);
    setPage('workbench');
    setWorkbenchSection('tasks');
  }, [activeRecoveryMetadata, associatedRecoveryTaskId]);

  const downloadArtifact = useCallback(async (artifact: import('./contracts.ts').ArtifactResponse) => {
    try {
      const { blob, name } = await api.download(artifact.id, artifact.version);
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = name;
      anchor.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) {
      setCommandError(describeError(error));
    }
  }, []);

  async function signOut() {
    try { await api.logout(); } catch { /* Clear the in-memory credential even if Host is unavailable. */ }
    setAccessToken(null);
    setWorkspace(undefined);
    setTaskSnapshots(new Map());
    setRecoveryRequestId(null);
    setAuth('pairing');
    setPage('workbench');
    setWorkbenchSection('overview');
  }

  if (auth === 'loading' || auth === 'unavailable') {
    return <main className="gate-wrap"><section className="panel gate-panel"><div className="brand-mark" aria-hidden="true">C</div><p className="eyebrow">Career 工作区</p><h1>{auth === 'loading' ? '正在连接工作区' : '暂时无法连接'}</h1><p className="lede">{commandError || '正在读取工作区状态。刷新不会更改任务。'}</p><button className="button" onClick={() => window.location.reload()}>重新连接</button></section></main>;
  }

  if (auth === 'pairing') return <PairingGate bootstrap={bootstrap} error={commandError} busy={pairingBusy} offline={!online} onPair={pair} />;
  if (!workspace) return <main className="gate-wrap"><section className="panel gate-panel"><h1>正在读取工作区</h1><p className="lede">连接成功后会显示工作区内容。</p></section></main>;

  const canWrite = online && syncState === 'live' && !commandBusy;
  const retryOriginalDisabled = (requestId: string) => {
    const command = tracker.activeById(requestId);
    return !canWrite || commandBusy || !command || (requiresExecutionAvailability(command.route, command.body) && workspace.executionAvailable !== true);
  };
  const selectedPage = pages.find((item) => item.id === page);
  const openWorkbenchSection = (section: WorkbenchSection) => {
    setPage('workbench');
    setWorkbenchSection(section);
  };
  const nav = (mobile = false) => <nav className={mobile ? 'mobile-nav' : 'sidebar-nav'} aria-label="主导航">
    {pages.map((item) => <button key={item.id} type="button" className={`nav-item ${page === item.id ? 'nav-item--active' : ''}`} aria-current={page === item.id ? 'page' : undefined} onClick={() => setPage(item.id)}>
      <span className="nav-item__title">{item.label}</span><small>{item.hint}</small>
    </button>)}
  </nav>;

  const onCreated = async (task: TaskResponse) => {
    mergeTask(task.id, task);
    setSelectedTaskId(task.id);
    openWorkbenchSection('tasks');
    try { await refreshWorkspace(task.id); } catch { setSyncState('reconnecting'); }
  };

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">跳到主要内容</a>
      <aside className="sidebar">
        <div className="brand"><span className="brand-mark" aria-hidden="true">C</span><span>Career</span></div>
        <div className="workspace-switcher"><small>当前工作区</small><strong>{workspace.name}</strong><span>{bootstrap?.product ?? '本机工作区'}</span></div>
        {nav()}
        <div className="sidebar-footer"><span className={`sync-dot sync-dot--${syncState}`} /><span>{syncState === 'live' ? '状态已同步' : syncState === 'offline' ? '离线 · 禁止写入' : syncState === 'connecting' ? '正在连接' : '正在重新同步'}</span><small>{lastSyncedAt ? `最近同步 ${new Date(lastSyncedAt).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}` : '最近同步 —'}</small></div>
      </aside>
      <div className="app-main">
        <header className="topbar">
          <div><p className="eyebrow">{workspace.name}</p><h2>{selectedPage?.label ?? '工作台'}</h2></div>
          <div className="topbar-actions">
            <span className={`connection-chip ${canWrite ? 'connection-chip--online' : 'connection-chip--offline'}`}><span aria-hidden="true" />{!online ? '离线，只读' : syncState === 'live' ? '实时同步' : '同步中，暂不可写'}</span>
            <button type="button" className="icon-button" onClick={() => void signOut()} title="退出此浏览器会话">退出</button>
          </div>
        </header>
        <div className="mobile-navigation">{nav(true)}</div>
        {!canWrite && <div className="sync-banner" role="status">{!online ? '网络已断开。页面保留最近同步的工作区状态，所有写操作已暂停。' : '正在读取最新工作区状态和变更。写操作暂不可用。'}</div>}
        {workspace.executionAvailable !== true && <div className="execution-banner" role="status" aria-live="polite"><div><strong>仍在检查上次任务</strong><p>检查完成前暂不能开始新的模型执行。</p><small>任务读取、草稿编辑和连接设置保存仍可用；能力测试与新执行命令已暂停。</small>{workspace.executionMessage && <details><summary>查看检查详情</summary><p>{workspace.executionMessage}</p></details>}</div></div>}
        {commandConflict && <div className="conflict-banner" role="status"><div><strong>任务版本冲突</strong><p>提交依据 v{conflictSubmittedVersion ?? '未知'}；冲突发生时当前版本 v{commandConflict.version}。审批备注等本地输入仍保留。</p>
          <details className="conflict-comparison"><summary>比较任务版本</summary><div className="comparison-grid">
            <section><strong>{conflictPrevious ? '已保存的提交前快照' : '提交时的旧快照'}</strong>{conflictPrevious ? <><p>{conflictPrevious.title}</p><small>{statusLabels[conflictPrevious.status] ?? conflictPrevious.status} · 修订 {conflictPrevious.revision} · v{conflictPrevious.version}</small><pre>{conflictPrevious.goal}</pre></> : <p>未保留版本 v{conflictSubmittedVersion ?? '未知'} 的旧任务快照。{conflictHasDecisionDialog ? '固定审批或成果内容仍在原对话框中，备注已保留。' : '本地未提交输入仍保留。'}</p>}</section>
            <section><strong>当前任务状态</strong><p>{commandConflict.title}</p><small>{statusLabels[commandConflict.status] ?? commandConflict.status} · 修订 {commandConflict.revision} · v{commandConflict.version}</small><pre>{commandConflict.goal}</pre></section>
          </div></details>
        </div><button type="button" className="button" onClick={() => { setSelectedTaskId(commandConflict.id); openWorkbenchSection('tasks'); setCommandConflict(null); setConflictPrevious(null); setConflictSubmittedVersion(null); setConflictHasDecisionDialog(false); }}>打开当前任务</button></div>}
        {unresolvedIds.length > 0 && <section className="receipt-banner" aria-label="待核对命令回执">
          <div><strong>有 {unresolvedIds.length} 个命令结果需要核对</strong><p>先查原 requestId 的接纳回执。页面不会自动生成新键重提。</p></div>
          <div className="receipt-items">{unresolvedIds.map((id) => <div key={id} className="receipt-item"><code>{id}</code><span>{receiptNotes[id] || '等待查询'}</span><div className="button-row">
            <button type="button" className="button button--small" disabled={!online || commandBusy} onClick={() => void reconcileReceipt(id)}>查询回执</button>
            {receiptNotes[id]?.startsWith('暂未找到') && tracker.pendingById(id) && !tracker.activeById(id) && <button type="button" className="button button--small" disabled={!online || commandBusy} onClick={() => beginReceiptRecovery(id)}>重新填写并使用原请求标识提交</button>}
            {receiptNotes[id]?.startsWith('暂未找到') && tracker.activeById(id) && <button type="button" className="button button--small" disabled={retryOriginalDisabled(id)} onClick={() => void retryOriginal(id)}>以原 requestId 重试原请求</button>}
          </div></div>)}</div>
        </section>}
        {commandError && !commandConflict && <div className="notice notice--error notice--dismiss"><p>{commandError}</p><button type="button" className="icon-button" aria-label="关闭提示" onClick={() => setCommandError('')}>×</button></div>}
        <main id="main-content" className="page-content" tabIndex={-1}>
          {page === 'workbench' && <nav className="workbench-nav" aria-label="工作台内容">
            {workbenchSections.map((section) => <button key={section.id} type="button" className={workbenchSection === section.id ? 'workbench-nav__item workbench-nav__item--active' : 'workbench-nav__item'} aria-current={workbenchSection === section.id ? 'page' : undefined} onClick={() => setWorkbenchSection(section.id)}>{section.label}</button>)}
          </nav>}
          <div hidden={page !== 'workbench' || workbenchSection !== 'overview'}><Workbench workspace={workspace} tasks={taskSnapshots} selectedTaskId={selectedTaskId} online={canWrite} busy={commandBusy} error={commandError} recoveryRequestId={activeRecoveryMetadata?.route === '/api/tasks' ? recoveryRequestId : null} runCommand={runCommand} onCreated={onCreated} onSelectTask={(id) => { setSelectedTaskId(id); openWorkbenchSection('tasks'); }} onSettings={() => setPage('settings')} /></div>
          <div hidden={page !== 'workbench' || workbenchSection !== 'tasks'}><TasksPage workspace={workspace} tasks={taskSnapshots} selectedTaskId={selectedTaskId} online={canWrite} executionAvailable={workspace.executionAvailable === true} commandBusy={commandBusy} recoveryRequestId={associatedRecoveryTaskId === selectedTaskId ? recoveryRequestId : null} recoveryCommand={associatedRecoveryTaskId === selectedTaskId ? activeRecoveryMetadata ?? null : null} prepareCommandRecovery={prepareCommandRecovery} runCommand={runCommand} onSelectTask={setSelectedTaskId} onDownload={downloadArtifact} /></div>
          <div hidden={page !== 'workbench' || workbenchSection !== 'approvals'}><ApprovalsPage tasks={taskSnapshots} detailLoadPending={detailLoad.pending.length} detailLoadFailed={detailLoad.failed.length} onRetryMissing={() => setDetailRetry((current) => current + 1)} onOpenTask={(id) => { setSelectedTaskId(id); openWorkbenchSection('tasks'); }} /></div>
          <div hidden={page !== 'workbench' || workbenchSection !== 'artifacts'}><ArtifactsPage tasks={taskSnapshots} detailLoadPending={detailLoad.pending.length} detailLoadFailed={detailLoad.failed.length} onRetryMissing={() => setDetailRetry((current) => current + 1)} onOpenTask={(id) => { setSelectedTaskId(id); openWorkbenchSection('tasks'); }} onDownload={downloadArtifact} /></div>
          <div hidden={page !== 'projects'}><PlaceholderPage title="项目" phase="项目管理" description="查看项目目标、需求、任务看板与成果；项目管理将在后续阶段开放。" /></div>
          <div hidden={page !== 'team'}><PlaceholderPage title="团队" phase="团队协作" description="查看成员职责、当前负载与能力；团队协作将在后续阶段开放。" /></div>
          <div hidden={page !== 'materials'}><PlaceholderPage title="资料" phase="知识库与记忆" description="导入资料、查看来源并管理记忆；资料能力将在后续阶段开放。" /></div>
          <div hidden={page !== 'settings'}>
            <ConnectionSettings active={page === 'settings'} connection={workspace.connection as ConnectionResponse | null} busy={commandBusy} online={canWrite} executionAvailable={workspace.executionAvailable === true} executionMessage={workspace.executionMessage} error={commandError} recoveryRequestId={activeRecoveryMetadata?.route.startsWith('/api/connection') ? recoveryRequestId : null} recoveryCommand={activeRecoveryMetadata?.route.startsWith('/api/connection') ? activeRecoveryMetadata : null} runCommand={runCommand} reload={() => refreshWorkspace()} />
            <section className="panel stage-note"><p className="eyebrow">工作区管理</p><h2>后续设置</h2><p>工具连接、邮箱、备份与访问管理将在后续阶段开放。当前不显示示例内容或模拟状态。</p></section>
          </div>
        </main>
        <footer className="app-footer"><span>保存在本机工作区</span></footer>
      </div>
    </div>
  );
}
