import React from 'react';
import { Modal } from './Modal';

interface ConfirmModalProps {
  title?: string;
  message: React.ReactNode;
  confirmLabel?: string;
  danger?: boolean;
  onClose?: () => void;
  onConfirm: () => void | Promise<void>;
}

/** Подтверждение действия (удаление в корзину и т.п.). */
export function ConfirmModal({
  title = 'Подтвердите',
  message,
  confirmLabel = 'OK',
  danger = false,
  onClose,
  onConfirm,
}: ConfirmModalProps) {
  const [busy, setBusy] = React.useState(false);
  async function run() {
    if (busy) return;
    setBusy(true);
    try {
      await onConfirm();
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      title={title}
      onClose={busy ? undefined : onClose}
      actions={
        <>
          <button className="btn text" onClick={onClose} disabled={busy}>
            Отмена
          </button>
          <button className={'btn ' + (danger ? 'danger' : 'primary')} onClick={run} disabled={busy}>
            {busy ? '…' : confirmLabel}
          </button>
        </>
      }
    >
      <div className="confirm-msg">{message}</div>
    </Modal>
  );
}
