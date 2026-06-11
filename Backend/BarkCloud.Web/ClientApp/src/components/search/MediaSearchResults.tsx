import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../Icon';
import { MediaThumb } from '../media/MediaThumb';
import { Lightbox } from '../media/Lightbox';
import { EmptyState, Loading } from '../ui/EmptyState';
import { apiGet } from '../../lib/api';
import { GRID_SIZES, plural } from '../../lib/format';
import type { Entry, MediaItem } from '../../lib/types';

interface SearchResponse {
  files: Entry[];
  nextCursorAt: string | null;
  nextCursorId: string;
}

function toMediaItem(e: Entry): MediaItem {
  return {
    ...(e.media as MediaItem),
    entryIds: [e.entryId],
    entryNames: [e.name],
    entriesCount: 1,
  };
}

/** Результаты поиска фото/видео (вкладки «Фото»/«Видео», ?q=): сетка с превью,
 *  «Показать ещё» по cursor-пагинации, Lightbox по клику. */
export function MediaSearchResults({ q }: { q: string }) {
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const [items, setItems] = React.useState<MediaItem[] | null>(null);
  const [cursor, setCursor] = React.useState<{ at: string; id: string } | null>(null);
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [lightbox, setLightbox] = React.useState<number | null>(null);

  const fetchPage = React.useCallback(
    (after?: { at: string; id: string }) => {
      let url = `/api/cloud/search?q=${encodeURIComponent(q)}&kind=media&limit=60`;
      if (after) url += `&cursorAt=${encodeURIComponent(after.at)}&cursorId=${encodeURIComponent(after.id)}`;
      return apiGet<SearchResponse>(url).then((d) => {
        const page = (d.files || []).filter((e) => e.media).map(toMediaItem);
        setCursor(d.nextCursorAt && d.nextCursorId ? { at: d.nextCursorAt, id: d.nextCursorId } : null);
        return page;
      });
    },
    [q],
  );

  React.useEffect(() => {
    setItems(null);
    setCursor(null);
    fetchPage()
      .then(setItems)
      .catch(() => setItems([]));
  }, [fetchPage]);

  function loadMore() {
    if (!cursor || loadingMore) return;
    setLoadingMore(true);
    fetchPage(cursor)
      .then((page) => setItems((prev) => [...(prev || []), ...page]))
      .catch(() => {})
      .finally(() => setLoadingMore(false));
  }

  return (
    <>
      <div className="section-head">
        <h2 style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <Icon.search size={18} /> Результаты поиска: «{q}»
        </h2>
        <div className="meta" style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          {items && (
            <span>
              {items.length}
              {cursor ? '+' : ''} {plural(items.length, 'результат', 'результата', 'результатов')}
            </span>
          )}
          <a onClick={() => navigate(pathname)} style={{ cursor: 'pointer', display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <Icon.x size={14} /> Очистить
          </a>
        </div>
      </div>

      {items === null ? (
        <Loading />
      ) : items.length === 0 ? (
        <EmptyState icon="search" title="Ничего не найдено" hint={`По запросу «${q}» фото и видео не нашлось.`} />
      ) : (
        <>
          <div className="photo-grid">
            {items.map((m, idx) => (
              <div key={m.id + idx} className="photo" onClick={() => setLightbox(idx)}>
                <MediaThumb media={m} sizes={GRID_SIZES} />
                {m.kind === 'video' && (
                  <div className="vbadge">
                    <Icon.play size={10} /> видео
                  </div>
                )}
              </div>
            ))}
          </div>
          {cursor && (
            <div style={{ display: 'flex', justifyContent: 'center', padding: '18px 0' }}>
              <button className="btn outlined" onClick={loadMore} disabled={loadingMore}>
                {loadingMore ? 'Загрузка…' : 'Показать ещё'}
              </button>
            </div>
          )}
        </>
      )}

      {lightbox !== null && items && <Lightbox items={items} index={lightbox} onClose={() => setLightbox(null)} />}
    </>
  );
}
