import type { TaskResponse, WorkspaceResponse } from '../contracts.ts';

export function shouldApplyTaskSnapshot(
  requestedTaskId: string,
  current: TaskResponse | undefined,
  incoming: TaskResponse,
): boolean {
  if (incoming.id !== requestedTaskId) return false;
  if (!current || current.id !== incoming.id) return true;
  return incoming.version >= current.version && incoming.snapshotSeq >= current.snapshotSeq;
}

export function shouldApplyWorkspaceSnapshot(
  current: WorkspaceResponse | undefined,
  incoming: WorkspaceResponse,
): boolean {
  return !current || incoming.snapshotSeq >= current.snapshotSeq;
}

export function latestTaskSnapshots(
  tasks: ReadonlyMap<string, TaskResponse>,
  summaries: WorkspaceResponse['tasks'],
): TaskResponse[] {
  const ids = new Set(summaries.map((task) => task.id));
  return [...tasks.values()].filter((task) => ids.has(task.id));
}
