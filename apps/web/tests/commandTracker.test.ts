import assert from 'node:assert/strict';
import test from 'node:test';
import { CommandTracker, type StorageLike } from '../src/state/commandTracker.ts';

class MemoryStorage implements StorageLike {
  private values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}

test('same active action keeps its requestId and pending metadata excludes the request body', () => {
  const storage = new MemoryStorage();
  const tracker = new CommandTracker(storage);
  const first = tracker.begin('task-a:approve', '/api/approvals/approval-a/decision', (requestId) => ({
    requestId,
    expectedVersion: 5,
    note: 'private approval note',
    apiKey: 'private credential',
    inputText: 'private task input',
  }));
  const repeated = tracker.begin('task-a:approve', '/api/approvals/approval-a/decision', (requestId) => ({ requestId, expectedVersion: 6 }));
  assert.equal(repeated.requestId, first.requestId);
  const persisted = storage.getItem('career.pending-command-ids.v1')!;
  assert.deepEqual(JSON.parse(persisted), {
    version: 2,
    commands: [{ requestId: first.requestId, key: 'task-a:approve', route: '/api/approvals/approval-a/decision', method: 'POST' }],
    legacyUnknownIds: [],
  });
  assert.doesNotMatch(persisted, /private approval note|private credential|private task input/);

  const restored = new CommandTracker(storage);
  assert.deepEqual(restored.unresolvedIds(), [first.requestId]);
  assert.equal(restored.pendingForKey('task-a:approve')?.requestId, first.requestId);
  assert.equal(restored.activeFor('task-a:approve'), undefined);
  assert.equal(restored.activeById(first.requestId), undefined);
  assert.throws(() => restored.begin('task-a:approve', '/api/approvals/approval-a/decision', (requestId) => ({ requestId })), /pending receipt/);
});

test('resolving a receipt removes its metadata and releases the command key', () => {
  const storage = new MemoryStorage();
  const first = new CommandTracker(storage).begin('task-a:create', '/api/tasks', (requestId) => ({ requestId, title: 'private title' }));
  const restored = new CommandTracker(storage);

  restored.resolveReceipt(first.requestId);
  assert.deepEqual(restored.unresolvedIds(), []);
  assert.equal(restored.pendingForKey('task-a:create'), undefined);
  assert.deepEqual(JSON.parse(storage.getItem('career.pending-command-ids.v1')!), { version: 2, commands: [], legacyUnknownIds: [] });

  const next = new CommandTracker(storage).begin('task-a:create', '/api/tasks', (requestId) => ({ requestId }));
  assert.notEqual(next.requestId, first.requestId);
});

test('legacy UUID-only receipts migrate to a global new-command block', () => {
  const storage = new MemoryStorage();
  storage.setItem('career.pending-command-ids.v1', JSON.stringify(['legacy-request-id']));
  const tracker = new CommandTracker(storage);

  assert.deepEqual(tracker.unknownPendingIds(), ['legacy-request-id']);
  assert.deepEqual(JSON.parse(storage.getItem('career.pending-command-ids.v1')!), {
    version: 2,
    commands: [],
    legacyUnknownIds: ['legacy-request-id'],
  });
  let bodyCreated = false;
  assert.throws(() => tracker.begin('unrelated:new-task', '/api/tasks', () => { bodyCreated = true; return {}; }), /legacy command receipts/);
  assert.equal(bodyCreated, false);

  tracker.resolveReceipt('legacy-request-id');
  assert.deepEqual(tracker.unknownPendingIds(), []);
  assert.ok(tracker.begin('unrelated:new-task', '/api/tasks', (requestId) => ({ requestId })));
});

test('a reloaded command can be reconstructed with the original request ID, route, and method', () => {
  const storage = new MemoryStorage();
  const original = new CommandTracker(storage).begin('task-a:edit-plan', '/api/tasks/task-a', (requestId) => ({
    requestId, expectedVersion: 4, goal: 'old private goal', secret: 'must not persist',
  }), 'PATCH');
  const restored = new CommandTracker(storage);
  assert.equal(restored.enableRecovery(original.requestId), true);
  assert.throws(() => restored.begin(original.key, '/api/tasks/other', (requestId) => ({ requestId }), 'PATCH'), /original command route and method/);
  assert.throws(() => restored.begin(original.key, original.route, (requestId) => ({ requestId }), 'POST'), /original command route and method/);
  assert.equal(restored.recoveryIsEnabled(original.requestId), true);

  const reconstructed = restored.begin(original.key, original.route, (requestId) => ({ requestId, expectedVersion: 5, goal: 're-entered goal' }), original.method);
  assert.equal(reconstructed.requestId, original.requestId);
  assert.equal(reconstructed.key, original.key);
  assert.equal(reconstructed.route, original.route);
  assert.equal(reconstructed.method, original.method);
  assert.deepEqual(reconstructed.body, { requestId: original.requestId, expectedVersion: 5, goal: 're-entered goal' });
  assert.equal(reconstructed.recovered, true);
  assert.doesNotMatch(storage.getItem('career.pending-command-ids.v1')!, /old private goal|must not persist|re-entered goal/);
});

test('a 409 recovery marker survives dropping its in-memory body until explicit re-entry', () => {
  const storage = new MemoryStorage();
  const tracker = new CommandTracker(storage);
  const command = tracker.begin('approval:approval-a:approve', '/api/approvals/approval-a/decision', (requestId) => ({ requestId, note: 'private note' }));

  tracker.retainForRecovery(command);
  assert.deepEqual(tracker.unresolvedIds(), [command.requestId]);
  assert.equal(tracker.pendingById(command.requestId)?.route, command.route);
  assert.equal(tracker.activeById(command.requestId), undefined);
  assert.throws(() => tracker.begin(command.key, command.route, (requestId) => ({ requestId, note: 'new note' })), /pending receipt/);

  assert.equal(tracker.enableRecovery(command.requestId), true);
  const retried = tracker.begin(command.key, command.route, (requestId) => ({ requestId, note: 'user explicitly re-entered' }));
  assert.equal(retried.requestId, command.requestId);
  assert.equal(retried.body.note, 'user explicitly re-entered');
});
