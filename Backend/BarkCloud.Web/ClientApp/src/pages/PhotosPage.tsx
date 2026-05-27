import React from 'react';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { AlbumCard } from '../components/albums/AlbumCard';
import { AlbumFormModal } from '../components/albums/AlbumFormModal';
import { AlbumDetail } from '../components/albums/AlbumDetail';
import { useToast } from '../hooks/useToast';
import { useInfiniteMedia } from '../hooks/useInfiniteMedia';
import { useMediaActions } from '../hooks/useMediaActions';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet, apiPost, pickFiles, uploadFile } from '../lib/api';
import { GRID_SIZES, plural, groupByDate } from '../lib/format';
import type { Album, MediaItem } from '../lib/types';

const RECENT_FOLDER = 'Недавно загруженные';

function Photo({ m, onOpen, onMenu }: { m: MediaItem; onOpen: (m: MediaItem) => void; onMenu: (e: React.MouseEvent, m: MediaItem) => void }) {
  return (
    <div className="photo" onClick={() => onOpen(m)} onContextMenu={(e) => onMenu(e, m)}>
      <MediaThumb media={m} sizes={GRID_SIZES} />
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
    reloadAlbums: loadAlbums,
  });

  async function ensureRecentFolder(): Promise<string> {
    const d = await apiGet<{ dirs?: { id: string; name: string }[] }>('/api/cloud/list?dir=');
    const found = (d.dirs || []).find((x) => x.name === RECENT_FOLDER);
    if (found) return found.id;
    const created = await apiPost<{ id: string }>('/api/cloud/dir', { name: RECENT_FOLDER });
    return created.id;
  }

  async function doUpload() {
    const files = await pickFiles({ accept: 'image/*' });
    if (!files.length) return;
    let folderId: string | null = null;
    try {
      folderId = await ensureRecentFolder();
    } catch {
      /* без папки — файл всё равно попадёт в галерею */
    }
    let count = 0;
    for (const f of files) {
      setUpload({ current: count + 1, total: files.length, pct: 0 });
      try {
        const res = await uploadFile(f, (p) => setUpload({ current: count + 1, total: files.length, pct: Math.round(p * 100) }));
        if (folderId && res && res.fileId) {
          try {
            await apiPost('/api/cloud/attach', { dir: folderId, fileId: res.fileId, name: res.name || f.name });
          } catch {
            /* attach best-effort */
          }
        }
      } catch (e) {
        toast(`«${f.name}»: ${(e as Error).message}`, 'err');
      }
      count++;
    }
    setUpload(null);
    toast(`Загружено: ${count} ${plural(count, 'файл', 'файла', 'файлов')}`);
    reload();
  }

  const groups = React.useMemo(() => groupByDate(photos), [photos]);

  usePageHeader(
    () => ({
      title: 'Фотогалерея',
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
          <button className="btn primary" onClick={doUpload}>
            <Icon.upload size={16} /> Загрузить
          </button>
        </>
      ),
    }),
    [tab],
  );

  return (
    <>
      {toastNode}
      {actionsCtx.overlay}

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

      {tab === 'photos' &&
        (loading && photos.length === 0 ? (
          <Loading />
        ) : photos.length === 0 ? (
          <EmptyState
            icon="photo"
            title="Пока нет фотографий"
            hint="Загрузите снимки — они появятся здесь и в галерее."
            action={
              <button className="btn primary" onClick={doUpload}>
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
