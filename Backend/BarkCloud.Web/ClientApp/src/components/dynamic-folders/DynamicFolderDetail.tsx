import React from 'react';
import { Icon } from '../Icon';
import { MediaThumb } from '../media/MediaThumb';
import { Lightbox } from '../media/Lightbox';
import { Loading, EmptyState } from '../ui/EmptyState';
import { DynamicFolderFormModal } from './DynamicFolderFormModal';
import { apiGet, apiPost } from '../../lib/api';
import { GRID_SIZES } from '../../lib/format';
import type { CardFile, DynamicFolder, Page } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

interface Props {
  folder: DynamicFolder;
  onBack: () => void;
  onChanged: () => void;
  toast: ToastPush;
}

/** Просмотр содержимого умной папки: сетка файлов, собранных по критериям. Изменить/удалить — только у пользовательских. */
export function DynamicFolderDetail({ folder, onBack, onChanged, toast }: Props) {
  const [items, setItems] = React.useState<CardFile[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [editing, setEditing] = React.useState(false);

  const load = React.useCallback(() => {
    setItems(null);
    apiGet<Page<CardFile>>('/api/dynamic-folders/items?folder=' + encodeURIComponent(folder.id))
      .then((d) => setItems(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setItems([]);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [folder.id]);
  React.useEffect(load, [load]);

  // Lightbox умеет только фото/видео; документы/аудио открываем скачиванием.
  const media = React.useMemo(() => (items || []).filter((m) => m.kind === 'photo' || m.kind === 'video'), [items]);

  async function download(id: string) {
    try {
      const d = await apiGet<{ urls: Record<string, string> }>('/api/files/download?ids=' + id);
      const url = d.urls[id];
      if (url) window.open(url, '_blank');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  function openItem(m: CardFile) {
    if (m.kind === 'photo' || m.kind === 'video') setLightbox(media.findIndex((x) => x.id === m.id));
    else download(m.id);
  }
  async function removeFolder() {
    if (!window.confirm('Удалить умную папку? Файлы останутся в облаке.')) return;
    try {
      await apiPost('/api/dynamic-folders/delete', { folder: folder.id });
      onChanged();
      onBack();
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  return (
    <div>
      <div className="date-head" style={{ marginBottom: 18 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <button className="icon-btn" onClick={onBack} title="Назад">
            <Icon.arrow size={20} style={{ transform: 'rotate(180deg)' }} />
          </button>
          <h3>{folder.name}</h3>
        </div>
        {!folder.isSystem && (
          <div className="right" style={{ gap: 8 }}>
            <button className="btn outlined" onClick={() => setEditing(true)}>
              <Icon.pencil size={16} /> Изменить
            </button>
            <button className="btn text" onClick={removeFolder}>
              <Icon.trash size={16} /> Удалить
            </button>
          </div>
        )}
      </div>

      {items === null ? (
        <Loading />
      ) : items.length === 0 ? (
        <EmptyState icon="folder" title="Пока пусто" hint="Сюда автоматически попадут файлы, подходящие под условия." />
      ) : (
        <div className="photo-grid">
          {items.map((m) => (
            <div key={m.id} className="photo" onClick={() => openItem(m)}>
              <MediaThumb media={m} sizes={GRID_SIZES} />
              {m.kind === 'video' && (
                <div className="vbadge">
                  <Icon.play size={10} /> видео
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {editing && (
        <DynamicFolderFormModal
          folder={folder}
          onClose={() => setEditing(false)}
          onSaved={() => {
            setEditing(false);
            onChanged();
            load();
            toast('Сохранено');
          }}
          toast={toast}
        />
      )}
      {lightbox !== null && <Lightbox items={media} index={lightbox} onClose={() => setLightbox(null)} />}
    </div>
  );
}
