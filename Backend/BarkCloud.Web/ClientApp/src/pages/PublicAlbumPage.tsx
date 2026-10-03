import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { PublicFileTile } from '../components/public/PublicTile';
import { PublicViewer } from '../components/public/PublicViewer';
import { countLabel, isPublicMedia } from '../components/public/publicFile';
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

/** Публичная страница альбома по шаринг-ссылке (/al/:token). Без авторизации; контент динамический. */
export function PublicAlbumPage() {
  const { token } = useParams<{ token: string }>();
  const [album, setAlbum] = React.useState<{ name: string; description: string } | null>(null);
  const [items, setItems] = React.useState<PubFile[]>([]);
  const [cursor, setCursor] = React.useState<{ at: string | null; id: string } | null>(null);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [viewer, setViewer] = React.useState<PubFile | null>(null);
  const firstPreview = items.find((f) => isPublicMedia(f.mediaKind) && f.previewUrl)?.previewUrl || null;
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
    if (isPublicMedia(f.mediaKind)) setViewer(f);
    else if (f.downloadUrl) window.location.href = f.downloadUrl;
  }

  if (state === 'loading') {
    return <PublicStatus title="Открываем альбом" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        title="Альбом недоступен"
        text="Альбом не найден или владелец отозвал доступ."
      />
    );
  }

  const photos = items.filter((f) => f.mediaKind === 'photo').length;
  const videos = items.filter((f) => f.mediaKind === 'video').length;
  return (
    <PublicShareShell>
      <PublicShareHeader
        variant="collection"
        icon={Icon.photo}
        label="Альбом"
        coverUrl={firstPreview || undefined}
        title={album?.name || 'Публичный альбом'}
        subtitle={album?.description || undefined}
        chips={[
          countLabel(items.length, 'элемент', 'элемента', 'элементов'),
          photos > 0 && videos > 0 && `${photos} фото · ${videos} видео`,
        ]}
      />

      {items.length === 0 ? (
        <div className="public-empty">Альбом пуст.</div>
      ) : (
        <section className="public-grid is-album" aria-label="Элементы публичного альбома">
          {items.map((f) => (
            <PublicFileTile key={f.fileId} file={f} plain onOpen={() => openFile(f)} />
          ))}
        </section>
      )}

      {cursor && (
        <div className="public-more">
          <button className="public-pill is-tonal public-st" type="button" onClick={loadMore} disabled={loadingMore}>
            {loadingMore ? 'Загрузка…' : 'Показать ещё'}
          </button>
        </div>
      )}

      {viewer && <PublicViewer file={viewer} onClose={() => setViewer(null)} />}
    </PublicShareShell>
  );
}
