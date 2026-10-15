import { api, ConnectionLostError } from './client.ts';
import type { TaskResponse, WorkspaceResponse } from '../contracts.ts';
import { shouldApplyTaskSnapshot, shouldApplyWorkspaceSnapshot } from '../state/snapshotCache.ts';
import { waitForRetry } from '../state/retryWait.ts';
export { waitForRetry, type RetryTimerApi } from '../state/retryWait.ts';

export type SyncState = 'connecting' | 'live' | 'reconnecting' | 'offline';

export interface SyncSink {
  workspace: WorkspaceResponse | undefined;
  tasks: Map<string, TaskResponse>;
  setWorkspace(value: WorkspaceResponse): void;
  setTask(id: string, value: TaskResponse): void;
  setState(value: SyncState): void;
  getSelectedTaskId(): string | null;
}

export async function runSyncLoop(sink: SyncSink, signal: AbortSignal): Promise<void> {
  let watermark = sink.workspace?.snapshotSeq ?? 0;
  while (!signal.aborted) {
    if (!navigator.onLine) {
      sink.setState('offline');
      await waitForRetry(1500, signal);
      continue;
    }
    sink.setState(watermark ? 'reconnecting' : 'connecting');
    try {
      const snapshot = await api.workspace();
      if (shouldApplyWorkspaceSnapshot(sink.workspace, snapshot)) sink.setWorkspace(snapshot);
      watermark = Math.max(watermark, snapshot.snapshotSeq);
      const selectedId = sink.getSelectedTaskId() ?? snapshot.tasks[0]?.id ?? null;
      if (selectedId) {
        try {
          const task = await api.task(selectedId);
          const current = sink.tasks.get(selectedId);
          if (shouldApplyTaskSnapshot(selectedId, current, task)) sink.setTask(selectedId, task);
        } catch (error) {
          if (!(error instanceof Error && 'status' in error && (error as { status: number }).status === 404)) throw error;
        }
      }
      if (signal.aborted) break;
      sink.setState('live');
      await api.events(watermark, signal, (event) => {
        watermark = Math.max(watermark, event.seq);
        void refreshAfterEvent(sink, event.taskId);
      });
    } catch (error) {
      if (signal.aborted) break;
      if (error instanceof ConnectionLostError && !navigator.onLine) sink.setState('offline');
      else sink.setState('reconnecting');
      await waitForRetry(1500, signal);
    }
  }
}

async function refreshAfterEvent(sink: SyncSink, taskId: string | null): Promise<void> {
  try {
    const workspace = await api.workspace();
    if (shouldApplyWorkspaceSnapshot(sink.workspace, workspace)) sink.setWorkspace(workspace);
  } catch {
    return;
  }
  if (!taskId) return;
  try {
    const task = await api.task(taskId);
    const current = sink.tasks.get(taskId);
    if (shouldApplyTaskSnapshot(taskId, current, task)) sink.setTask(taskId, task);
  } catch {
    // The next event or reconnect snapshot will refresh this task.
  }
}
