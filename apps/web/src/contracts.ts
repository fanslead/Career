export interface BootstrapResponse {
  product: string;
  workspaceId: string;
  workspaceName: string;
  hasOwner: boolean;
  requiresPairing: boolean;
  protocolVersion: string;
}

export interface SessionResponse {
  accessToken: string;
  expiresAt: string;
  workspaceId: string;
}

export interface ConnectionResponse {
  id: string;
  name: string;
  protocol: string;
  endpoint: string;
  model: string;
  hasCredential: boolean;
  allowLocalEndpoint: boolean;
  testState: string;
  testMessage: string | null;
  testedAt: string | null;
  maxOutputTokens: number;
  maxModelCalls: number;
  maxInputCharacters: number;
}

export interface PlanResponse {
  id: string;
  version: number;
  proposalHash: string;
  status: string;
  scopeDescription: string;
}

export interface RunResponse {
  id: string;
  taskRevision: number;
  status: string;
  attemptId: string | null;
  attemptNumber: number;
  progress: string | null;
  failureCode: string | null;
  failureMessage: string | null;
  modelCalls: number;
  inputTokens: number;
  outputTokens: number;
  usageState: string;
  createdAt: string;
  endedAt: string | null;
}

export interface ApprovalResponse {
  id: string;
  runId: string;
  operationId: string;
  version: number;
  toolName: string;
  payloadHash: string;
  status: string;
  target: string;
  content: string;
  reason: string;
  createdAt: string;
  expiresAt: string;
  decisionNote: string | null;
}

export interface ArtifactResponse {
  id: string;
  runId: string;
  taskRevision: number;
  version: number;
  title: string;
  fileName: string;
  contentHash: string;
  sizeBytes: number;
  mediaType: string;
  content: string;
  sources: string[];
  checks: string[];
  unverified: string[];
  createdAt: string;
}

export interface AcceptanceResponse {
  id: string;
  taskRevision: number;
  artifactId: string;
  artifactVersion: number;
  contentHash: string;
  decision: string;
  note: string | null;
  createdAt: string;
}

export interface ActivityResponse {
  id: string;
  type: string;
  message: string;
  occurredAt: string;
}

export type AvailableAction =
  | 'confirm-plan'
  | 'edit-plan'
  | 'pause'
  | 'resume'
  | 'cancel'
  | 'retry'
  | 'view-approval'
  | 'view-artifact'
  | 'accept'
  | 'return';

export interface TaskResponse {
  id: string;
  title: string;
  goal: string;
  criteria: string[];
  inputText: string;
  mode: string;
  project: string | null;
  owner: string;
  status: string;
  revision: number;
  version: number;
  plan: PlanResponse;
  runs: RunResponse[];
  approvals: ApprovalResponse[];
  artifacts: ArtifactResponse[];
  acceptances: AcceptanceResponse[];
  activity: ActivityResponse[];
  createdAt: string;
  updatedAt: string;
  snapshotSeq: number;
  availableActions: AvailableAction[];
}

export interface TaskSummaryResponse {
  id: string;
  title: string;
  status: string;
  revision: number;
  version: number;
  owner: string;
  project: string | null;
  updatedAt: string;
  progress: string | null;
  pendingApprovals: number;
  pendingArtifacts: number;
}

export interface WorkspaceResponse {
  workspaceId: string;
  name: string;
  intakePaused: boolean;
  connection: ConnectionResponse | null;
  tasks: TaskSummaryResponse[];
  snapshotSeq: number;
  executionAvailable: boolean;
  executionMessage: string | null;
}

export interface EventResponse {
  seq: number;
  eventId: string;
  schemaVersion: number;
  occurredAt: string;
  taskId: string | null;
  runId: string | null;
  type: string;
  message: string;
}

export interface CommandReceiptResponse {
  requestId: string;
  route: string;
  taskId: string;
  version: number;
  acceptedAt: string;
}

export interface ProblemResponse {
  type: string;
  title: string;
  status: number;
  detail: string;
  code: string;
  requestId: string | null;
  current: TaskResponse | null;
}

export type TaskDraft = {
  title: string;
  goal: string;
  criteriaText: string;
  inputText: string;
  mode: 'office' | 'development' | 'business';
  project: string;
};
