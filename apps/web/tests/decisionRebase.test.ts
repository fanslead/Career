import assert from 'node:assert/strict';
import test from 'node:test';
import type { ApprovalResponse, ArtifactResponse, TaskResponse } from '../src/contracts.ts';
import { canRebaseDecision } from '../src/state/decisionRebase.ts';

const approval: ApprovalResponse = {
  id: 'approval-1', runId: 'run-1', operationId: 'operation-1', version: 1,
  toolName: 'file.write', payloadHash: 'sha-approval', status: 'Pending', target: 'report.txt',
  content: 'fixed content', reason: 'write report', createdAt: '', expiresAt: '', decisionNote: null,
};
const artifact: ArtifactResponse = {
  id: 'artifact-1', runId: 'run-1', taskRevision: 3, version: 1, title: 'Report', fileName: 'report.md',
  contentHash: 'sha-artifact', sizeBytes: 12, mediaType: 'text/markdown', content: 'report', sources: [], checks: [], unverified: [], createdAt: '',
};

function task(overrides: Partial<TaskResponse> = {}): TaskResponse {
  return {
    id: 'task-1', title: 'Task', goal: 'Goal', criteria: [], inputText: '', mode: 'office', project: null, owner: '',
    status: 'WaitingApproval', revision: 3, version: 8,
    plan: { id: '', version: 1, proposalHash: '', status: 'Confirmed', scopeDescription: '' },
    runs: [], approvals: [approval], artifacts: [], acceptances: [], activity: [], createdAt: '', updatedAt: '',
    snapshotSeq: 10, availableActions: ['view-approval'], ...overrides,
  };
}

test('approval decision can rebase only while the same pending fixed payload remains', () => {
  const snapshot = { kind: 'approval', item: approval, decision: 'approve', taskVersion: 7, taskRevision: 3 } as const;
  assert.equal(canRebaseDecision(snapshot, task()), true);
  assert.equal(canRebaseDecision(snapshot, task({ approvals: [{ ...approval, content: 'changed' }] })), false);
  assert.equal(canRebaseDecision(snapshot, task({ approvals: [{ ...approval, status: 'Approved' }] })), false);
});

test('acceptance decision can rebase only to the same latest artifact in the same review revision', () => {
  const snapshot = { kind: 'acceptance', item: artifact, decision: 'accept', taskVersion: 7, taskRevision: 3 } as const;
  const reviewTask = task({ status: 'InReview', approvals: [], artifacts: [artifact], availableActions: ['accept', 'return'] });
  assert.equal(canRebaseDecision(snapshot, reviewTask), true);
  assert.equal(canRebaseDecision(snapshot, task({ ...reviewTask, artifacts: [artifact, { ...artifact, version: 2 }] })), false);
  assert.equal(canRebaseDecision(snapshot, task({ ...reviewTask, artifacts: [{ ...artifact, contentHash: 'different' }] })), false);
  assert.equal(canRebaseDecision(snapshot, task({ ...reviewTask, acceptances: [{ id: 'accepted', taskRevision: 3, artifactId: artifact.id, artifactVersion: 1, contentHash: artifact.contentHash, decision: 'accept', note: null, createdAt: '' }] })), false);
});
