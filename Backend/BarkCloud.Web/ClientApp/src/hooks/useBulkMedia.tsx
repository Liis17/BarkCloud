import React from 'react';
import { useSelection } from './useSelection';
import { SelectionBar } from '../components/ui/SelectionBar';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { Modal } from '../components/ui/Modal';
import { apiGet, apiPost, deleteMediaBatch } from '../lib/api';
import type { Album, MediaItem } from '../lib/types';
import type { ToastPush } from './useToast';

interface DownloadResponse {
  urls: Record<string, string | null>;
}

interface UseBulkMediaArgs {
  items: MediaItem[];
  albums: Album[];
  toast: ToastPush;
  onRemoved: (id: string) => void;
  onReloadAlbums?: () => void;
}

/** Множественный выбор и групповые действия для галерей (Фото/Видео): удалить, в альбом, копировать ссылки. */
export function useBulkMedia({ items, albums, toast, onRemoved, onReloadAlbums }: UseBulkMediaArgs) {
  const sel = useSelection();
  const [confirmDel, setConfirmDel] = React.useState(false);
  const [pickAlbum, setPickAlbum] = React.useState(false);

  const chosen = React.useCallback(() => items.filter((m) => sel.has(m.id)), [items, sel]);

  // Клик по «галке»/карточке в режиме выбора: Shift тянет диапазон в порядке отображения.
  const select = React.useCallback((id: string, shift: boolean) => sel.select(id, items.map((m) => m.id), shift), [sel, items]);

  async function bulkDelete() {
    const list = chosen();
    try {
      const result = await deleteMediaBatch(list.map((m) => m.id));
      const removed = new Set(result.succeededIds || (result.failed ? [] : list.map((m) => m.id)));
      for (const m of list.filter((item) => removed.has(item.id)))
        onRemoved(m.id);
      setConfirmDel(false);
      sel.clear();
      if (result.succeeded)
        toast(result.failed ? `Перемещено в корзину: ${result.succeeded}, не удалось: ${result.failed}` : `Перемещено в корзину: ${result.succeeded}`);
      else if (result.failed)
        toast('Не удалось переместить выбранные файлы в корзину', 'err');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  async function bulkCopyLinks() {
    try {
      const d = await apiGet<DownloadResponse>('/api/files/download?ids=' + encodeURIComponent(sel.list.join(',')));
      const urls = sel.list.map((id) => d.urls && d.urls[id]).filter((u): u is string => !!u);
      if (!urls.length) throw new Error('Ссылки недоступны');
      await navigator.clipboard.writeText(urls.join('\n'));
      toast(`Скопировано ссылок: ${urls.length} (временные)`);
    } catch (e) {
      toast((e as Error).message || 'Не удалось скопировать', 'err');
    }
  }

  async function bulkAddToAlbum(albumId: string) {
    try {
      await apiPost('/api/albums/items/add', { album: albumId, fileIds: sel.list });
      setPickAlbum(false);
      const n = sel.count;
      sel.clear();
      onReloadAlbums && onReloadAlbums();
      toast(`Добавлено в альбом: ${n}`);
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  const bar = (
    <SelectionBar
      count={sel.count}
      onClear={sel.clear}
      actions={[
        ...(albums.length ? [{ label: 'В альбом', icon: 'plus', onClick: () => setPickAlbum(true) }] : []),
        { label: 'Копировать ссылки', icon: 'link', onClick: bulkCopyLinks },
        { label: 'Удалить', icon: 'trash', danger: true, onClick: () => setConfirmDel(true) },
      ]}
    />
  );

  const overlay = (
    <>
      {confirmDel && (
        <ConfirmModal
          title="Удалить в корзину?"
          danger
          confirmLabel="Удалить"
          message={`Выбранные элементы (${sel.count}) будут перемещены в корзину.`}
          onClose={() => setConfirmDel(false)}
          onConfirm={bulkDelete}
        />
      )}
      {pickAlbum && (
        <Modal title="Добавить в альбом" onClose={() => setPickAlbum(false)}>
          <div className="album-pick-list">
            {albums.map((a) => (
              <button key={a.id} className="album-pick-row" onClick={() => bulkAddToAlbum(a.id)}>
                {a.name} <span className="cnt">{a.count}</span>
              </button>
            ))}
          </div>
        </Modal>
      )}
    </>
  );

  return { isSelected: sel.has, toggle: select, active: sel.active, count: sel.count, clear: sel.clear, bar, overlay };
}
