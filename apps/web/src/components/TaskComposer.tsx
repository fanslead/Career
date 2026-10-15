import { useState, type FormEvent } from 'react';
import type { TaskDraft, TaskResponse } from '../contracts.ts';

const initialDraft: TaskDraft = { title: '', goal: '', criteriaText: '', inputText: '', mode: 'office', project: '' };

export function TaskComposer({
  busy,
  online,
  error,
  recoveryRequestId,
  runCommand,
  onCreated,
}: {
  busy: boolean;
  online: boolean;
  error: string;
  recoveryRequestId: string | null;
  runCommand<T>(key: string, route: string, makeBody: (requestId: string) => Record<string, unknown>, method?: string): Promise<{ ok: boolean; value?: T; error?: string }>;
  onCreated(task: TaskResponse): Promise<void>;
}) {
  const [draft, setDraft] = useState(initialDraft);
  const [localError, setLocalError] = useState('');

  function set<K extends keyof TaskDraft>(key: K, value: TaskDraft[K]) {
    setLocalError('');
    setDraft((current) => ({ ...current, [key]: value }));
  }

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const criteria = draft.criteriaText.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
    if (criteria.length === 0) {
      setLocalError('至少填写一条可检查的验收条目。');
      return;
    }
    const result = await runCommand<TaskResponse>('task:create', '/api/tasks', (requestId) => ({
      requestId, title: draft.title.trim(), goal: draft.goal.trim(), criteria,
      inputText: draft.inputText, mode: draft.mode, project: draft.project.trim() || null,
    }));
    if (result.ok && result.value) {
      await onCreated(result.value);
      setDraft(initialDraft);
    } else if (result.error) setLocalError(result.error);
  }

  return (
    <form className="panel task-composer" onSubmit={submit}>
      <div className="panel-heading">
        <div>
          <p className="eyebrow">开始一项工作</p>
          <h2>交办新任务</h2>
        </div>
        <span className="phase-label">首版仅由你负责</span>
      </div>
      {recoveryRequestId && <div className="notice notice--warn" role="status"><strong>正在恢复未核对的任务创建</strong><p>浏览器只保存了原 requestId，没有保存正文或资料。请检查本页草稿并补全后使用原 requestId 提交：<code>{recoveryRequestId}</code></p></div>}
      <div className="form-grid">
        <label className="field field--wide">
          <span>任务名称</span>
          <input value={draft.title} onChange={(event) => set('title', event.currentTarget.value)} required maxLength={160} placeholder="例如：整理本季度客户反馈" />
        </label>
        <label className="field field--wide">
          <span>目标与要求</span>
          <textarea value={draft.goal} onChange={(event) => set('goal', event.currentTarget.value)} required maxLength={4000} rows={3} placeholder="描述要完成的工作、范围和需要注意的事项" />
        </label>
        <div className="field field--wide">
          <label htmlFor="task-criteria">验收条目</label>
          <textarea id="task-criteria" aria-describedby="task-criteria-hint" value={draft.criteriaText} onChange={(event) => set('criteriaText', event.currentTarget.value)} required rows={3} placeholder={'每项成果都可追溯到原始反馈\n列出影响最大的 3 个问题'} />
          <small id="task-criteria-hint" className="hint">每行一项</small>
        </div>
        <div className="field field--wide">
          <label htmlFor="task-input-text">输入资料文本</label>
          <textarea id="task-input-text" aria-describedby="task-input-text-hint" value={draft.inputText} onChange={(event) => set('inputText', event.currentTarget.value)} maxLength={100000} rows={6} placeholder="粘贴你明确提供给本任务的资料。当前阶段支持文本输入。" />
          <small id="task-input-text-hint" className="hint">{draft.inputText.length.toLocaleString()} 字符 · 当前任务不会读取未在此处提交的文件</small>
        </div>
        <label className="field">
          <span>工作类型</span>
          <select value={draft.mode} onChange={(event) => set('mode', event.currentTarget.value as TaskDraft['mode'])}>
            <option value="office">办公</option>
            <option value="development">研发</option>
            <option value="business">经营</option>
          </select>
        </label>
        <label className="field">
          <span>项目（可选）</span>
          <input value={draft.project} onChange={(event) => set('project', event.currentTarget.value)} maxLength={120} placeholder="留空表示不归属项目" />
        </label>
      </div>
      {(localError || error) && <p className="form-error" role="alert">{localError || error}</p>}
      <div className="button-row button-row--end">
        <p className="hint">提交后会固定输入和验收条目，生成计划供你确认。</p>
        <button className="button button--primary" disabled={busy || !online}>{busy ? '正在提交…' : online ? recoveryRequestId ? '使用原 requestId 创建任务' : '创建任务并查看计划' : '离线时不能交办'}</button>
      </div>
    </form>
  );
}
