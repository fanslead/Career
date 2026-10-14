import assert from 'node:assert/strict';
import test from 'node:test';
import { createLatestReadGate } from './latestReadGate.mjs';

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

test('late response from a previous selection cannot replace the current run', async () => {
  const gate = createLatestReadGate();
  const displayed = [];
  const failures = [];
  const oldRequest = deferred();
  const currentRequest = deferred();

  gate.select('A');
  const oldRead = gate.read('A', () => oldRequest.promise, (value) => displayed.push(value), (error) => failures.push(error));
  gate.select('B');
  const currentRead = gate.read('B', () => currentRequest.promise, (value) => displayed.push(value), (error) => failures.push(error));

  currentRequest.resolve({ id: 'B' });
  assert.equal(await currentRead, true);
  oldRequest.resolve({ id: 'A' });
  assert.equal(await oldRead, false);
  assert.deepEqual(displayed, [{ id: 'B' }]);
  assert.deepEqual(failures, []);
});

test('newer refresh of the same run wins and stale failures are ignored', async () => {
  const gate = createLatestReadGate();
  const displayed = [];
  const failures = [];
  const oldRequest = deferred();
  const newRequest = deferred();

  gate.select('A');
  const oldRead = gate.read('A', () => oldRequest.promise, (value) => displayed.push(value), (error) => failures.push(error));
  const newRead = gate.read('A', () => newRequest.promise, (value) => displayed.push(value), (error) => failures.push(error));

  newRequest.resolve({ id: 'A', version: 2 });
  assert.equal(await newRead, true);
  oldRequest.reject(new Error('stale read failed'));
  assert.equal(await oldRead, false);
  assert.deepEqual(displayed, [{ id: 'A', version: 2 }]);
  assert.deepEqual(failures, []);
});

test('invalidating the same selection before a list read prevents a late detail response from restoring ready state', async () => {
  const gate = createLatestReadGate();
  const pending = deferred();
  let state = 'loading';

  gate.select('A');
  const oldPoll = gate.read('A', () => pending.promise, () => { state = 'ready'; }, () => { state = 'error'; });

  // refreshRuns invalidates detail reads while its list request is pending.
  gate.select('A');
  state = 'loading';
  // The current list request fails and exposes its error before oldPoll settles.
  state = 'error';
  pending.resolve({ id: 'A', status: 'Succeeded' });

  assert.equal(await oldPoll, false);
  assert.equal(state, 'error');
});

test('the current read reports failures without clearing the displayed result', async () => {
  const gate = createLatestReadGate();
  const displayed = [{ id: 'A', version: 1 }];
  const failures = [];
  gate.select('A');

  assert.equal(await gate.read('A', async () => { throw new Error('offline'); }, (value) => displayed.push(value), (error) => failures.push(error.message)), false);
  assert.deepEqual(displayed, [{ id: 'A', version: 1 }]);
  assert.deepEqual(failures, ['offline']);
});
