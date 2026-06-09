import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { useDocumentHead } from '../hooks/useDocumentHead';

interface PubFile {
  fileId: string;
  name: string;
  mediaKind: string;
  downloadUrl: string;
  previewUrl: string;
  fileSize: number;
  imageWidth: number;
  imageHeight: number;
}
interface AlbumListing {
  found: boolean;
  albumName: string;
  description: string;
  items: PubFile[];
  nextCursorAt: string | null;
  nextCursorId: string;
}

function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

const wrap: React.CSSProperties = { minHeight: '100vh', background: 'var(--md-surface, #101014)', color: 'var(--md-on-surface, #e6e6ea)' };
const inner: React.CSSProperties = { maxWidth: 1100, margin: '0 auto', padding: '24px 20px 64px' };

/** Публичная страница альбома по шаринг-ссылке (/al/:token). Без авторизации; контент динамический. */
export function PublicAlbumPage() {
  const { token } = useParams<{ token: string }>();
  const [album, setAlbum] = React.useState<{ name: string; description: string } | null>(null);
  const [items, setItems] = React.useState<PubFile[]>([]);
  const [cursor, setCursor] = React.useState<{ at: string | null; id: string } | null>(null);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [viewer, setViewer] = React.useState<PubFile | null>(null);
  const firstPreview = items.find((f) => (f.mediaKind === 'photo' || f.mediaKind === 'video') && f.previewUrl)?.previewUrl || null;
  const headTitle = viewer?.name || album?.name || (state === 'notfound' ? 'Альбом недоступен' : 'Публичный альбом');
  const headIconUrl = viewer?.previewUrl || firstPreview;

  useDocumentHead(
    () => ({ title: headTitle, iconUrl: headIconUrl }),
    [headTitle, headIconUrl],
  );

  const load = React.useCallback(
    async (more: boolean) => {
      const qs = new URLSearchParams();
      if (more && cursor?.at) {
        qs.set('cursorAt', cursor.at);
        qs.set('cursorId', cursor.id);
      }
      const r = await fetch(`/al/${token}/list?${qs.toString()}`);
      if (!r.ok) throw new Error('not found');
      const d: AlbumListing = await r.json();
      if (!d.found) throw new Error('not found');
      setAlbum({ name: d.albumName, description: d.description });
      setItems((prev) => (more ? [...prev, ...d.items] : d.items));
      setCursor(d.nextCursorAt ? { at: d.nextCursorAt, id: d.nextCursorId } : null);
    },
    [token, cursor],
  );

  React.useEffect(() => {
    let alive = true;
    setState('loading');
    load(false)
      .then(() => alive && setState('ok'))
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  async function loadMore() {
    if (loadingMore || !cursor) return;
    setLoadingMore(true);
    try {
      await load(true);
    } catch {
      /* ignore */
    } finally {
      setLoadingMore(false);
    }
  }

  function openFile(f: PubFile) {
    if (f.mediaKind === 'photo' || f.mediaKind === 'video') setViewer(f);
    else if (f.downloadUrl) window.location.href = f.downloadUrl;
  }

  if (state === 'loading') {
    return (
      <div style={{ ...wrap, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <span className="spinner" />
      </div>
    );
  }
  if (state === 'notfound') {
    return (
      <div style={{ ...wrap, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24, textAlign: 'center' }}>
        <div>
          <Icon.photo size={44} />
          <h2 style={{ margin: '12px 0 6px' }}>Альбом недоступен</h2>
          <p style={{ color: 'var(--md-on-surface-variant, #9a9aa6)' }}>Альбом не найден или ссылка была отозвана.</p>
        </div>
      </div>
    );
  }

  return (
    <div style={wrap}>
      <div style={inner}>
        <div style={{ marginBottom: 18 }}>
          <h1 style={{ margin: 0, fontSize: 24, display: 'inline-flex', alignItems: 'center', gap: 10 }}>
            <Icon.photo size={26} /> {album?.name}
          </h1>
          {album?.description ? (
            <p style={{ margin: '6px 0 0', color: 'var(--md-on-surface-variant, #9a9aa6)' }}>{album.description}</p>
          ) : null}
          <p style={{ margin: '6px 0 0', fontSize: 13, color: 'var(--md-on-surface-variant, #9a9aa6)' }}>
            {items.length} {items.length === 1 ? 'элемент' : 'элементов'}
          </p>
        </div>

        {items.length === 0 ? (
          <p style={{ color: 'var(--md-on-surface-variant, #9a9aa6)' }}>Альбом пуст.</p>
        ) : (
          <div className="pubfolder-grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 14 }}>
            {items.map((f) => {
              const isMedia = f.mediaKind === 'photo' || f.mediaKind === 'video';
              return (
                <button
                  key={f.fileId}
                  onClick={() => openFile(f)}
                  title={f.name}
                  style={{
                    display: 'flex',
                    flexDirection: 'column',
                    borderRadius: 12,
                    overflow: 'hidden',
                    border: '1px solid var(--md-outline-variant, #333)',
                    background: 'var(--md-surface-container, #1b1b22)',
                    color: 'inherit',
                    cursor: 'pointer',
                    padding: 0,
                  }}
                >
                  <div style={{ position: 'relative', aspectRatio: '1 / 1', background: 'var(--md-surface-container-high, #25252e)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    {isMedia && f.previewUrl ? (
                      <img src={f.previewUrl} alt={f.name} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                    ) : (
                      <Icon.file size={40} />
                    )}
                    {f.mediaKind === 'video' && (
                      <span style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', pointerEvents: 'none' }}>
                        <Icon.play size={34} />
                      </span>
                    )}
                  </div>
                  <div style={{ padding: '8px 10px', textAlign: 'left' }}>
                    <div style={{ fontSize: 13, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.name}</div>
                    {f.fileSize > 0 && <div style={{ fontSize: 11, color: 'var(--md-on-surface-variant, #9a9aa6)' }}>{fmtSize(f.fileSize)}</div>}
                  </div>
                </button>
              );
            })}
          </div>
        )}

        {cursor && (
          <div style={{ textAlign: 'center', marginTop: 20 }}>
            <button className="btn outlined" onClick={loadMore} disabled={loadingMore}>
              {loadingMore ? 'Загрузка…' : 'Показать ещё'}
            </button>
          </div>
        )}
      </div>

      {viewer && (
        <div
          onClick={() => setViewer(null)}
          style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,.85)', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24, zIndex: 50 }}
        >
          <div onClick={(e) => e.stopPropagation()} style={{ maxWidth: '92vw', maxHeight: '92vh', textAlign: 'center' }}>
            {viewer.mediaKind === 'video' ? (
              <video src={viewer.downloadUrl} controls autoPlay style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8 }} />
            ) : (
              <img src={viewer.downloadUrl} alt={viewer.name} style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8, objectFit: 'contain' }} />
            )}
            <div style={{ marginTop: 12, display: 'flex', gap: 10, justifyContent: 'center' }}>
              <a className="btn primary" href={viewer.downloadUrl} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <Icon.download size={16} /> Скачать
              </a>
              <button className="btn outlined" onClick={() => setViewer(null)}>
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
