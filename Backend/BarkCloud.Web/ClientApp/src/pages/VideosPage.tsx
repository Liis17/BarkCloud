import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
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
import { searchHitToCardFile, type SearchHit } from '../lib/search';
import type { Album, MediaItem, VideoMeta } from '../lib/types';

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
  // По меньшей стороне — корректно для вертикальных (портретных) роликов.
  const w = m.width || 0;
  const h = m.height || 0;
  const lines = w && h ? Math.min(w, h) : h;
  if (lines >= 2160) return '4K';
  if (lines >= 1440) return '2K';
  if (lines >= 1080) return '1080p';
  if (lines >= 720) return '720p';
  if (lines > 0) return 'SD';
  return '';
}
function shortDate(iso: string | null): string {
  return iso ? dateLabel(new Date(iso)) : '';
}
function fmtDuration(seconds: number): string {
  const s = Math.round(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n: number) => (n < 10 ? '0' + n : String(n));
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
}
const CODEC_NAMES: Record<string, string> = {
  hevc: 'H.265', h265: 'H.265', h264: 'H.264', avc: 'H.264', av1: 'AV1',
  vp9: 'VP9', vp8: 'VP8', mpeg4: 'MPEG-4', mpeg2video: 'MPEG-2', prores: 'ProRes',
  aac: 'AAC', mp3: 'MP3', opus: 'Opus', flac: 'FLAC', vorbis: 'Vorbis',
  ac3: 'AC-3', eac3: 'E-AC-3', alac: 'ALAC', pcm_s16le: 'PCM', pcm_s24le: 'PCM',
};
function prettyCodec(c?: string): string {
  if (!c) return '';
  const k = c.toLowerCase();
  return CODEC_NAMES[k] || c.toUpperCase();
}
/** «H.265 | AAC» — кодеки видео и аудио через вертикальную черту. */
function codecLabel(v?: VideoMeta): string {
  if (!v) return '';
  return [prettyCodec(v.videoCodec), prettyCodec(v.audioCodec)].filter(Boolean).join(' | ');
}
const HIGH_BITRATE = 100_000_000; // 100 Мбит/с
function fmtMbps(bps: number): string {
  return Math.round(bps / 1_000_000) + ' Мбит/с';
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
  const v = m.video;
  const dur = v?.duration ? fmtDuration(v.duration) : '';
  const codecs = codecLabel(v);
  const hdr = v?.hdr === true;
  const highBitrate = (v?.bitrate || 0) > HIGH_BITRATE;
  return (
    <div
      className={'vcard' + (checked ? ' checked' : '')}
      onClick={(e) => (e.shiftKey ? onToggle(true) : selecting ? onToggle(false) : onOpen(m))}
      onContextMenu={(e) => onMenu(e, m)}
    >
      <div className="vthumb">
        <MediaThumb media={m} sizes="(max-width: 700px) 100vw, 320px" />
        <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать">
          {checked ? <Icon.check size={14} /> : null}
        </button>
        <button className="play">
          <Icon.play size={22} />
        </button>

        {/* верх-право: разрешение, HDR, кодеки — отдельными плашками */}
        {(res || hdr || codecs) && (
          <div className="v-badges tr">
            {res && <span className="vbadge res">{res}</span>}
            {hdr && <span className="vbadge hdr">HDR</span>}
            {codecs && <span className="vbadge codec">{codecs}</span>}
          </div>
        )}

        {/* низ-лево: высокий битрейт (> 100 Мбит/с) */}
        {highBitrate && (
          <div className="vbadge bitrate" title="Суммарный битрейт выше 100 Мбит/с">
            <Icon.arrow size={12} style={{ transform: 'rotate(-90deg)' }} />
            {fmtMbps(v!.bitrate!)}
          </div>
        )}

        {/* низ-право: длительность */}
        {dur && <div className="vbadge dur">{dur}</div>}
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
  const navigate = useNavigate();
  const searchQuery = (new URLSearchParams(location.search).get('q') || '').trim();
  const openFileId = new URLSearchParams(location.search).get('open') || '';
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [lightbox, setLightbox] = React.useState<number | null>(null);
  const [deepMedia, setDeepMedia] = React.useState<MediaItem | null>(null);
  const resolvedOpenId = React.useRef('');
  const [toastNode, toast] = useToast();
  const { enqueue, attachVersion } = useUploadActions();

  const { items: videos, loading, done, sentinelRef, removeItem, updateItem, prependItems } = useInfiniteMedia('video', toast);

  React.useEffect(() => {
    if (!openFileId || resolvedOpenId.current === openFileId) return;
    const index = videos.findIndex((item) => item.id === openFileId);
    if (index >= 0) {
      resolvedOpenId.current = openFileId;
      setLightbox(index);
      return;
    }
    resolvedOpenId.current = openFileId;
    apiGet<SearchHit>(`/api/search/hit?kind=video&id=${encodeURIComponent(openFileId)}`)
      .then((hit) => setDeepMedia({ ...searchHitToCardFile(hit), entriesCount: 0, entryNames: [], entryIds: [] }))
      .catch((e) => { toast((e as Error).message || 'Видео больше недоступно', 'err'); navigate('/videos', { replace: true }); });
  }, [openFileId, videos, toast, navigate]);

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
                      onOpen={(mm) => { bulk.setAnchor(mm.id); setLightbox(videos.findIndex((v) => v.id === mm.id)); }}
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
      {deepMedia && <Lightbox media={deepMedia} actions={actionsCtx.api} onClose={() => { setDeepMedia(null); navigate('/videos', { replace: true }); }} />}
    </>
  );
}
