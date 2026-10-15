export function taskDetailsNeedingRefresh(
  summaries: readonly { id: string; version: number }[],
  cached: ReadonlyMap<string, { version: number }>,
): string[] {
  return summaries
    .filter((summary) => {
      const current = cached.get(summary.id);
      return !current || current.version < summary.version;
    })
    .map((summary) => summary.id);
}

export async function loadTaskDetailsBatch<T>(
  taskIds: readonly string[],
  concurrency: number,
  signal: AbortSignal,
  load: (taskId: string) => Promise<T>,
  onLoaded: (taskId: string, value: T) => void,
  onFailed: (taskId: string, error: unknown) => void,
): Promise<void> {
  const ids = [...new Set(taskIds)];
  const workerCount = Math.min(Math.max(1, Math.floor(concurrency)), ids.length);
  let nextIndex = 0;

  await Promise.all(Array.from({ length: workerCount }, async () => {
    while (!signal.aborted) {
      const taskId = ids[nextIndex++];
      if (taskId === undefined) return;

      let value: T;
      try {
        value = await load(taskId);
      } catch (error) {
        if (!signal.aborted) onFailed(taskId, error);
        continue;
      }
      if (!signal.aborted) onLoaded(taskId, value);
    }
  }));
}
