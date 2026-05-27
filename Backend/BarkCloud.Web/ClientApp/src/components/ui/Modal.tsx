import React from 'react';
import { Icon } from '../Icon';

interface ModalProps {
  title: React.ReactNode;
  children: React.ReactNode;
  onClose?: () => void;
  actions?: React.ReactNode;
  wide?: boolean;
}

/** Модальное окно (Esc / клик по фону — закрыть). */
export function Modal({ title, children, onClose, actions, wide }: ModalProps) {
  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose && onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className={'modal' + (wide ? ' wide' : '')} onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{title}</h3>
          <button className="icon-btn" onClick={onClose} title="Закрыть">
            <Icon.x size={20} />
          </button>
        </div>
        <div className="modal-body">{children}</div>
        {actions && <div className="modal-actions">{actions}</div>}
      </div>
    </div>
  );
}
