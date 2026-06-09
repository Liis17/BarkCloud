import React from 'react';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { AlbumCard } from '../components/albums/AlbumCard';
import { AlbumFormModal } from '../components/albums/AlbumFormModal';
import { AlbumDetail } from '../components/albums/AlbumDetail';
import { MemoriesStrip } from '../components/memories/MemoriesStrip';
import { useToast } from '../hooks/useToast';
import { useInfiniteMedia } from '../hooks/useInfiniteMedia';
import { useMediaActions } from '../hooks/useMediaActions';
import { useDuplicatePrompt, type DuplicateDecision } from '../hooks/useDuplicatePrompt';
import { useFileDrop } from '../hooks/useFileDrop';
import { useBulkMedia } from '../hooks/useBulkMedia';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet, apiPost, pickFiles, uploadFile, checkDuplicate } from '../lib/api';
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

interface UploadState {
  pct: number;
  current: number;
  total: number;
}

export function PhotosPage() {
  const [tab, setTab] = React.useState<'photos' | 'albums'>('photos');
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [openAlbum, setOpenAlbum] = React.useState<Album | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [upload, setUpload] = React.useState<UploadState | null>(null);
  const [toastNode, toast] = useToast();
  const dup = useDuplicatePrompt();

  const { items: photos, loading, done, sentinelRef, removeItem, updateItem, reload } = useInfiniteMedia('photo', toast);

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
    onRemoved: (m) => removeItem(m.id),
    onItemPatched: updateItem,
    reloadAlbums: loadAlbums,
  });

  async function doUpload(dropped?: File[]) {
    const files = dropped && dropped.length ? dropped : await pickFiles({ accept: 'image/*' });
    if (!files.length) return;
    let processed = 0;
    let uploaded = 0;
    let duplicateBatchDecision: DuplicateDecision | null = null;
    for (const f of files) {
      setUpload({ current: processed + 1, total: files.length, pct: 0 });
      try {
        const d = await checkDuplicate(f);
        if (d.exists) {
          const decision: DuplicateDecision = duplicateBatchDecision ?? (await dup.ask(f.name, d.locations));
          if (decision === 'skip-all' || decision === 'upload-all') duplicateBatchDecision = decision;
          if (decision === 'skip' || decision === 'skip-all') {
            processed++;
            continue;
          }
        }
        const res = await uploadFile(f, (p) => setUpload({ current: processed + 1, total: files.length, pct: Math.round(p * 100) }));
        if (res?.fileId) {
          // Загрузка с вкладки «Фото» → авто-распределение по типу в системную папку.
          try {
            await apiPost('/api/cloud/attach', { fileId: res.fileId, name: res.name || f.name, routeByMediaKind: true });
          } catch {
            /* attach best-effort */
          }
        }
        uploaded++;
      } catch (e) {
        toast(`«${f.name}»: ${(e as Error).message}`, 'err');
      }
      processed++;
    }
    setUpload(null);
    if (uploaded > 0) toast(`Загружено: ${uploaded} ${plural(uploaded, 'файл', 'файла', 'файлов')}`);
    reload();
  }

  const { over, dropHandlers } = useFileDrop((f) => doUpload(f));
  const bulk = useBulkMedia({ items: photos, albums: albums || [], toast, onRemoved: removeItem, onReloadAlbums: loadAlbums });

  const groups = React.useMemo(() => groupByDate(photos), [photos]);

  usePageHeader(
    () => ({
      title: 'Фотогалерея',
      documentTitle: openAlbum ? openAlbum.name : tab === 'albums' ? 'Альбомы' : 'Фото',
      documentIconUrl: openAlbum?.coverUrl || null,
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Фото</span>
        </>
      ),
      actions: (
        <>
          {tab === 'albums' && (
            <button className="btn outlined" onClick={() => setCreating(true)}>
              <Icon.plus size={16} /> Альбом
            </button>
          )}
          <button className="btn primary" onClick={() => doUpload()}>
            <Icon.upload size={16} /> Загрузить
          </button>
        </>
      ),
    }),
    [tab, openAlbum?.name, openAlbum?.coverUrl],
  );

  return (
    <>
      {toastNode}
      {actionsCtx.overlay}
      {dup.overlay}
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
          <button className={'chip' + (tab === 'photos' ? ' active' : '')} onClick={() => { setTab('photos'); setOpenAlbum(null); }}>
            {tab === 'photos' && <Icon.check size={16} />} Все фото
            <span className="count">
              {photos.length}
              {done ? '' : '+'}
            </span>
          </button>
          <button className={'chip' + (tab === 'albums' ? ' active' : '')} onClick={() => setTab('albums')}>
            {tab === 'albums' && <Icon.check size={16} />} Альбомы
            {albums && <span className="count">{albums.length}</span>}
          </button>
        </div>
      </div>

      {upload && (
        <div className="upload-banner">
          <span className="spinner" />
          Загрузка {upload.current}/{upload.total}…
          <div className="bar">
            <div className="bar-fill" style={{ width: upload.pct + '%' }} />
          </div>
        </div>
      )}

      {tab === 'albums' &&
        (openAlbum ? (
          <AlbumDetail album={openAlbum} candidates={photos} toast={toast} onBack={() => setOpenAlbum(null)} onChanged={() => loadAlbums()} />
        ) : albums === null ? (
          <Loading />
        ) : (
          <div className="album-grid">
            {albums.map((a) => (
              <AlbumCard key={a.id} album={a} onOpen={(al) => setOpenAlbum(al)} />
            ))}
            <div className="album-card new-album" onClick={() => setCreating(true)}>
              <Icon.plus size={28} />
              <span>Создать альбом</span>
            </div>
          </div>
        ))}

      {tab === 'photos' && <MemoriesStrip />}

      {tab === 'photos' &&
        (loading && photos.length === 0 ? (
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

      {creating && (
        <AlbumFormModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false);
            setTab('albums');
            loadAlbums();
            toast('Альбом создан');
          }}
          toast={toast}
        />
      )}
      {lightbox !== null && <Lightbox items={photos} index={lightbox} onClose={() => setLightbox(null)} />}
    </>
  );
}
