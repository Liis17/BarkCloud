import React from 'react';
import { Icon } from '../Icon';
import { MediaThumb } from '../media/MediaThumb';
import { Lightbox } from '../media/Lightbox';
import { Loading, EmptyState } from '../ui/EmptyState';
import { AlbumFormModal } from './AlbumFormModal';
import { PickMediaModal } from './PickMediaModal';
import { useContextMenu, type ContextItem } from '../ui/ContextMenu';
import { PropertiesModal } from '../ui/PropertiesModal';
import { ShareWithUserModal } from '../ui/ShareWithUserModal';
import { apiGet, apiPost } from '../../lib/api';
import { createShare, createAlbumShare } from '../../lib/share';
import { GRID_SIZES } from '../../lib/format';
import { useDocumentHead } from '../../hooks/useDocumentHead';
import type { Album, CardFile, Page } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

interface AlbumDetailProps {
  album: Album;
  candidates: CardFile[];
  gridSizes?: string;
  onBack: () => void;
  onChanged: () => void;
  toast: ToastPush;
}

/** Просмотр альбома: сетка элементов, обложка, добавить/убрать, переименовать, удалить. */
export function AlbumDetail({ album, candidates, gridSizes = GRID_SIZES, onBack, onChanged, toast }: AlbumDetailProps) {
  const [items, setItems] = React.useState<CardFile[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [editing, setEditing] = React.useState(false);
  const [picking, setPicking] = React.useState(false);
  const [props, setProps] = React.useState<CardFile | null>(null);
  const [shareWith, setShareWith] = React.useState<CardFile | null>(null);
  const { menu, openAt } = useContextMenu();

  useDocumentHead(
    () => ({ title: album.name, iconUrl: album.coverUrl || null }),
    [album.name, album.coverUrl],
    10,
  );

  const load = React.useCallback(() => {
    setItems(null);
    apiGet<Page<CardFile>>('/api/albums/items?album=' + encodeURIComponent(album.id))
      .then((d) => setItems(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setItems([]);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [album.id]);
  React.useEffect(load, [load]);

  const excludeIds = React.useMemo(() => new Set((items || []).map((i) => i.id)), [items]);

  async function addItems(fileIds: string[]) {
    await apiPost('/api/albums/items/add', { album: album.id, fileIds });
    setPicking(false);
    load();
    onChanged();
    toast('Добавлено в альбом');
  }
  async function removeItem(id: string) {
    try {
      await apiPost('/api/albums/items/remove', { album: album.id, fileIds: [id] });
      load();
      onChanged();
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function setCover(id: string) {
    try {
      await apiPost('/api/albums/update', { album: album.id, coverFileId: id });
      onChanged();
      toast('Обложка обновлена');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function removeAlbum() {
    if (!window.confirm('Удалить альбом? Файлы останутся в облаке.')) return;
    try {
      await apiPost('/api/albums/delete', { album: album.id });
      onChanged();
      onBack();
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function addToFavorites(id: string) {
    try {
      await apiPost('/api/cloud/favorites/add', { fileId: id });
      toast('Добавлено в избранное');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  function itemMenu(m: CardFile): ContextItem[] {
    return [
      { label: 'Сделать обложкой', icon: 'photo', onClick: () => setCover(m.id) },
      { label: 'Добавить в избранное', icon: 'star', onClick: () => addToFavorites(m.id) },
      { label: 'Создать публичную ссылку', icon: 'share', onClick: () => createShare(m.id, m.name, toast) },
      { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareWith(m) },
      { label: 'Свойства', icon: 'info', onClick: () => setProps(m) },
      { divider: true },
      { label: 'Убрать из альбома', icon: 'x', danger: true, onClick: () => removeItem(m.id) },
    ];
  }

  return (
    <div>
      <div className="date-head" style={{ marginBottom: 18 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <button className="icon-btn" onClick={onBack} title="Назад">
            <Icon.arrow size={20} style={{ transform: 'rotate(180deg)' }} />
          </button>
          <div>
            <h3>{album.name}</h3>
            {album.description && <div style={{ fontSize: 13, color: 'var(--md-on-surface-variant)' }}>{album.description}</div>}
          </div>
        </div>
        <div className="right" style={{ gap: 8 }}>
          <button className="btn outlined" onClick={() => setPicking(true)}>
            <Icon.plus size={16} /> Добавить
          </button>
          <button className="btn outlined" onClick={() => createAlbumShare(album.id, album.name, toast)} title="Создать публичную ссылку на альбом">
            <Icon.share size={16} /> Поделиться
          </button>
          <button className="btn outlined" onClick={() => setEditing(true)}>
            <Icon.pencil size={16} /> Изменить
          </button>
          <button className="btn text" onClick={removeAlbum}>
            <Icon.trash size={16} /> Удалить
          </button>
        </div>
      </div>

      {items === null ? (
        <Loading />
      ) : items.length === 0 ? (
        <EmptyState
          icon="photo"
          title="Альбом пуст"
          hint="Добавьте фото или видео из вашей галереи."
          action={
            <button className="btn primary" onClick={() => setPicking(true)}>
              <Icon.plus size={16} /> Добавить
            </button>
          }
        />
      ) : (
        <div className="photo-grid">
          {items.map((m, idx) => (
            <div key={m.id} className="photo" onClick={() => setLightbox(idx)} onContextMenu={(e) => openAt(e, itemMenu(m))}>
              <MediaThumb media={m} sizes={gridSizes} />
              {m.kind === 'video' && (
                <div className="vbadge">
                  <Icon.play size={10} /> видео
                </div>
              )}
              <div className="item-tools">
                <button title="Сделать обложкой" onClick={(e) => { e.stopPropagation(); setCover(m.id); }}>
                  <Icon.star size={15} />
                </button>
                <button title="Убрать из альбома" onClick={(e) => { e.stopPropagation(); removeItem(m.id); }}>
                  <Icon.x size={15} />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <AlbumFormModal
          album={album}
          onClose={() => setEditing(false)}
          onSaved={() => {
            setEditing(false);
            onChanged();
            toast('Сохранено');
          }}
          toast={toast}
        />
      )}
      {picking && (
        <PickMediaModal candidates={candidates} exclude={excludeIds} onClose={() => setPicking(false)} onAdd={addItems} toast={toast} />
      )}
      {lightbox !== null && items && <Lightbox items={items} index={lightbox} onClose={() => setLightbox(null)} />}
      {props && <PropertiesModal fileId={props.id} fallback={props} onClose={() => setProps(null)} />}
      {shareWith && (
        <ShareWithUserModal fileId={shareWith.id} fileName={shareWith.name} onClose={() => setShareWith(null)} toast={toast} />
      )}
      {menu}
    </div>
  );
}
