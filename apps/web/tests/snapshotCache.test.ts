import assert from 'node:assert/strict';
import test from 'node:test';
import { shouldApplyTaskSnapshot, shouldApplyWorkspaceSnapshot } from '../src/state/snapshotCache.ts';
import type { TaskResponse, WorkspaceResponse } from '../src/contracts.ts';

const task = (id: string, version: number, seq: number): TaskResponse => ({
  id, title: '', goal: '', criteria: [], inputText: '', mode: 'office', project: null, owner: '',
  status: 'Running', revision: 1, version,
  plan: { id: '', version: 1, proposalHash: '', status: 'Confirmed', scopeDescription: '' },
  runs: [], approvals: [], artifacts: [], acceptances: [], activity: [], createdAt: '', updatedAt: '',
  snapshotSeq: seq, availableActions: [],
});

test('a late reply for task A cannot replace selected task B', () => {
  assert.equal(shouldApplyTaskSnapshot('task-b', task('task-b', 2, 8), task('task-a', 99, 99)), false);
});

test('older snapshots cannot move a task or workspace backwards', () => {
  assert.equal(shouldApplyTaskSnapshot('task-a', task('task-a', 4, 12), task('task-a', 3, 11)), false);
  const current = { snapshotSeq: 12 } as WorkspaceResponse;
  assert.equal(shouldApplyWorkspaceSnapshot(current, { snapshotSeq: 11 } as WorkspaceResponse), false);
  assert.equal(shouldApplyWorkspaceSnapshot(current, { snapshotSeq: 13 } as WorkspaceResponse), true);
});
