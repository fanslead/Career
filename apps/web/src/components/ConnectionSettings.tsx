import { useEffect, useState, type FormEvent } from 'react';
import type { ConnectionResponse } from '../contracts.ts';
import type { PendingCommandMetadata } from '../state/commandTracker.ts';

const empty = {
  name: '', protocol: 'chat-completions', endpoint: '', model: '', apiKey: '',
  allowLocalEndpoint: false, maxOutputTokens: 4096, maxModelCalls: 6, maxInputCharacters: 32000,
};

export function ConnectionSettings({
  active,
  connection,
  busy,
  online,
  executionAvailable,
  executionMessage,
  error,
  recoveryRequestId,
  recoveryCommand,
  runCommand,
  reload,
}: {
  active: boolean;
  connection: ConnectionResponse | null;
  busy: boolean;
  online: boolean;
  executionAvailable: boolean;
  executionMessage: string | null;
  error: string;
  recoveryRequestId: string | null;
  recoveryCommand: PendingCommandMetadata | null;
  runCommand<T>(key: string, route: string, makeBody: (requestId: string) => Record<string, unknown>, method?: string): Promise<{ ok: boolean; value?: T; error?: string }>;
  reload(): Promise<void>;
}) {
  const [draft, setDraft] = useState(empty);
  const [dirty, setDirty] = useState(false);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [message, setMessage] = useState('');
  const [localError, setLocalError] = useState('');

  useEffect(() => {
    if (dirty) return;
    setDraft({
      ...empty,
      name: connection?.name ?? '', protocol: connection?.protocol ?? empty.protocol,
      endpoint: connection?.endpoint ?? '', model: connection?.model ?? '',
      allowLocalEndpoint: connection?.allowLocalEndpoint ?? false,
      maxOutputTokens: connection?.maxOutputTokens ?? empty.maxOutputTokens,
      maxModelCalls: connection?.maxModelCalls ?? empty.maxModelCalls,
      maxInputCharacters: connection?.maxInputCharacters ?? empty.maxInputCharacters,
    });
  }, [connection?.id, connection?.testState, dirty]);

  useEffect(() => {
    if (active) return;
    setDraft((current) => current.apiKey ? { ...current, apiKey: '' } : current);
  }, [active]);

  useEffect(() => {
    if (recoveryRequestId && recoveryCommand?.route === '/api/connection' && recoveryCommand.method === 'PUT') {
      setDirty(true);
      setLocalError('');
    }
  }, [recoveryRequestId, recoveryCommand]);

  function update<K extends keyof typeof empty>(key: K, value: typeof empty[K]) {
    setDirty(true);
    setMessage('');
    setLocalError('');
    setDraft((current) => ({ ...current, [key]: value }));
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setMessage('');
    setLocalError('');
    setSaving(true);
    try {
      const outcome = await runCommand<ConnectionResponse>('connection:save', '/api/connection', (requestId) => ({
        requestId, name: draft.name.trim(), protocol: draft.protocol,
        endpoint: draft.endpoint.trim(), model: draft.model.trim(), apiKey: draft.apiKey || null,
        allowLocalEndpoint: draft.allowLocalEndpoint, maxOutputTokens: Number(draft.maxOutputTokens),
        maxModelCalls: Number(draft.maxModelCalls), maxInputCharacters: Number(draft.maxInputCharacters),
      }), 'PUT');
      if (!outcome.ok) { setLocalError(outcome.error || '连接设置未提交。'); return; }
      setDraft((current) => ({ ...current, apiKey: '' }));
      try { await reload(); }
      catch {
        setMessage('保存请求已接收，但当前无法刷新连接状态。重新同步后会显示连接信息。');
        return;
      }
      setDirty(false);
      setMessage('连接设置已读取回工作区。能力测试仍需单独运行。');
    } finally {
      setSaving(false);
    }
  }

  async function testConnection() {
    setMessage('');
    setLocalError('');
    setTesting(true);
    try {
      const outcome = await runCommand<ConnectionResponse>('connection:test', '/api/connection/test', (requestId) => ({ requestId }));
      if (!outcome.ok) { setLocalError(outcome.error || '能力测试未提交。'); return; }
      try { await reload(); }
      catch {
        setMessage('能力测试请求已接收，但暂时无法读取测试结果。重新连接后请再次查看测试状态。');
        return;
      }
      setDirty(false);
      setMessage('能力测试结果已从工作区刷新。请查看下方测试状态。');
    } finally {
      setTesting(false);
    }
  }

  const testState = connection?.testState.toLowerCase();

  return (
    <section className="stack">
      <header className="page-heading">
        <div>
          <p className="eyebrow">连接与预算</p>
          <h1>模型设置</h1>
          <p className="lede">设置一个 OpenAI 兼容 Chat Completions 连接。保存配置与能力测试分开进行。</p>
        </div>
      </header>

      {connection && (
        <div className="panel connection-status" aria-live="polite">
          <div>
            <strong>{connection.name}</strong>
            <p>{connection.protocol} · {connection.model} · {connection.hasCredential ? '密钥已配置' : '尚未配置密钥'}</p>
          </div>
          <span className={`status-tag ${dirty || testState === 'failed' ? 'status-tag--warn' : testState === 'verified' ? 'status-tag--good' : 'status-tag--pending'}`}>
            {dirty ? '参数已改，保存后重新测试' : testState === 'verified' ? '能力已验证' : testState === 'failed' ? '测试未通过' : '待测试'}
          </span>
          {!dirty && connection.testMessage && <p className="connection-status__message">{connection.testMessage}</p>}
          {dirty && connection.testMessage && <details><summary>查看参数修改前的测试结果</summary><p>{connection.testMessage}</p></details>}
        </div>
      )}

      {!executionAvailable && <div className="notice notice--warn"><p>工作区尚未完成上次任务检查。保存连接设置仍可用，能力测试暂不可运行。</p>{executionMessage && <details><summary>查看检查详情</summary><p>{executionMessage}</p></details>}</div>}

      {recoveryRequestId && recoveryCommand && <div className="notice notice--warn" role="status"><strong>原连接命令等待你检查后重提</strong><p>浏览器只保存原 requestId、路径和方法（{recoveryCommand.method} {recoveryCommand.route}），没有保存请求正文或密钥：<code>{recoveryRequestId}</code></p><p>{recoveryCommand.route === '/api/connection' ? '请检查当前连接参数，并重新输入需要更换的密钥。' : '请确认连接参数和执行器状态后，使用原 requestId 再次测试。'}</p></div>}

      <form className="panel" onSubmit={save}>
        <fieldset className="settings-fields" disabled={busy || saving || testing}>
        <div className="form-grid">
          <label className="field">
            <span>连接名称</span>
            <input value={draft.name} onChange={(event) => update('name', event.currentTarget.value)} required maxLength={80} />
          </label>
          <label className="field">
            <span>协议</span>
            <select value={draft.protocol} onChange={(event) => update('protocol', event.currentTarget.value)}>
              <option value="chat-completions">OpenAI 兼容 Chat Completions</option>
            </select>
          </label>
          <label className="field field--wide">
            <span>服务地址</span>
            <input type="url" value={draft.endpoint} onChange={(event) => update('endpoint', event.currentTarget.value)} placeholder="https://api.example.com/v1" required />
          </label>
          <label className="field">
            <span>模型名称</span>
            <input value={draft.model} onChange={(event) => update('model', event.currentTarget.value)} required maxLength={160} />
          </label>
          <label className="field">
            <span>API 密钥</span>
            <input
              type="password" autoComplete="new-password" value={draft.apiKey}
              onChange={(event) => update('apiKey', event.currentTarget.value)}
              placeholder={connection?.hasCredential ? '已有密钥已保存；留空表示不更换' : '仅在此输入，不会回显'}
            />
          </label>
          <label className="field">
            <span>单次输出 token 上限</span>
            <input type="number" min={256} max={16000} value={draft.maxOutputTokens} onChange={(event) => update('maxOutputTokens', Number(event.currentTarget.value))} required />
          </label>
          <label className="field">
            <span>模型调用次数上限</span>
            <input type="number" min={1} max={10} value={draft.maxModelCalls} onChange={(event) => update('maxModelCalls', Number(event.currentTarget.value))} required />
          </label>
          <label className="field">
            <span>输入字符上限</span>
            <input type="number" min={100} max={100000} value={draft.maxInputCharacters} onChange={(event) => update('maxInputCharacters', Number(event.currentTarget.value))} required />
          </label>
          <div className="field field--wide check-setting">
            <label className="check" htmlFor="allow-local-endpoint">
              <input id="allow-local-endpoint" aria-describedby="allow-local-endpoint-hint" type="checkbox" checked={draft.allowLocalEndpoint} onChange={(event) => update('allowLocalEndpoint', event.currentTarget.checked)} />
              <span>允许此连接使用本机回环地址</span>
            </label>
            <small id="allow-local-endpoint-hint" className="hint">仅对当前模型连接生效</small>
          </div>
        </div>
        </fieldset>
        <p className="hint">密钥加密保存在本机，不在页面回显。使用远程模型时，本任务明确提供的资料会发送到该服务。</p>
        {(error || localError) && <p className="form-error" role="alert">{localError || error}</p>}
        {message && <p className="notice notice--good" role="status">{message}</p>}
        <div className="button-row">
          <button className="button button--primary" disabled={busy || saving || testing || !online}>{saving ? '正在保存…' : recoveryCommand?.route === '/api/connection' ? '使用原 requestId 保存连接' : '保存连接'}</button>
          <button type="button" className="button" disabled={busy || saving || testing || dirty || !online || !executionAvailable || !connection || !connection.hasCredential} onClick={() => void testConnection()}>
            {testing ? '正在测试…' : recoveryCommand?.route === '/api/connection/test' ? '使用原 requestId 重新测试' : dirty ? '保存后重新测试' : testState === 'verified' ? '重新测试连接' : '测试连接'}
          </button>
        </div>
      </form>
    </section>
  );
}
