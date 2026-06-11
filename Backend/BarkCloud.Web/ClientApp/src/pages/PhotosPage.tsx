import React from 'react';
import { useLocation } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { MemoriesStrip } from '../components/memories/MemoriesStrip';
import { MediaSearchResults } from '../components/search/MediaSearchResults';
import { useToast } from '../hooks/useToast';
import { useInfiniteMedia } from '../hooks/useInfiniteMedia';
import { useMediaActions } from '../hooks/useMediaActions';
import { useFileDrop } from '../hooks/useFileDrop';
import { useBulkMedia } from '../hooks/useBulkMedia';
import { usePageHeader } from '../hooks/usePageHeader';
import { useUploadActions } from '../hooks/useUploadManager';
import { apiGet, pickFiles } from '../lib/api';
import { GRID_SIZES, plural, groupByDate } from '../lib/format';
import type { Album, MediaItem } from '../lib/types';

function Photo({ m, selecting, checked, onToggle, onOpen, onMenu }: {
  m: MediaItem;
  selecting: boolean;
  checked: boolean;
  onToggle: (shift: boolean) => void;
  onOpen: (m: MediaItem) => void;
  onMenu: (e: React.MouseEvent, m: MediaItem) => void;
}) {
  return (
    <div className={'photo' + (checked ? ' checked' : '')} onClick={(e) => (selecting ? onToggle(e.shiftKey) : onOpen(m))} onContextMenu={(e) => onMenu(e, m)}>
      <MediaThumb media={m} sizes={GRID_SIZES} />
      <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать">
        {checked ? <Icon.check size={14} /> : null}
      </button>
      {m.kind === 'video' && (
        <div className="vbadge">
          <Icon.play size={10} /> видео
        </div>
      )}
    </div>
  );
}

export function PhotosPage() {
  const location = useLocation();
  const searchQuery = (new URLSearchParams(location.search).get('q') || '').trim();
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  // Инкремент при удалении фото — «В этот день» перезагружается, иначе там остаётся удалённый снимок.
  const [memKey, setMemKey] = React.useState(0);
  const [toastNode, toast] = useToast();
  const { enqueue, attachVersion } = useUploadActions();

  const { items: photos, loading, done, sentinelRef, removeItem, updateItem, prependItems } = useInfiniteMedia('photo', toast);

  const loadAlbums = React.useCallback(() => {
    apiGet<{ albums: Album[] }>('/api/albums')
      .then((d) => setAlbums(d.albums || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setAlbums([]);
      });
  }, [toast]);

  React.useEffect(() => {
    loadAlbums();
  }, [loadAlbums]);

  const actionsCtx = useMediaActions({
    albums: albums || [],
    toast,
    onRenamed: (m, name) => updateItem(m.id, { entryNames: [name, ...(m.entryNames || []).slice(1)] }),
    onRemoved: (m) => {
      removeItem(m.id);
      setMemKey((k) => k + 1);
    },
    onItemPatched: updateItem,
    reloadAlbums: loadAlbums,
  });

  async function doUpload(dropped?: File[]) {
    const files = dropped && dropped.length ? dropped : await pickFiles({ accept: 'image/*' });
    if (!files.length) return;
    enqueue(files, { routeByMediaKind: true });
  }

  const { over, dropHandlers } = useFileDrop((f) => doUpload(f));
  const bulk = useBulkMedia({
    items: photos,
    albums: albums || [],
    toast,
    onRemoved: (id) => {
      removeItem(id);
      setMemKey((k) => k + 1);
    },
    onReloadAlbums: loadAlbums,
  });

  const prependRef = React.useRef(prependItems);
  prependRef.current = prependItems;

  const groups = React.useMemo(() => groupByDate(photos), [photos]);

  React.useEffect(() => {
    apiGet<{ items: MediaItem[] }>('/api/cloud/media?kind=photo&limit=60')
      .then((d) => prependRef.current(d.items || []))
      .catch(() => {});
  }, [attachVersion]);

  usePageHeader(
    () => ({
      title: 'Фотогалерея',
      documentTitle: 'Фото',
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Фото</span>
        </>
      ),
      actions: (
        <button className="btn primary" onClick={() => doUpload()}>
          <Icon.upload size={16} /> Загрузить
        </button>
      ),
    }),
    [],
  );

  if (searchQuery) {
    return (
      <>
        {toastNode}
        <MediaSearchResults q={searchQuery} albums={albums || []} toast={toast} reloadAlbums={loadAlbums} />
      </>
    );
  }

  return (
    <>
      {toastNode}
      {actionsCtx.overlay}
      {bulk.bar}
      {bulk.overlay}

      <div className={'dropzone' + (over ? ' drop-over' : '')} {...dropHandlers}>
        {over && (
          <div className="drop-overlay">
            <Icon.upload size={40} />
            <span>Отпустите фото для загрузки</span>
          </div>
        )}

      <div className="photos-toolbar">
        <div className="chip-row">
          <span className="chip active">
            <Icon.check size={16} /> Все фото
            <span className="count">
              {photos.length}
              {done ? '' : '+'}
            </span>
          </span>
        </div>
      </div>

      <MemoriesStrip refreshKey={memKey} actions={actionsCtx.api} />

      {(loading && photos.length === 0 ? (
          <Loading />
        ) : photos.length === 0 ? (
          <EmptyState
            icon="photo"
            title="Пока нет фотографий"
            hint="Загрузите снимки — они появятся здесь и в галерее."
            action={
              <button className="btn primary" onClick={() => doUpload()}>
                <Icon.upload size={16} /> Загрузить
              </button>
            }
          />
        ) : (
          <>
            {groups.map((g) => (
              <div key={g.key} className="date-group">
                <div className="date-head">
                  <h3>{g.label}</h3>
                  <div className="right">
                    <span>
                      {g.items.length} {plural(g.items.length, 'фото', 'фото', 'фото')}
                    </span>
                  </div>
                </div>
                <div className="photo-grid">
                  {g.items.map((m) => (
                    <Photo
                      key={m.id}
                      m={m}
                      selecting={bulk.active}
                      checked={bulk.isSelected(m.id)}
                      onToggle={(shift) => bulk.toggle(m.id, shift)}
                      onOpen={() => setLightbox(photos.findIndex((p) => p.id === m.id))}
                      onMenu={actionsCtx.openMenu}
                    />
                  ))}
                </div>
              </div>
            ))}
            <div ref={sentinelRef} className="infinite-sentinel">
              {loading && photos.length > 0 && <Loading label="Загрузка…" />}
            </div>
          </>
        ))}
      </div>

      {lightbox !== null && <Lightbox items={photos} index={lightbox} actions={actionsCtx.api} onClose={() => setLightbox(null)} />}
    </>
  );
}
