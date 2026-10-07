import React from 'react';
import { Icon } from '../Icon';

interface ModalProps {
  title: React.ReactNode;
  children: React.ReactNode;
  onClose?: () => void;
  actions?: React.ReactNode;
  wide?: boolean;
  className?: string;
}

/** Модальное окно (Esc / клик по фону — закрыть). */
export function Modal({ title, children, onClose, actions, wide, className }: ModalProps) {
  const titleId = React.useId();
  const dialog = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    const first = dialog.current?.querySelector<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), [tabindex="0"]');
    (first || dialog.current)?.focus();
    return () => previous?.focus();
  }, []);
  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const dialogs = document.querySelectorAll('[role="dialog"][aria-modal="true"]');
      if (dialogs[dialogs.length - 1] !== dialog.current) return;
      if (e.key === 'Escape') { e.preventDefault(); onClose && onClose(); }
      if (e.key === 'Tab' && dialog.current) {
        const items = Array.from(dialog.current.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'));
        if (!items.length) { e.preventDefault(); return; }
        if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items[items.length - 1].focus(); }
        else if (!e.shiftKey && document.activeElement === items[items.length - 1]) { e.preventDefault(); items[0].focus(); }
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div ref={dialog} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} className={'modal' + (wide ? ' wide' : '') + (className ? ' ' + className : '')} onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3 id={titleId}>{title}</h3>
          <button className="icon-btn" onClick={onClose} disabled={!onClose} title="Закрыть" aria-label="Закрыть">
            <Icon.x size={20} />
          </button>
        </div>
        <div className="modal-body">{children}</div>
        {actions && <div className="modal-actions">{actions}</div>}
      </div>
    </div>
  );
}
