export interface RetryTimerApi {
  setTimeout(callback: () => void, ms: number): unknown;
  clearTimeout(timeout: unknown): void;
}

const browserTimers: RetryTimerApi = {
  setTimeout: (callback, ms) => window.setTimeout(callback, ms),
  clearTimeout: (timeout) => window.clearTimeout(timeout as number),
};

export const waitForRetry = (ms: number, signal: AbortSignal, timers: RetryTimerApi = browserTimers) => new Promise<void>((resolve) => {
  let settled = false;
  let timeout: unknown;
  let listenerAttached = false;
  const cleanup = () => signal.removeEventListener('abort', onAbort);
  const finish = () => {
    if (settled) return;
    settled = true;
    if (listenerAttached) cleanup();
    resolve();
  };
  const onAbort = () => {
    if (timeout !== undefined) timers.clearTimeout(timeout);
    finish();
  };
  timeout = timers.setTimeout(finish, ms);
  signal.addEventListener('abort', onAbort, { once: true });
  listenerAttached = true;
  if (settled) cleanup();
  if (signal.aborted) onAbort();
});
