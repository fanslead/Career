import type { ApprovalResponse, ArtifactResponse, TaskResponse } from '../contracts.ts';

export type DecisionSnapshot =
  | { kind: 'approval'; item: ApprovalResponse; decision: 'approve' | 'reject'; taskVersion: number; taskRevision: number }
  | { kind: 'acceptance'; item: ArtifactResponse; decision: 'accept' | 'return'; taskVersion: number; taskRevision: number };

export function canRebaseDecision(snapshot: DecisionSnapshot, task: TaskResponse): boolean {
  if (snapshot.kind === 'approval') {
    const current = task.approvals.find((approval) => approval.id === snapshot.item.id);
    return task.availableActions.includes('view-approval')
      && current?.status === 'Pending'
      && current.payloadHash === snapshot.item.payloadHash
      && current.toolName === snapshot.item.toolName
      && current.target === snapshot.item.target
      && current.content === snapshot.item.content;
  }

  const current = task.artifacts
    .filter((artifact) => artifact.taskRevision === task.revision)
    .slice()
    .sort((left, right) => right.version - left.version)[0];
  return task.status === 'InReview'
    && task.revision === snapshot.taskRevision
    && task.availableActions.includes(snapshot.decision)
    && current?.id === snapshot.item.id
    && current.version === snapshot.item.version
    && current.taskRevision === snapshot.item.taskRevision
    && current.contentHash === snapshot.item.contentHash
    && !task.acceptances.some((acceptance) => acceptance.artifactId === current.id && acceptance.artifactVersion === current.version);
}
