const STORAGE_KEY = 'career.pending-command-ids.v1';
const STORAGE_VERSION = 2;

export interface PendingCommandMetadata {
  requestId: string;
  key: string;
  route: string;
  method: string;
}

interface PersistedCommandState {
  version: typeof STORAGE_VERSION;
  commands: PendingCommandMetadata[];
  legacyUnknownIds: string[];
}

export class PendingCommandError extends Error {
  readonly requestId: string;

  constructor(requestId: string, message: string) {
    super(message);
    this.name = 'PendingCommandError';
    this.requestId = requestId;
  }
}

export interface TrackedCommand {
  key: string;
  requestId: string;
  route: string;
  method: string;
  body: Record<string, unknown>;
  recovered: boolean;
}

export interface StorageLike {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export class CommandTracker {
  private readonly active = new Map<string, TrackedCommand>();
  private readonly unresolved = new Set<string>();
  private readonly pendingMetadata = new Map<string, PendingCommandMetadata>();
  private readonly legacyUnknownIds = new Set<string>();
  private readonly recoveryEnabled = new Set<string>();
  private readonly storage?: StorageLike;

  constructor(storage?: StorageLike) {
    this.storage = storage;
    this.restore();
  }

  activeFor(key: string): TrackedCommand | undefined {
    return this.active.get(key);
  }

  activeById(requestId: string): TrackedCommand | undefined {
    return [...this.active.values()].find((command) => command.requestId === requestId);
  }

  pendingForKey(key: string): PendingCommandMetadata | undefined {
    return [...this.pendingMetadata.values()].find((command) => command.key === key);
  }

  pendingForRoute(route: string, method: string): PendingCommandMetadata | undefined {
    return [...this.pendingMetadata.values()].find((command) => command.route === route && command.method === method);
  }

  pendingById(requestId: string): PendingCommandMetadata | undefined {
    return this.pendingMetadata.get(requestId);
  }

  enableRecovery(requestId: string): boolean {
    if (!this.pendingMetadata.has(requestId)) return false;
    this.recoveryEnabled.add(requestId);
    return true;
  }

  recoveryIsEnabled(requestId: string): boolean {
    return this.recoveryEnabled.has(requestId);
  }

  unknownPendingIds(): string[] {
    return [...this.legacyUnknownIds];
  }

  begin(key: string, route: string, createBody: (requestId: string) => Record<string, unknown>, method = 'POST'): TrackedCommand {
    const existing = this.active.get(key);
    if (existing) return existing;
    const unknownId = this.legacyUnknownIds.values().next().value as string | undefined;
    if (unknownId) {
      throw new PendingCommandError(unknownId, 'Unresolved legacy command receipts must be checked before starting a new command.');
    }
    const restored = this.pendingForKey(key);
    if (restored) {
      if (!this.recoveryEnabled.has(restored.requestId)) {
        throw new PendingCommandError(restored.requestId, 'This command key has a pending receipt and cannot be submitted again.');
      }
      if (restored.route !== route || restored.method !== method) {
        throw new PendingCommandError(restored.requestId, 'Recovery must use the original command route and method.');
      }
      const command: TrackedCommand = {
        key: restored.key,
        requestId: restored.requestId,
        route: restored.route,
        method: restored.method,
        body: createBody(restored.requestId),
        recovered: true,
      };
      this.active.set(key, command);
      this.recoveryEnabled.delete(restored.requestId);
      return command;
    }
    const requestId = crypto.randomUUID();
    const command: TrackedCommand = { key, requestId, route, method, body: createBody(requestId), recovered: false };
    this.active.set(key, command);
    this.pendingMetadata.set(requestId, { requestId, key, route, method });
    this.unresolved.add(requestId);
    this.persist();
    return command;
  }

  markUncertain(command: TrackedCommand): void {
    this.pendingMetadata.set(command.requestId, { requestId: command.requestId, key: command.key, route: command.route, method: command.method });
    this.unresolved.add(command.requestId);
    this.persist();
  }

  resolve(command: TrackedCommand): void {
    this.active.delete(command.key);
    this.pendingMetadata.delete(command.requestId);
    this.unresolved.delete(command.requestId);
    this.recoveryEnabled.delete(command.requestId);
    this.persist();
  }

  retainForRecovery(command: TrackedCommand): void {
    const active = this.active.get(command.key);
    if (active?.requestId === command.requestId) this.active.delete(command.key);
    this.recoveryEnabled.delete(command.requestId);
    this.persist();
  }

  unresolvedIds(): string[] {
    return [...this.unresolved];
  }

  resolveReceipt(requestId: string): void {
    this.unresolved.delete(requestId);
    this.pendingMetadata.delete(requestId);
    this.legacyUnknownIds.delete(requestId);
    this.recoveryEnabled.delete(requestId);
    for (const [key, command] of this.active) {
      if (command.requestId === requestId) this.active.delete(key);
    }
    this.persist();
  }

  private restore(): void {
    try {
      const value = this.storage?.getItem(STORAGE_KEY);
      const parsed: unknown = value ? JSON.parse(value) : null;
      if (Array.isArray(parsed)) {
        for (const id of parsed) {
          if (typeof id === 'string' && id.length > 0) {
            this.unresolved.add(id);
            this.legacyUnknownIds.add(id);
          }
        }
        if (this.legacyUnknownIds.size > 0) this.persist();
        return;
      }

      if (!parsed || typeof parsed !== 'object') return;
      const state = parsed as Partial<PersistedCommandState>;
      if (state.version !== STORAGE_VERSION) return;
      for (const command of Array.isArray(state.commands) ? state.commands : []) {
        if (!this.isMetadata(command)) continue;
        this.pendingMetadata.set(command.requestId, command);
        this.unresolved.add(command.requestId);
      }
      for (const id of Array.isArray(state.legacyUnknownIds) ? state.legacyUnknownIds : []) {
        if (typeof id !== 'string' || id.length === 0) continue;
        this.legacyUnknownIds.add(id);
        this.unresolved.add(id);
      }
    } catch {
      // A broken storage value cannot restore command bodies and is ignored.
    }
  }

  private isMetadata(value: unknown): value is PendingCommandMetadata {
    if (!value || typeof value !== 'object') return false;
    const command = value as Partial<PendingCommandMetadata>;
    return typeof command.requestId === 'string' && command.requestId.length > 0
      && typeof command.key === 'string' && command.key.length > 0
      && typeof command.route === 'string' && command.route.startsWith('/')
      && typeof command.method === 'string' && command.method.length > 0;
  }

  private persist(): void {
    try {
      const state: PersistedCommandState = {
        version: STORAGE_VERSION,
        commands: [...this.pendingMetadata.values()],
        legacyUnknownIds: [...this.legacyUnknownIds],
      };
      this.storage?.setItem(STORAGE_KEY, JSON.stringify(state));
    } catch {
      // The command remains recoverable in memory if browser storage is unavailable.
    }
  }
}
