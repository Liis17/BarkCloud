import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { Loading } from '../components/ui/EmptyState';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet } from '../lib/api';
import { isGridSection, matchLabel, openSearchHit, searchHitIconName, SECTION_LABEL, type SearchHit, type SearchResponse, type SearchSection, type SearchSectionKey } from '../lib/search';

const ORDER: SearchSectionKey[] = ['photos', 'videos', 'files', 'tracks', 'albums', 'playlists', 'folders', 'shared', 'trash', 'torrents'];

type SectionState = SearchSection & { loadingMore?: boolean; error?: string };

export function SearchPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const q = new URLSearchParams(location.search).get('q')?.trim() || '';
  const [sections, setSections] = React.useState<SectionState[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);

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

  if (q.length < 2) {
    return <div className="search-page-empty"><Icon.search size={34} /><h2>Введите не менее двух символов</h2><p>Искать можно по файлам, фото, музыке, альбомам, тегам и имени для поиска.</p></div>;
  }
  if (sections === null && !error) return <Loading />;
  if (error) return <div className="search-page-empty"><Icon.search size={34} /><h2>Поиск недоступен</h2><p>{error}</p><button className="btn outlined" onClick={() => navigate(0)}>Повторить</button></div>;

  const visible = (sections || []).filter((section) => section.items.length > 0 || section.unavailable);
  if (visible.length === 0) {
    return <div className="search-page-empty"><Icon.search size={34} /><h2>Ничего не найдено</h2><p>Попробуйте другое имя, тег или имя исполнителя.</p></div>;
  }

  return (
    <div className="search-page">
      <div className="search-page-summary">Результаты по «{q}»</div>
      {visible.map((section) => (
        <section className="search-section" key={section.key}>
          <div className="search-section-head"><h2>{SECTION_LABEL[section.key]}</h2><span>{section.items.length}{section.hasMore ? '+' : ''}</span></div>
          {section.unavailable ? (
            <div className="search-section-unavailable">Раздел временно недоступен. Остальные результаты поиска показаны.</div>
          ) : isGridSection(section.key) ? (
            <div className="search-result-grid">{section.items.map((hit) => <GridHit key={`${hit.kind}:${hit.id}`} hit={hit} onOpen={() => openSearchHit(hit, navigate)} />)}</div>
          ) : (
            <div className="search-result-list">{section.items.map((hit) => <ListHit key={`${hit.kind}:${hit.id}`} hit={hit} onOpen={() => openSearchHit(hit, navigate)} />)}</div>
          )}
          {section.error && <div className="search-section-error">{section.error}</div>}
          {section.hasMore && !section.unavailable && <button className="btn outlined search-more" onClick={() => loadMore(section)} disabled={section.loadingMore}>{section.loadingMore ? 'Загружаем…' : 'Показать ещё'}</button>}
        </section>
      ))}
    </div>
  );
}

function GridHit({ hit, onOpen }: { hit: SearchHit; onOpen: () => void }) {
  return <button type="button" className="search-grid-hit" onClick={onOpen}>
    <HitPreview hit={hit} large />
    <span className="search-hit-title">{hit.title}</span>
    {hit.subtitle && <span className="search-hit-subtitle">{hit.subtitle}</span>}
    <MatchReason hit={hit} />
    {hit.favorite && <span className="search-favorite grid" aria-label="Избранное">★</span>}
  </button>;
}

function ListHit({ hit, onOpen }: { hit: SearchHit; onOpen: () => void }) {
  return <button type="button" className="search-list-hit" onClick={onOpen}>
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
  const Glyph = Icon[searchHitIconName(hit)];
  return <span className={'search-hit-preview' + (large ? ' large' : '')}>{hit.previewUrl ? <img src={hit.previewUrl} alt="" /> : <Glyph size={large ? 30 : 20} />}</span>;
}
