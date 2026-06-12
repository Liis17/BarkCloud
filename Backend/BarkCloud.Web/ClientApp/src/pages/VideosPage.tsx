import React from 'react';
import { useLocation } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { MediaSearchResults } from '../components/search/MediaSearchResults';
import { useToast } from '../hooks/useToast';
import { useInfiniteMedia } from '../hooks/useInfiniteMedia';
import { useMediaActions } from '../hooks/useMediaActions';
import { useFileDrop } from '../hooks/useFileDrop';
import { useBulkMedia } from '../hooks/useBulkMedia';
import { usePageHeader } from '../hooks/usePageHeader';
import { useUploadActions } from '../hooks/useUploadManager';
import { apiGet, pickFiles } from '../lib/api';
import { plural, dateLabel, groupByDate } from '../lib/format';
import type { Album, MediaItem } from '../lib/types';

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

function VideoCard({ m, selecting, checked, onToggle, onOpen, onMenu }: {
  m: MediaItem;
  selecting: boolean;
  checked: boolean;
  onToggle: (shift: boolean) => void;
  onOpen: (m: MediaItem) => void;
  onMenu: (e: React.MouseEvent, m: MediaItem) => void;
}) {
  const res = resLabel(m);
  return (
    <div className={'vcard' + (checked ? ' checked' : '')} onClick={(e) => (selecting ? onToggle(e.shiftKey) : onOpen(m))} onContextMenu={(e) => onMenu(e, m)}>
      <div className="vthumb">
        <MediaThumb media={m} sizes="(max-width: 700px) 100vw, 320px" />
        <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать">
          {checked ? <Icon.check size={14} /> : null}
        </button>
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

export function VideosPage() {
  const location = useLocation();
  const searchQuery = (new URLSearchParams(location.search).get('q') || '').trim();
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [toastNode, toast] = useToast();
  const { enqueue, attachVersion } = useUploadActions();

  const { items: videos, loading, done, sentinelRef, removeItem, updateItem, prependItems } = useInfiniteMedia('video', toast);

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
    const files = dropped && dropped.length ? dropped : await pickFiles({ accept: 'video/*' });
    if (!files.length) return;
    enqueue(files, { routeByMediaKind: true });
  }

  const { over, dropHandlers } = useFileDrop((f) => doUpload(f));
  const bulk = useBulkMedia({ items: videos, albums: albums || [], toast, onRemoved: removeItem, onReloadAlbums: loadAlbums });

  const prependRef = React.useRef(prependItems);
  prependRef.current = prependItems;

  const featured = videos.length ? videos[0] : null;
  const totalSize = videos.reduce((s, v) => s + (v.size || 0), 0);
  const groups = React.useMemo(() => groupByDate(videos), [videos]);

  React.useEffect(() => {
    apiGet<{ items: MediaItem[] }>('/api/cloud/media?kind=video&limit=60')
      .then((d) => prependRef.current(d.items || []))
      .catch(() => {});
  }, [attachVersion]);
  const stats = [
    { k: 'Всего видео', v: videos.length ? videos.length + (done ? '' : '+') : '—' },
    { k: 'Занято видео', v: fmtSize(totalSize) },
    { k: 'Альбомов', v: albums ? String(albums.length) : '—' },
  ];

  usePageHeader(
    () => ({
      title: 'Видео',
      documentTitle: 'Видео',
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Видео</span>
        </>
      ),
      actions: (
        <button className="btn primary" onClick={() => doUpload()}>
          <Icon.upload size={16} /> Загрузить видео
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
            <span>Отпустите видео для загрузки</span>
          </div>
        )}

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
          <span className="chip active">
            <Icon.check size={16} /> Все видео
            <span className="count">
              {videos.length}
              {done ? '' : '+'}
            </span>
          </span>
        </div>
      </div>

      {(loading && videos.length === 0 ? (
          <Loading />
        ) : videos.length === 0 ? (
          <EmptyState
            icon="video"
            title="Пока нет видео"
            hint="Загрузите ролики — обложкой станет кадр из видео."
            action={
              <button className="btn primary" onClick={() => doUpload()}>
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

            {groups.map((g) => (
              <div key={g.key} className="date-group">
                <div className="date-head">
                  <h3>{g.label}</h3>
                  <div className="right">
                    <span>
                      {g.items.length} {plural(g.items.length, 'ролик', 'ролика', 'роликов')}
                    </span>
                  </div>
                </div>
                <div className="vid-grid">
                  {g.items.map((m) => (
                    <VideoCard
                      key={m.id}
                      m={m}
                      selecting={bulk.active}
                      checked={bulk.isSelected(m.id)}
                      onToggle={(shift) => bulk.toggle(m.id, shift)}
                      onOpen={() => setLightbox(videos.findIndex((v) => v.id === m.id))}
                      onMenu={actionsCtx.openMenu}
                    />
                  ))}
                </div>
              </div>
            ))}
            <div ref={sentinelRef} className="infinite-sentinel">
              {loading && videos.length > 0 && <Loading label="Загрузка…" />}
            </div>
          </>
        ))}
      </div>

      {lightbox !== null && <Lightbox items={videos} index={lightbox} actions={actionsCtx.api} onClose={() => setLightbox(null)} />}
    </>
  );
}
