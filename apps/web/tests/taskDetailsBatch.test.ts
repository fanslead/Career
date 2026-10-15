import assert from 'node:assert/strict';
import test from 'node:test';
import { loadTaskDetailsBatch, taskDetailsNeedingRefresh } from '../src/state/taskDetailsBatch.ts';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise; });
  return { promise, resolve, reject };
}

test('detail refresh selector includes missing and stale task snapshots only', () => {
  const ids = taskDetailsNeedingRefresh(
    [{ id: 'missing', version: 1 }, { id: 'stale', version: 5 }, { id: 'current', version: 5 }, { id: 'newer', version: 4 }],
    new Map([['stale', { version: 4 }], ['current', { version: 5 }], ['newer', { version: 6 }]]),
  );
  assert.deepEqual(ids, ['missing', 'stale']);
});

test('detail reads stay bounded and failed task IDs can be retried explicitly', async () => {
  const controller = new AbortController();
  const started: string[] = [];
  const loaded: string[] = [];
  const failed: string[] = [];
  const completions = new Map<string, ReturnType<typeof deferred<string>>>();
  const cStarted = deferred<void>();
  let active = 0;
  let maximumActive = 0;
  const load = (id: string) => {
    started.push(id);
    active++;
    maximumActive = Math.max(maximumActive, active);
    const item = deferred<string>();
    completions.set(id, item);
    if (id === 'c') cStarted.resolve();
    return item.promise.finally(() => { active--; });
  };

  const batch = loadTaskDetailsBatch(
    ['a', 'b', 'a', 'c'], 2, controller.signal, load,
    (id) => loaded.push(id), (id) => failed.push(id),
  );
  assert.deepEqual(started, ['a', 'b']);
  completions.get('a')!.resolve('A');
  await cStarted.promise;
  assert.deepEqual(started, ['a', 'b', 'c']);
  assert.equal(maximumActive, 2);
  completions.get('b')!.reject(new Error('temporary read failure'));
  completions.get('c')!.resolve('C');
  await batch;
  assert.deepEqual(loaded.sort(), ['a', 'c']);
  assert.deepEqual(failed, ['b']);

  const retryLoaded: string[] = [];
  await loadTaskDetailsBatch(['b'], 1, new AbortController().signal, async () => 'B', (id) => retryLoaded.push(id), () => assert.fail('retry should succeed'));
  assert.deepEqual(retryLoaded, ['b']);
});
