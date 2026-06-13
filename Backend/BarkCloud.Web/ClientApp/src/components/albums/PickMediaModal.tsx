import React from 'react';
import { Modal } from '../ui/Modal';
import { MediaThumb } from '../media/MediaThumb';
import { Icon } from '../Icon';
import { useSelection } from '../../hooks/useSelection';
import type { CardFile } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

interface PickMediaModalProps {
  candidates: CardFile[];
  exclude: Set<string>;
  onClose: () => void;
  onAdd: (fileIds: string[]) => Promise<void>;
  toast: ToastPush;
  title?: string;
}

/** Выбор медиа для добавления в альбом. */
export function PickMediaModal({ candidates, exclude, onClose, onAdd, toast, title = 'Добавить в альбом' }: PickMediaModalProps) {
  const sel = useSelection();
  const [busy, setBusy] = React.useState(false);
  const available = candidates.filter((p) => !exclude.has(p.id));
  const orderedIds = available.map((p) => p.id);

  async function add() {
    if (!sel.count) {
      onClose();
      return;
    }
    setBusy(true);
    try {
      await onAdd(sel.list);
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      wide
      title={title}
      onClose={onClose}
      actions={
        <>
          <button className="btn text" onClick={onClose}>
            Отмена
          </button>
          <button className="btn primary" onClick={add} disabled={busy}>
            Добавить{sel.count ? ` (${sel.count})` : ''}
          </button>
        </>
      }
    >
      {available.length === 0 ? (
        <div style={{ color: 'var(--md-on-surface-variant)', padding: '12px 0' }}>Нет элементов для добавления.</div>
      ) : (
        <div className="pick-grid">
          {available.map((p) => (
            <div key={p.id} className={'pick-cell' + (sel.has(p.id) ? ' on' : '')} onClick={(e) => sel.select(p.id, orderedIds, e.shiftKey)}>
              <MediaThumb media={p} sizes="120px" />
              {sel.has(p.id) && (
                <div className="pick-check">
                  <Icon.check size={14} />
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}
