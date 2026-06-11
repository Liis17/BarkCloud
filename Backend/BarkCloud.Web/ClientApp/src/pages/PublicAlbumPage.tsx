import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
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
    return <PublicStatus icon={Icon.photo} title="Открываем альбом" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        icon={Icon.photo}
        title="Альбом недоступен"
        text="Альбом не найден или владелец отозвал доступ."
      />
    );
  }

  return (
    <PublicShareShell>
      <PublicShareHeader
        icon={Icon.photo}
        label="Публичный альбом BarkCloud"
        title={album?.name || 'Публичный альбом'}
        subtitle={album?.description || 'Медиа открываются прямо по ссылке, без входа в аккаунт.'}
        meta={`${items.length} ${items.length === 1 ? 'элемент' : 'элементов'}`}
      />

        {items.length === 0 ? (
          <div className="public-empty">Альбом пуст.</div>
        ) : (
          <div className="public-grid">
            {items.map((f) => {
              const isMedia = f.mediaKind === 'photo' || f.mediaKind === 'video';
              return (
                <button
                  key={f.fileId}
                  onClick={() => openFile(f)}
                  title={f.name}
                  className="public-tile"
                >
                  <div className="public-tile-media">
                    {isMedia && f.previewUrl ? (
                      <img src={f.previewUrl} alt={f.name} />
                    ) : (
                      <Icon.file size={40} />
                    )}
                    {f.mediaKind === 'video' && (
                      <span className="public-play">
                        <Icon.play size={34} />
                      </span>
                    )}
                  </div>
                  <div className="public-tile-title">{f.name}</div>
                  <div className="public-tile-sub">{f.fileSize > 0 ? fmtSize(f.fileSize) : ''}</div>
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

      {viewer && (
        <div
          onClick={() => setViewer(null)}
          className="public-viewer"
        >
          <div onClick={(e) => e.stopPropagation()} className="public-viewer-body">
            {viewer.mediaKind === 'video' ? (
              <video src={viewer.downloadUrl} controls autoPlay />
            ) : (
              <img src={viewer.downloadUrl} alt={viewer.name} />
            )}
            <div className="public-viewer-actions">
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
    </PublicShareShell>
  );
}
