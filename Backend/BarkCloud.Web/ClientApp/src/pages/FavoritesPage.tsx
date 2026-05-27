import React from 'react';
import { Icon } from '../components/Icon';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet, apiPost } from '../lib/api';
import { GRID_SIZES, plural, groupByDate } from '../lib/format';
import type { CardFile, Page } from '../lib/types';

// Файл можно открыть в просмотрщике, только если это фото/видео с готовым превью.
const viewable = (m: CardFile) => m.previews && m.previews.length > 0 && (m.kind === 'photo' || m.kind === 'video');

function FavCard({ m, onOpen, onUnstar }: { m: CardFile; onOpen: (m: CardFile) => void; onUnstar: (m: CardFile) => void }) {
  return (
    <div className="photo" onClick={() => onOpen(m)}>
      {viewable(m) ? (
        <MediaThumb media={m} sizes={GRID_SIZES} />
      ) : (
        <div className="fav-doc">
          <Icon.file size={40} />
          <span className="ext">{m.ext || 'FILE'}</span>
          <span className="nm">{m.name}</span>
        </div>
      )}
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
                <FavCard key={m.id} m={m} onOpen={open} onUnstar={unstar} />
              ))}
            </div>
          </div>
        ))
      )}

      {lightbox && <Lightbox media={lightbox} onClose={() => setLightbox(null)} />}
    </>
  );
}
