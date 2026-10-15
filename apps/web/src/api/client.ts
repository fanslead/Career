import type {
  BootstrapResponse,
  CommandReceiptResponse,
  ConnectionResponse,
  EventResponse,
  ProblemResponse,
  SessionResponse,
  TaskResponse,
  WorkspaceResponse,
} from '../contracts.ts';

let accessToken: string | null = null;
let refreshInFlight: Promise<boolean> | null = null;
let sessionExpiredHandler: (() => void) | null = null;

const clientHeader = 'X-Career-Client';
const clientId = 'web';

export class ApiProblem extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    readonly detail: string,
    readonly requestId: string | null,
    readonly current: TaskResponse | null,
  ) {
    super(detail || code || `HTTP ${status}`);
    this.name = 'ApiProblem';
  }
}

export class ConnectionLostError extends Error {
  constructor(message = '无法连接工作区服务。') {
    super(message);
    this.name = 'ConnectionLostError';
  }
}

export function setAccessToken(value: string | null): void {
  accessToken = value;
}

export function hasAccessToken(): boolean {
  return accessToken !== null;
}

export function setSessionExpiredHandler(handler: (() => void) | null): void {
  sessionExpiredHandler = handler;
}

function expireSession(): void {
  setAccessToken(null);
  sessionExpiredHandler?.();
}

async function refreshSession(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    try {
      const response = await fetch('/api/auth/refresh', {
        method: 'POST',
        credentials: 'include',
        headers: { Accept: 'application/json', [clientHeader]: clientId },
      });
      if (!response.ok) {
        expireSession();
        return false;
      }
      const session = (await response.json()) as SessionResponse;
      setAccessToken(session.accessToken);
      return true;
    } catch {
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

async function readFailure(response: Response): Promise<ApiProblem> {
  let value: Partial<ProblemResponse> = {};
  try {
    value = (await response.json()) as Partial<ProblemResponse>;
  } catch {
    // Preserve the HTTP status if a host or proxy returned a non-problem body.
  }
  return new ApiProblem(
    response.status,
    value.code ?? 'http_error',
    value.detail ?? value.title ?? `服务返回 ${response.status}`,
    value.requestId ?? null,
    value.current ?? null,
  );
}

export async function request<T>(path: string, init: RequestInit = {}, auth = true): Promise<T> {
  if (init.method && init.method.toUpperCase() !== 'GET' && !navigator.onLine) {
    throw new ConnectionLostError('当前处于离线状态，操作未提交。恢复连接后请先检查工作区。');
  }

  const send = async (): Promise<Response> => {
    const headers = new Headers(init.headers);
    headers.set('Accept', 'application/json');
    headers.set(clientHeader, clientId);
    if (init.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
    if (auth && accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
    return fetch(path, { ...init, headers, credentials: 'include' });
  };

  let response: Response;
  try {
    response = await send();
  } catch {
    throw new ConnectionLostError();
  }
  if (auth && response.status === 401) {
    if (await refreshSession()) {
      try {
        response = await send();
      } catch {
        throw new ConnectionLostError();
      }
      if (response.status === 401) expireSession();
    }
  }
  if (!response.ok) throw await readFailure(response);
  if (response.status === 204) return undefined as T;
  return await response.json() as T;
}

export const api = {
  bootstrap: () => request<BootstrapResponse>('/api/bootstrap', {}, false),
  pair: (code: string) => request<SessionResponse>('/api/auth/pair', {
    method: 'POST', body: JSON.stringify({ code }),
  }, false),
  refresh: refreshSession,
  logout: () => request<void>('/api/auth/logout', { method: 'POST' }),
  workspace: () => request<WorkspaceResponse>('/api/workspace'),
  task: (id: string, signal?: AbortSignal) => request<TaskResponse>(`/api/tasks/${encodeURIComponent(id)}`, { signal }),
  connection: async () => (await request<ConnectionResponse | null | undefined>('/api/connection')) ?? null,
  receipt: (requestId: string) => request<CommandReceiptResponse>(`/api/commands/${encodeURIComponent(requestId)}`),
  command: <T>(route: string, body: Record<string, unknown>, method = 'POST') => request<T>(route, {
    method,
    body: JSON.stringify(body),
  }),
  events: async (after: number, signal: AbortSignal, onEvent: (event: EventResponse) => void) => {
    const headers = new Headers({ Accept: 'text/event-stream', [clientHeader]: clientId });
    if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`);
    let response: Response;
    try {
      response = await fetch(`/api/events?after=${after}`, { headers, credentials: 'include', signal });
    } catch {
      throw new ConnectionLostError();
    }
    if (response.status === 401) {
      if (await refreshSession()) {
        headers.set('Authorization', `Bearer ${accessToken}`);
        try {
          response = await fetch(`/api/events?after=${after}`, { headers, credentials: 'include', signal });
        } catch {
          throw new ConnectionLostError();
        }
        if (response.status === 401) expireSession();
      }
    }
    if (!response.ok || !response.body) throw await readFailure(response);
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true }).replaceAll('\r\n', '\n');
        let boundary: number;
        while ((boundary = buffer.indexOf('\n\n')) >= 0) {
          const block = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const data = block.split('\n').filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trimStart()).join('\n');
          if (data) {
            try {
              onEvent(JSON.parse(data) as EventResponse);
            } catch {
              // Ignore malformed frames; the next snapshot is authoritative.
            }
          }
        }
      }
    } finally {
      reader.releaseLock();
    }
  },
  download: async (artifactId: string, version: number) => {
    const url = `/api/artifacts/${encodeURIComponent(artifactId)}/versions/${version}/download`;
    const send = () => fetch(url, { headers: { [clientHeader]: clientId, ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}) }, credentials: 'include' });
    let response: Response;
    try { response = await send(); } catch { throw new ConnectionLostError(); }
    if (response.status === 401) {
      if (await refreshSession()) {
        try { response = await send(); } catch { throw new ConnectionLostError(); }
        if (response.status === 401) expireSession();
      }
    }
    if (!response.ok) throw await readFailure(response);
    return { blob: await response.blob(), name: filenameFrom(response.headers.get('Content-Disposition')) };
  },
};

function filenameFrom(disposition: string | null): string {
  const encoded = disposition?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  const plain = disposition?.match(/filename="?([^";]+)"?/i)?.[1];
  try {
    return decodeURIComponent(encoded ?? plain ?? '成果.md').replace(/[\\/\u0000-\u001f]/g, '_');
  } catch {
    return '成果.md';
  }
}
