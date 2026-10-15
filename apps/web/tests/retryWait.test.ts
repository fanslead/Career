import assert from 'node:assert/strict';
import test from 'node:test';
import { waitForRetry, type RetryTimerApi } from '../src/state/retryWait.ts';

function fakeSignal() {
  let aborted = false;
  const listeners = new Set<EventListenerOrEventListenerObject>();
  const signal = {
    get aborted() { return aborted; },
    addEventListener(_type: string, listener: EventListenerOrEventListenerObject) { listeners.add(listener); },
    removeEventListener(_type: string, listener: EventListenerOrEventListenerObject) { listeners.delete(listener); },
  } as unknown as AbortSignal;
  return {
    signal,
    listeners,
    abort() {
      aborted = true;
      for (const listener of [...listeners]) {
        if (typeof listener === 'function') listener(new Event('abort'));
        else listener.handleEvent(new Event('abort'));
      }
    },
  };
}

test('sync retry wait removes its abort listener after timer completion and abort', async () => {
  let timerCallback: (() => void) | undefined;
  let clearCount = 0;
  const timers: RetryTimerApi = {
    setTimeout(callback) { timerCallback = callback; return 'timer'; },
    clearTimeout(value) { assert.equal(value, 'timer'); clearCount++; },
  };
  const timed = fakeSignal();
  const timerWait = waitForRetry(10, timed.signal, timers);
  assert.equal(timed.listeners.size, 1);
  timerCallback!();
  await timerWait;
  assert.equal(timed.listeners.size, 0);
  assert.equal(clearCount, 0);

  const aborted = fakeSignal();
  const abortWait = waitForRetry(10, aborted.signal, timers);
  assert.equal(aborted.listeners.size, 1);
  aborted.abort();
  await abortWait;
  assert.equal(aborted.listeners.size, 0);
  assert.equal(clearCount, 1);
});
