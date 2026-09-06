import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { AlbumCard } from '../components/albums/AlbumCard';
import { AlbumFormModal } from '../components/albums/AlbumFormModal';
import { AlbumDetail } from '../components/albums/AlbumDetail';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet } from '../lib/api';
import { plural } from '../lib/format';
import type { Album, MediaItem } from '../lib/types';

/** Отдельная вкладка «Альбомы» (бывшая вкладка внутри Фото/Видео). */
export function AlbumsPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const openAlbumId = new URLSearchParams(location.search).get('album') || '';
  const [albums, setAlbums] = React.useState<Album[] | null>(null);
  const [openAlbum, setOpenAlbum] = React.useState<Album | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [candidates, setCandidates] = React.useState<MediaItem[]>([]);
  const [toastNode, toast] = useToast();
  const resolvedOpenId = React.useRef('');

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

  React.useEffect(() => {
    if (!openAlbumId || !albums || resolvedOpenId.current === openAlbumId) return;
    resolvedOpenId.current = openAlbumId;
    const album = albums.find((item) => item.id === openAlbumId);
    if (album) {
      setOpenAlbum(album);
      return;
    }
    apiGet(`/api/search/hit?kind=album&id=${encodeURIComponent(openAlbumId)}`)
      .then(() => toast('Альбом недоступен в текущем списке', 'err'))
      .catch((e) => toast((e as Error).message || 'Альбом больше недоступен', 'err'))
      .finally(() => navigate('/albums', { replace: true }));
  }, [openAlbumId, albums, navigate, toast]);

  // Кандидаты для PickMediaModal (последние фото и видео) — лениво, при первом открытии альбома.
  const candidatesLoaded = React.useRef(false);
  React.useEffect(() => {
    if (!openAlbum || candidatesLoaded.current) return;
    candidatesLoaded.current = true;
    Promise.all([
      apiGet<{ items: MediaItem[] }>('/api/cloud/media?kind=photo&limit=100').then((d) => d.items || []),
      apiGet<{ items: MediaItem[] }>('/api/cloud/media?kind=video&limit=100').then((d) => d.items || []),
    ])
      .then(([photos, videos]) =>
        setCandidates([...photos, ...videos].sort((a, b) => (b.createdAt || '').localeCompare(a.createdAt || ''))),
      )
      .catch(() => {});
  }, [openAlbum]);

  usePageHeader(
    () => ({
      title: 'Альбомы',
      documentTitle: openAlbum ? openAlbum.name : 'Альбомы',
      documentIconUrl: openAlbum?.coverUrl || null,
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Альбомы</span>
        </>
      ),
      actions: (
        <button className="btn primary" onClick={() => setCreating(true)}>
          <Icon.plus size={16} /> Альбом
        </button>
      ),
    }),
    [openAlbum?.name, openAlbum?.coverUrl],
  );

  return (
    <>
      {toastNode}

      {openAlbum ? (
        <AlbumDetail album={openAlbum} candidates={candidates} albums={albums || []} toast={toast} onBack={() => setOpenAlbum(null)} onChanged={loadAlbums} />
      ) : albums === null ? (
        <Loading />
      ) : albums.length === 0 ? (
        <EmptyState
          icon="photo"
          title="Пока нет альбомов"
          hint="Создайте альбом и добавьте в него фото и видео из галереи."
          action={
            <button className="btn primary" onClick={() => setCreating(true)}>
              <Icon.plus size={16} /> Создать альбом
            </button>
          }
        />
      ) : (
        <>
          <div className="section-head">
            <h2>Все альбомы</h2>
            <div className="meta">
              {albums.length} {plural(albums.length, 'альбом', 'альбома', 'альбомов')}
            </div>
          </div>
          <div className="album-grid">
            {albums.map((a) => (
              <AlbumCard key={a.id} album={a} onOpen={(al) => setOpenAlbum(al)} />
            ))}
            <div className="album-card new-album" onClick={() => setCreating(true)}>
              <Icon.plus size={28} />
              <span>Создать альбом</span>
            </div>
          </div>
        </>
      )}

      {creating && (
        <AlbumFormModal
          onClose={() => setCreating(false)}
          onSaved={() => {
            setCreating(false);
            loadAlbums();
            toast('Альбом создан');
          }}
          toast={toast}
        />
      )}
    </>
  );
}
