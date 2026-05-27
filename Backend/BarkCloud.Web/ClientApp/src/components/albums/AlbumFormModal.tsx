import React from 'react';
import { Modal } from '../ui/Modal';
import { apiPost } from '../../lib/api';
import type { Album } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

interface AlbumFormModalProps {
  album?: Album | null;
  onClose: () => void;
  onSaved: (saved: Album) => void;
  toast: ToastPush;
}

/** Создание / редактирование альбома. */
export function AlbumFormModal({ album, onClose, onSaved, toast }: AlbumFormModalProps) {
  const [name, setName] = React.useState(album ? album.name : '');
  const [description, setDescription] = React.useState(album ? album.description : '');
  const [busy, setBusy] = React.useState(false);

  async function save() {
    if (!name.trim()) {
      toast('Введите название', 'err');
      return;
    }
    setBusy(true);
    try {
      const saved = album
        ? await apiPost<Album>('/api/albums/update', { album: album.id, name, description })
        : await apiPost<Album>('/api/albums', { name, description });
      onSaved(saved);
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      title={album ? 'Редактировать альбом' : 'Новый альбом'}
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
      <label className="field-label">Название</label>
      <input type="text" value={name} onChange={(e) => setName(e.target.value)} autoFocus placeholder="Например: Отпуск 2026" />
      <label className="field-label">Описание</label>
      <textarea value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Необязательно" />
    </Modal>
  );
}
