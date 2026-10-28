import React from 'react';
import { useTextSearchOpen } from '../hooks/useTextSearchOpen';
import { MediaThumb } from '../components/media/MediaThumb';
import { Lightbox } from '../components/media/Lightbox';
import { useAudioPlayer } from '../hooks/useAudioPlayer';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { Loading } from '../components/ui/EmptyState';
import { usePageHeader } from '../hooks/usePageHeader';
import { useSearchHitMenu } from '../hooks/useSearchHitMenu';
import { useToast } from '../hooks/useToast';
import { apiGet } from '../lib/api';
import { isGridSection, matchLabel, searchHitIconName, searchHitToCardFile, searchHitToTrack, SECTION_LABEL, type SearchHit, type SearchHitKind, type SearchResponse, type SearchSection, type SearchSectionKey } from '../lib/search';
import type { CardFile } from '../lib/types';

const ORDER: SearchSectionKey[] = ['photos', 'videos', 'files', 'tracks', 'albums', 'playlists', 'folders', 'shared', 'trash', 'torrents'];

/** Секция, чьи загруженные результаты листает Lightbox; трек открывается отдельно — мини-плеером. */
const MEDIA_SECTION: Partial<Record<SearchHitKind, SearchSectionKey>> = { photo: 'photos', video: 'videos' };

type SectionState = SearchSection & { loadingMore?: boolean; error?: string };

type HitMenuHandler = (e: React.MouseEvent, hit: SearchHit) => void;

const sameHit = (a: SearchHit, b: SearchHit) => a.kind === b.kind && a.id === b.id;

export function SearchPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const player = useAudioPlayer();
  const q = new URLSearchParams(location.search).get('q')?.trim() || '';
  const [sections, setSections] = React.useState<SectionState[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  /** Открытый в Lightbox файл и его секция: листание идёт по уже загруженным результатам этой секции. */
  const [lightbox, setLightbox] = React.useState<{ section: SearchSectionKey; fileId: string } | null>(null);
  const [toastNode, toast] = useToast();

  function patchHit(hit: SearchHit, patch: Partial<SearchHit>) {
    setSections((current) => current?.map((section) => ({ ...section, items: section.items.map((item) => sameHit(item, hit) ? { ...item, ...patch } : item) })) || null);
  }
  function removeHit(hit: SearchHit) {
    setSections((current) => current?.map((section) => ({ ...section, items: section.items.filter((item) => !sameHit(item, hit)) })) || null);
  }

  /** Фото/видео открываются Lightbox-ом поверх поиска, трек — в общем мини-плеере; маршрут не меняется. */
  function openMediaFromSearch(hit: SearchHit): boolean {
    const section = MEDIA_SECTION[hit.kind];
    if (section && hit.fileId) {
      setLightbox({ section, fileId: hit.fileId });
      return true;
    }
    if (hit.kind === 'track' && hit.fileId) {
      player.playTrack(searchHitToTrack(hit));
      return true;
    }
    return false;
  }

  const { openSearchResult, textViewer } = useTextSearchOpen(openMediaFromSearch);
  const { overlay, openMenu } = useSearchHitMenu({
    toast,
    onOpen: openSearchResult,
    onRenamed: (hit, title) => patchHit(hit, { title }),
    onRemoved: removeHit,
    onPatched: patchHit,
  });

  usePageHeader(() => ({
    title: q ? `Поиск: ${q}` : 'Поиск',
    documentTitle: q ? `Поиск «${q}»` : 'Поиск',
    kicker: <><span>Библиотека</span><span className="sep">/</span><span className="cur">Поиск</span></>,
  }), [q]);

  React.useEffect(() => {
    if (q.length < 2) {
      setSections([]);
      setError(null);
      return;
    }
    const controller = new AbortController();
    setSections(null);
    setError(null);
    setLightbox(null);
    apiGet<SearchResponse>('/api/search?q=' + encodeURIComponent(q), { signal: controller.signal })
      .then((response) => {
        if (!controller.signal.aborted) {
          const ordered = [...response.sections].sort((a, b) => ORDER.indexOf(a.key) - ORDER.indexOf(b.key));
          setSections(ordered);
        }
      })
      .catch((e: Error) => {
        if (!controller.signal.aborted) setError(e.message || 'Не удалось выполнить поиск');
      });
    return () => controller.abort();
  }, [q]);

  async function loadMore(section: SectionState) {
    if (section.loadingMore || !section.hasMore || !section.nextCursor) return;
    setSections((current) => current?.map((item) => item.key === section.key ? { ...item, loadingMore: true, error: undefined } : item) || null);
    try {
      const response = await apiGet<SearchResponse>(`/api/search?q=${encodeURIComponent(q)}&section=${encodeURIComponent(section.key)}&cursor=${encodeURIComponent(section.nextCursor)}`);
      const updated = response.sections.find((item) => item.key === section.key);
      if (!updated) throw new Error('Секция поиска недоступна');
      setSections((current) => current?.map((item) => item.key === section.key
        ? { ...updated, items: [...item.items, ...updated.items], loadingMore: false }
        : item) || null);
    } catch (e) {
      setSections((current) => current?.map((item) => item.key === section.key
        ? { ...item, loadingMore: false, error: (e as Error).message || 'Не удалось загрузить ещё результаты' }
        : item) || null);
    }
  }

  // Листание Lightbox — по уже загруженным результатам секции; записи одного файла не дублируются.
  const lightboxItems = React.useMemo<CardFile[] | null>(() => {
    if (!lightbox) return null;
    const seen = new Set<string>();
    const items: CardFile[] = [];
    for (const hit of sections?.find((section) => section.key === lightbox.section)?.items || []) {
      if (!(hit.kind === 'photo' || hit.kind === 'video') || !hit.fileId || seen.has(hit.fileId)) continue;
      seen.add(hit.fileId);
      items.push(searchHitToCardFile(hit));
    }
    return items;
  }, [lightbox, sections]);
  const lightboxIndex = lightbox && lightboxItems ? lightboxItems.findIndex((card) => card.id === lightbox.fileId) : -1;

  if (q.length < 2) {
    return <div className="search-page-empty"><Icon.search size={34} /><h2>Введите не менее двух символов</h2><p>Искать можно по файлам, фото, музыке, альбомам, тегам и имени для поиска.</p></div>;
  }
  if (sections === null && !error) return <Loading />;
  if (error) return <div className="search-page-empty"><Icon.search size={34} /><h2>Поиск недоступен</h2><p>{error}</p><button className="btn outlined" onClick={() => navigate(0)}>Повторить</button></div>;

  const visible = (sections || []).filter((section) => section.items.length > 0 || section.unavailable);
  if (visible.length === 0) {
    return <><div className="search-page-empty"><Icon.search size={34} /><h2>Ничего не найдено</h2><p>Попробуйте другое имя, тег или имя исполнителя.</p></div>{overlay}{toastNode}</>;
  }

  return (
    <>
      <div className="search-page">
        <div className="search-page-summary">Результаты по «{q}»</div>
        {visible.map((section) => (
          <section className="search-section" key={section.key}>
            <div className="search-section-head"><h2>{SECTION_LABEL[section.key]}</h2><span>{section.items.length}{section.hasMore ? '+' : ''}</span></div>
            {section.unavailable ? (
              <div className="search-section-unavailable">Раздел временно недоступен. Остальные результаты поиска показаны.</div>
            ) : isGridSection(section.key) ? (
              <div className="search-result-grid">{section.items.map((hit) => <GridHit key={`${hit.kind}:${hit.id}`} hit={hit} onOpen={() => openSearchResult(hit)} onMenu={openMenu} />)}</div>
            ) : (
              <div className="search-result-list">{section.items.map((hit) => <ListHit key={`${hit.kind}:${hit.id}`} hit={hit} onOpen={() => openSearchResult(hit)} onMenu={openMenu} />)}</div>
            )}
            {section.error && <div className="search-section-error">{section.error}</div>}
            {section.hasMore && !section.unavailable && <button className="btn outlined search-more" onClick={() => loadMore(section)} disabled={section.loadingMore}>{section.loadingMore ? 'Загружаем…' : 'Показать ещё'}</button>}
          </section>
        ))}
      </div>
      {overlay}
      {toastNode}
      {textViewer}
      {lightboxItems && lightboxIndex >= 0 && (
        <Lightbox items={lightboxItems} index={lightboxIndex} onClose={() => setLightbox(null)} />
      )}
    </>
  );
}

