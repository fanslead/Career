export const taskStatusLabels: Record<string, string> = {
  AwaitingPlan: '等待确认计划', Queued: '排队中', Running: '执行中', WaitingApproval: '等待审批',
  Stopping: '正在停止', Paused: '已暂停', Blocked: '需要处理', Failed: '执行失败',
  InReview: '等待验收', Done: '已验收完成', Cancelled: '已取消',
};

export const planStatusLabels: Record<string, string> = {
  Pending: '待确认', Confirmed: '已确认', Expired: '已失效',
};

const usageStatusLabels: Record<string, string> = {
  'not-started': '尚未调用',
  none: '尚未调用',
  pending: '用量待核对，费用未知',
  unknown: '用量待核对，费用未知',
  'pending-or-unknown': '用量待核对，费用未知',
  known: '用量已记录，费用未知',
  confirmed: '用量已确认，费用未知',
  'confirmed-price-unknown': '用量已确认，费用未知',
};

export function usageStatusLabel(status: string): string {
  return usageStatusLabels[status] ?? '用量状态待核对';
}

export const statusLabels: Record<string, string> = {
  ...taskStatusLabels,
  Queued: '排队中', Running: '执行中', WaitingApproval: '等待审批', Stopping: '正在停止',
  Paused: '已暂停', Reconciling: '正在核对效果', Failed: '执行失败', Succeeded: '已交付候选成果',
  Cancelled: '已取消', UnknownOutcome: '结果待核对',
};

const toolNameLabels: Record<string, string> = {
  publish_document: '发布成果文件',
};

export function toolNameLabel(identifier: string): string {
  return toolNameLabels[identifier] ?? identifier;
}
