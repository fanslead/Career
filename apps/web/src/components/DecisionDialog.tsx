import { useEffect, useId, useRef, type ReactNode } from 'react';

export function DecisionDialog({
  open,
  title,
  description,
  children,
  primaryLabel,
  danger = false,
  busy,
  disabled = false,
  disabledReason,
  onConfirm,
  onClose,
}: {
  open: boolean;
  title: string;
  description: string;
  children: ReactNode;
  primaryLabel: string;
  danger?: boolean;
  busy: boolean;
  disabled?: boolean;
  disabledReason?: string;
  onConfirm(): void;
  onClose(): void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  const titleId = useId();
  useEffect(() => {
    const element = dialog.current;
    if (!element) return;
    if (open && !element.open) {
      returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      element.showModal();
    } else if (!open && element.open) {
      element.close();
      const target = returnFocus.current;
      if (target?.isConnected && !target.matches(':disabled')) target.focus();
      else document.getElementById('main-content')?.focus();
      returnFocus.current = null;
    }
  }, [open]);

  return (
    <dialog ref={dialog} className="dialog" onCancel={(event) => { event.preventDefault(); onClose(); }} aria-labelledby={titleId}>
      <div className="dialog-head">
        <div><p className="eyebrow">需要你的决定</p><h2 id={titleId}>{title}</h2></div>
        <button type="button" className="icon-button" aria-label="关闭对话框" disabled={busy} onClick={onClose}>×</button>
      </div>
      <div className="dialog-body">
        <p>{description}</p>
        {disabledReason && <p className="notice notice--warn" role="status">{disabledReason}</p>}
        {children}
      </div>
      <div className="dialog-footer">
        <button type="button" className="button" disabled={busy} onClick={onClose}>返回检查</button>
        <button type="button" className={`button ${danger ? 'button--danger' : 'button--primary'}`} disabled={busy || disabled || !open} onClick={onConfirm}>
          {busy ? '正在提交…' : primaryLabel}
        </button>
      </div>
    </dialog>
  );
}