function GridHit({ hit, onOpen, onMenu }: { hit: SearchHit; onOpen: () => void; onMenu: HitMenuHandler }) {
  return <button type="button" className="search-grid-hit" onClick={onOpen} onContextMenu={(e) => onMenu(e, hit)}>
    <HitPreview hit={hit} large />
    <span className="search-hit-title">{hit.title}</span>
    {hit.subtitle && <span className="search-hit-subtitle">{hit.subtitle}</span>}
    <MatchReason hit={hit} />
    {hit.favorite && <span className="search-favorite grid" aria-label="Избранное">★</span>}
  </button>;
}

function ListHit({ hit, onOpen, onMenu }: { hit: SearchHit; onOpen: () => void; onMenu: HitMenuHandler }) {
  return <button type="button" className="search-list-hit" onClick={onOpen} onContextMenu={(e) => onMenu(e, hit)}>
    <HitPreview hit={hit} />
    <span className="search-list-copy"><b>{hit.title}</b>{hit.subtitle && <small>{hit.subtitle}</small>}<MatchReason hit={hit} /></span>
    {hit.favorite && <span className="search-favorite" aria-label="Избранное">★</span>}
  </button>;
}

function MatchReason({ hit }: { hit: SearchHit }) {
  const label = matchLabel(hit);
  return label ? <span className="search-match-reason">Совпадение: {label}</span> : null;
}

function HitPreview({ hit, large = false }: { hit: SearchHit; large?: boolean }) {
  if (hit.mediaKind === 'photo' || hit.mediaKind === 'video') {
    return <MediaThumb
      className={'thumb search-hit-preview' + (large ? ' large' : '')}
      sizes={large ? '240px' : '44px'}
      colorPlaceholder
      media={{
        id: hit.fileId || hit.id,
        kind: hit.mediaKind,
        previews: hit.previewUrl ? [{ w: 512, target: 512, url: hit.previewUrl }] : [],
        placeholder: hit.placeholder,
      }}
    />;
  }
  const Glyph = Icon[searchHitIconName(hit)];
  return <span className={'search-hit-preview' + (large ? ' large' : '')}>{hit.previewUrl ? <img src={hit.previewUrl} alt="" /> : <Glyph size={large ? 30 : 20} />}</span>;
}
