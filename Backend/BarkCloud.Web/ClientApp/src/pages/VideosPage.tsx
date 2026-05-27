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
import { plural, dateLabel } from '../lib/format';
import type { Album, MediaItem } from '../lib/types';

const RECENT_FOLDER = 'Недавно загруженные';

function fmtSize(bytes: number): string {
  if (!bytes) return '0 Б';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0,
    v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}
function resLabel(m: MediaItem): string {
  const h = m.height || 0;
  if (h >= 2160) return '4K';
  if (h >= 1440) return '1440p';
  if (h >= 1080) return '1080p';
  if (h >= 720) return '720p';
  if (h > 0) return 'SD';
  return '';
}
function shortDate(iso: string | null): string {
  return iso ? dateLabel(new Date(iso)) : '';
}

function VideoCard({ m, onOpen, onMenu }: { m: MediaItem; onOpen: (m: MediaItem) => void; onMenu: (e: React.MouseEvent, m: MediaItem) => void }) {
  const res = resLabel(m);
  return (
    <div className="vcard" onClick={() => onOpen(m)} onContextMenu={(e) => onMenu(e, m)}>
      <div className="vthumb">
        <MediaThumb media={m} sizes="(max-width: 700px) 100vw, 320px" />
        <button className="play">
          <Icon.play size={22} />
        </button>
        {res && <div className="res">{res}</div>}
      </div>
      <div className="vt">{m.name}</div>
      <div className="vmeta">
        <span>{shortDate(m.createdAt)}</span>
        <span className="dot">·</span>
        <span>{m.sizeLabel || fmtSize(m.size)}</span>
      </div>
    </div>
  );
}

interface UploadState {
  pct: number;
  current: number;
  total: number;
}

export function VideosPage() {
  const [tab, setTab] = React.useState<'videos' | 'albums'>('videos');
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [openAlbum, setOpenAlbum] = React.useState<Album | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [upload, setUpload] = React.useState<UploadState | null>(null);
  const [toastNode, toast] = useToast();

  const { items: videos, loading, done, sentinelRef, removeItem, updateItem, reload } = useInfiniteMedia('video', toast);

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
    const files = await pickFiles({ accept: 'video/*' });
    if (!files.length) return;
    let folderId: string | null = null;
    try {
      folderId = await ensureRecentFolder();
    } catch {
      /* без папки — видео всё равно попадёт в галерею */
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
    toast(`Загружено: ${count} ${plural(count, 'видео', 'видео', 'видео')}`);
    reload();
  }

  const featured = videos.length ? videos[0] : null;
  const totalSize = videos.reduce((s, v) => s + (v.size || 0), 0);
  const stats = [
    { k: 'Всего видео', v: videos.length ? videos.length + (done ? '' : '+') : '—' },
    { k: 'Занято видео', v: fmtSize(totalSize) },
    { k: 'Альбомов', v: albums ? String(albums.length) : '—' },
  ];

  usePageHeader(
    () => ({
      title: 'Видео',
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Видео</span>
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
            <Icon.upload size={16} /> Загрузить видео
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

      <div className="stat-strip">
        {stats.map((s, i) => (
          <div key={i} className="stat">
            <div className="k">{s.k}</div>
            <div className="v">{s.v}</div>
          </div>
        ))}
      </div>

      <div className="vid-toolbar">
        <div className="chip-row">
          <button className={'chip' + (tab === 'videos' ? ' active' : '')} onClick={() => { setTab('videos'); setOpenAlbum(null); }}>
            {tab === 'videos' && <Icon.check size={16} />} Все видео
            <span className="count">
              {videos.length}
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
          <AlbumDetail album={openAlbum} candidates={videos} toast={toast} onBack={() => setOpenAlbum(null)} onChanged={() => loadAlbums()} />
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

      {tab === 'videos' &&
        (loading && videos.length === 0 ? (
          <Loading />
        ) : videos.length === 0 ? (
          <EmptyState
            icon="video"
            title="Пока нет видео"
            hint="Загрузите ролики — обложкой станет кадр из видео."
            action={
              <button className="btn primary" onClick={doUpload}>
                <Icon.upload size={16} /> Загрузить видео
              </button>
            }
          />
        ) : (
          <>
            {featured && (
              <div className="featured-vid" onClick={() => setLightbox(0)} onContextMenu={(e) => actionsCtx.openMenu(e, featured)}>
                <MediaThumb media={featured} sizes="100vw" />
                <div className="overlay">
                  <div className="kicker">
                    <span className="pin">★ Последнее</span>
                    <span>{shortDate(featured.createdAt)}</span>
                  </div>
                  <div className="title">{featured.name}</div>
                  <div className="meta-row">
                    {resLabel(featured) && (
                      <span>
                        <span className="key">Качество</span> {resLabel(featured)}
                      </span>
                    )}
                    <span>
                      <span className="key">Размер</span> {featured.sizeLabel || fmtSize(featured.size)}
                    </span>
                    {featured.width > 0 && (
                      <span>
                        <span className="key">Кадр</span> {featured.width}×{featured.height}
                      </span>
                    )}
                  </div>
                </div>
                <button className="play-btn">
                  <Icon.play size={36} />
                </button>
                {resLabel(featured) && <div className="res-badge">{resLabel(featured)}</div>}
              </div>
            )}

            <div className="section-head">
              <h2>Все видео</h2>
              <div className="meta">
                {videos.length} {plural(videos.length, 'ролик', 'ролика', 'роликов')}
              </div>
            </div>

            <div className="vid-grid">
              {videos.map((m, idx) => (
                <VideoCard key={m.id} m={m} onOpen={() => setLightbox(idx)} onMenu={actionsCtx.openMenu} />
              ))}
            </div>
            <div ref={sentinelRef} className="infinite-sentinel">
              {loading && videos.length > 0 && <Loading label="Загрузка…" />}
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
      {lightbox !== null && <Lightbox items={videos} index={lightbox} onClose={() => setLightbox(null)} />}
    </>
  );
}
