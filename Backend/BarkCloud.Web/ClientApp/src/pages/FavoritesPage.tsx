import React from 'react';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { useToast } from '../hooks/useToast';
import { useMediaActions } from '../hooks/useMediaActions';
import { useBulkMedia } from '../hooks/useBulkMedia';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet, apiPost } from '../lib/api';
import { GRID_SIZES, plural, groupByDate } from '../lib/format';
import type { Album, CardFile, Page } from '../lib/types';

// Файл можно открыть в просмотрщике, только если это фото/видео с готовым превью.
const viewable = (m: CardFile) => m.previews && m.previews.length > 0 && (m.kind === 'photo' || m.kind === 'video');

function FavCard({ m, selecting, checked, onToggle, onOpen, onUnstar }: {
  m: CardFile;
  selecting: boolean;
  checked: boolean;
  onToggle: (shift: boolean) => void;
  onOpen: (m: CardFile) => void;
  onUnstar: (m: CardFile) => void;
}) {
  return (
    <div
      className={'photo' + (checked ? ' checked' : '')}
      onClick={(e) => (e.shiftKey ? onToggle(true) : selecting ? onToggle(false) : onOpen(m))}
    >
      {viewable(m) ? (
        <MediaThumb media={m} sizes={GRID_SIZES} />
      ) : (
        <div className="fav-doc">
          <Icon.file size={40} />
          <span className="ext">{m.ext || 'FILE'}</span>
          <span className="nm">{m.name}</span>
        </div>
      )}
      <button className="selbox" onClick={(e) => { e.stopPropagation(); onToggle(e.shiftKey); }} title="Выбрать">
        {checked ? <Icon.check size={14} /> : null}
      </button>
      {m.kind === 'video' && (
        <div className="vbadge">
          <Icon.play size={10} /> видео
        </div>
      )}
      <button
        className="fav-unstar"
        title="Убрать из избранного"
        onClick={(e) => {
          e.stopPropagation();
          onUnstar(m);
        }}
      >
        <Icon.star size={16} />
      </button>
    </div>
  );
}

export function FavoritesPage() {
  const [items, setItems] = React.useState<CardFile[] | null>(null);
  const [lightbox, setLightbox] = React.useState<CardFile | null>(null);
  const [albums, setAlbums] = React.useState<Album[]>([]);
  const [toastNode, toast] = useToast();

  const load = React.useCallback(() => {
    setItems(null);
    apiGet<Page<CardFile>>('/api/cloud/favorites')
      .then((d) => setItems(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setItems([]);
      });
  }, [toast]);
  React.useEffect(load, [load]);

  const loadAlbums = React.useCallback(() => {
    apiGet<{ albums: Album[] }>('/api/albums')
      .then((d) => setAlbums(d.albums || []))
      .catch(() => {});
  }, []);
  React.useEffect(() => {
    loadAlbums();
  }, [loadAlbums]);

  // Панель действий Lightbox: удалённый файл убираем из списка и закрываем вьювер.
  const actionsCtx = useMediaActions({
    albums,
    toast,
    onRemoved: (m) => {
      setItems((list) => (list || []).filter((x) => x.id !== m.id));
      setLightbox((lb) => (lb && lb.id === m.id ? null : lb));
    },
    reloadAlbums: loadAlbums,
  });

  async function download(m: CardFile) {
    try {
      const d = await apiGet<{ urls: Record<string, string | null> }>('/api/files/download?ids=' + encodeURIComponent(m.id));
      const url = d.urls && d.urls[m.id];
      if (url) window.open(url, '_blank');
      else toast('Ссылка недоступна', 'err');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  function open(m: CardFile) {
    if (viewable(m)) setLightbox(m);
    else download(m);
  }

  async function unstar(m: CardFile) {
    try {
      await apiPost('/api/cloud/favorites/remove', { fileId: m.id });
      setItems((list) => (list || []).filter((x) => x.id !== m.id));
      toast('Убрано из избранного');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  const bulk = useBulkMedia({
    items: items || [],
    albums,
    toast,
    onRemoved: (id) => {
      setItems((list) => (list || []).filter((x) => x.id !== id));
      setLightbox((lb) => (lb && lb.id === id ? null : lb));
    },
    onReloadAlbums: loadAlbums,
  });

  const groups = React.useMemo(() => (items ? groupByDate(items) : []), [items]);

  usePageHeader(
    () => ({
      title: 'Избранное',
      kicker: (
        <>
          <span>Совместное</span>
          <span className="sep">/</span>
          <span className="cur">Избранное</span>
        </>
      ),
    }),
    [],
  );

  return (
    <>
      {toastNode}
      {actionsCtx.overlay}
      {bulk.bar}
      {bulk.overlay}

      {items === null ? (
        <Loading />
      ) : items.length === 0 ? (
        <EmptyState icon="star" title="Пока нет избранного" hint="Помеченные файлы будут появляться здесь." />
      ) : (
        groups.map((g) => (
          <div key={g.key} className="date-group">
            <div className="date-head">
              <h3>{g.label}</h3>
              <div className="right">
                <span>
                  {g.items.length} {plural(g.items.length, 'файл', 'файла', 'файлов')}
                </span>
              </div>
            </div>
            <div className="photo-grid">
              {g.items.map((m) => (
                <FavCard
                  key={m.id}
                  m={m}
                  selecting={bulk.active}
                  checked={bulk.isSelected(m.id)}
                  onToggle={(shift) => bulk.toggle(m.id, shift)}
                  onOpen={(mm) => { bulk.setAnchor(mm.id); open(mm); }}
                  onUnstar={unstar}
                />
              ))}
            </div>
          </div>
        ))
      )}

      {lightbox && <Lightbox media={lightbox} actions={actionsCtx.api} onClose={() => setLightbox(null)} />}
    </>
  );
}
