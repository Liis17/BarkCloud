import React from 'react';
import { Modal } from './Modal';

interface RenameModalProps {
  title?: string;
  label?: string;
  initial?: string;
  onClose?: () => void;
  onSave: (name: string) => void | Promise<void>;
}

/** Маленькая модалка переименования. */
export function RenameModal({ title = 'Переименовать', label = 'Новое имя', initial = '', onClose, onSave }: RenameModalProps) {
  const [name, setName] = React.useState(initial || '');
  const [busy, setBusy] = React.useState(false);
  async function save() {
    const v = name.trim();
    if (!v) return;
    setBusy(true);
    try {
      await onSave(v);
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      title={title}
      onClose={onClose}
      actions={
        <>
          <button className="btn text" onClick={onClose}>
            Отмена
          </button>
          <button className="btn primary" onClick={save} disabled={busy}>
            {busy ? '…' : 'Сохранить'}
          </button>
        </>
      }
    >
      <label className="field-label">{label}</label>
      <input
        type="text"
        value={name}
        autoFocus
        onChange={(e) => setName(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') save();
        }}
      />
    </Modal>
  );
}
