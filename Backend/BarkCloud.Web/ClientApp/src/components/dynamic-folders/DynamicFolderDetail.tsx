import React from 'react';
import { Icon } from '../Icon';
import { MediaThumb } from '../media/MediaThumb';
import { Lightbox } from '../media/Lightbox';
import { Loading, EmptyState } from '../ui/EmptyState';
import { DynamicFolderFormModal } from './DynamicFolderFormModal';
import { useMediaActions } from '../../hooks/useMediaActions';
import { apiGet, apiPost } from '../../lib/api';
import { GRID_SIZES, kindRu, fmtFull, plural } from '../../lib/format';
import { useDocumentHead } from '../../hooks/useDocumentHead';
import type { Album, DynamicFolder, MediaItem, Page } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';

interface Props {
  folder: DynamicFolder;
  onBack: () => void;
  onChanged: () => void;
  toast: ToastPush;
  albums?: Album[];
  reloadAlbums?: () => void;
}

/** Просмотр содержимого умной папки: сетка превью или список (по folder.viewMode), собранные по критериям. */
export function DynamicFolderDetail({ folder, onBack, onChanged, toast, albums, reloadAlbums }: Props) {
  const [items, setItems] = React.useState<MediaItem[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [editing, setEditing] = React.useState(false);
  const isDuplicateFolder = folder.id === 'sys-duplicate-media' || folder.id === 'sys-duplicate-files';

  useDocumentHead(
    () => ({ title: folder.name, iconUrl: folder.coverUrl || null }),
    [folder.name, folder.coverUrl],
    10,
  );

  const load = React.useCallback(() => {
    setItems(null);
    loadFolderItems(folder.id, isDuplicateFolder)
      .then((loaded) => setItems(loaded))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setItems([]);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [folder.id, isDuplicateFolder]);
  React.useEffect(load, [load]);

  const actions = useMediaActions({
    albums,
    toast,
    reloadAlbums,
    onRenamed: () => {
      load();
      onChanged();
    },
    onRemoved: () => {
      load();
      onChanged();
    },
  });

  // Lightbox умеет только фото/видео; документы/аудио открываем скачиванием.
  const media = React.useMemo(() => (items || []).filter((m) => m.kind === 'photo' || m.kind === 'video'), [items]);
  const duplicateGroups = React.useMemo(() => groupDuplicates(items || []), [items]);

  async function download(id: string) {
    try {
      const d = await apiGet<{ urls: Record<string, string> }>('/api/files/download?ids=' + id);
      const url = d.urls[id];
      if (url) window.open(url, '_blank');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  function openItem(m: MediaItem) {
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
      ) : items.length === 0 || (isDuplicateFolder && duplicateGroups.length === 0) ? (
        <EmptyState icon="folder" title="Пока пусто" hint="Сюда автоматически попадут файлы, подходящие под условия." />
      ) : isDuplicateFolder ? (
        <div className={'df-dup-groups ' + (folder.viewMode === 1 ? 'list' : 'media')}>
          {duplicateGroups.map((g, index) => (
            <section
              className={'df-dup-group ' + (folder.viewMode === 1 ? 'list' : 'media')}
              key={g.key}
              style={{ '--dup-cols': Math.min(g.items.length, 4) } as React.CSSProperties}
            >
              <div className="df-dup-head">
                <div>
                  <div className="df-dup-title">Группа {index + 1}</div>
                  <div className="df-dup-meta">
                    {g.items.length} {plural(g.items.length, 'файл', 'файла', 'файлов')} · {g.key.slice(0, 12)}
                  </div>
                </div>
              </div>
              {folder.viewMode === 1 ? (
                <div className="df-list df-dup-list">
                  {g.items.map((m) => (
                    <div
                      key={m.id}
                      className="df-list-row"
                      onClick={() => openItem(m)}
                      onContextMenu={(e) => actions.openMenu(e, m)}
                    >
                      <div className={'file-icon ' + (m.iconKind || 'doc')}>{m.ext || 'FILE'}</div>
                      <div className="df-list-main">
                        <div className="fn">{m.name}</div>
                        <div className="meta">{kindRu(m.kind)}</div>
                      </div>
                      <div className="df-list-size">{m.sizeLabel || '—'}</div>
                      <div className="df-list-date">{fmtFull(m.createdAt)}</div>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="df-dup-media-items">
                  {g.items.map((m) => (
                    <div key={m.id} className="photo df-dup-photo" onClick={() => openItem(m)} onContextMenu={(e) => actions.openMenu(e, m)}>
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
            </section>
          ))}
        </div>
      ) : folder.viewMode === 1 ? (
        <div className="df-list">
          {items.map((m) => (
            <div
              key={m.id}
              className="df-list-row"
              onClick={() => openItem(m)}
              onContextMenu={(e) => actions.openMenu(e, m)}
            >
              <div className={'file-icon ' + (m.iconKind || 'doc')}>{m.ext || 'FILE'}</div>
              <div className="df-list-main">
                <div className="fn">{m.name}</div>
                <div className="meta">{kindRu(m.kind)}</div>
              </div>
              <div className="df-list-size">{m.sizeLabel || '—'}</div>
              <div className="df-list-date">{fmtFull(m.createdAt)}</div>
            </div>
          ))}
        </div>
      ) : (
        <div className="photo-grid">
          {items.map((m) => (
            <div key={m.id} className="photo" onClick={() => openItem(m)} onContextMenu={(e) => actions.openMenu(e, m)}>
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
      {actions.overlay}
    </div>
  );
}

async function loadFolderItems(folderId: string, loadAllPages: boolean): Promise<MediaItem[]> {
  const items: MediaItem[] = [];
  let cursorAt: string | null = null;
  let cursorId: string | null = null;

  do {
    const params = new URLSearchParams({ folder: folderId, limit: loadAllPages ? '200' : '100' });
    if (cursorAt) params.set('cursorAt', cursorAt);
    if (cursorId) params.set('cursorId', cursorId);

    const page = await apiGet<Page<MediaItem>>('/api/dynamic-folders/items?' + params.toString());
    items.push(...(page.items || []));
    cursorAt = loadAllPages ? page.nextCursorAt : null;
    cursorId = loadAllPages ? page.nextCursorId : null;
  } while (loadAllPages && cursorAt && cursorId);

  return items;
}

function groupDuplicates(items: MediaItem[]) {
  const groups: { key: string; items: MediaItem[] }[] = [];
  const byKey = new Map<string, { key: string; items: MediaItem[] }>();

  for (const item of items) {
    const key = item.duplicateGroupKey || item.id;
    let group = byKey.get(key);
    if (!group) {
      group = { key, items: [] };
      byKey.set(key, group);
      groups.push(group);
    }
    group.items.push(item);
  }

  return groups.filter((g) => g.items.length > 1);
}
