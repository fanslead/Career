import { useState, type FormEvent } from 'react';
import type { BootstrapResponse } from '../contracts.ts';

export function PairingGate({
  bootstrap,
  error,
  busy,
  offline,
  onPair,
}: {
  bootstrap: BootstrapResponse | null;
  error: string;
  busy: boolean;
  offline: boolean;
  onPair(code: string): Promise<void>;
}) {
  const [code, setCode] = useState('');
  const [submitError, setSubmitError] = useState('');

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSubmitError('');
    try {
      await onPair(code.trim());
      setCode('');
    } catch (reason) {
      setSubmitError(reason instanceof Error ? reason.message : '配对失败，请检查启动终端中的新配对码。');
    }
  }

  return (
    <main className="gate-wrap">
      <section className="panel gate-panel" aria-labelledby="pair-title">
        <div className="brand-mark" aria-hidden="true">C</div>
        <p className="eyebrow">本地工作区</p>
        <h1 id="pair-title">连接到 Career</h1>
        <p className="lede">浏览器需要先与正在运行的工作区配对。配对码只会显示在启动工作区的终端中。</p>
        {!bootstrap && <div className="notice notice--error" role="alert">{error || '正在读取工作区状态…'}</div>}
        {bootstrap?.hasOwner && <div className="notice notice--warn">此工作区已有所有者。刷新凭据不可用时，请从工作区终端重新生成一次性配对码。</div>}
        {bootstrap && !bootstrap.requiresPairing && !bootstrap.hasOwner && (
          <div className="notice notice--warn">工作区当前没有开放配对。请检查工作区是否正常启动后重试。</div>
        )}
        {bootstrap?.requiresPairing && (
          <form onSubmit={submit}>
            <div className="field">
              <label htmlFor="pair-code">终端配对码</label>
              <input
                id="pair-code"
                autoComplete="one-time-code"
                autoCapitalize="off"
                spellCheck={false}
                value={code}
                onChange={(event) => setCode(event.currentTarget.value)}
                required
                minLength={4}
                aria-describedby="pair-hint"
              />
              <span id="pair-hint" className="hint">不会在此页面显示配对码，也不会把配对码保存在浏览器里。</span>
            </div>
            {(submitError || error) && <p className="form-error" role="alert">{submitError || error}</p>}
            <button className="button button--primary button--wide" disabled={busy || offline || !code.trim()}>
              {busy ? '正在配对…' : offline ? '离线时无法配对' : '安全配对'}
            </button>
          </form>
        )}
        <p className="fine-print">{bootstrap?.workspaceName || '正在读取工作区'}</p>
      </section>
    </main>
  );
}
