import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../Icon';
import { UploadIndicator } from '../upload/UploadIndicator';
import { apiGet } from '../../lib/api';
import { openSearchHit, searchHitIconName, SECTION_LABEL, type SearchHit, type SearchResponse } from '../../lib/search';
import type { PageHeader } from '../../hooks/usePageHeader';

const LISTBOX_ID = 'global-search-results';

/** Глобальный поиск. Запросы отменяются, чтобы устаревшая подсказка не перезаписала новую. */
export function Topbar({ kicker, title, actions }: PageHeader) {
  const navigate = useNavigate();
  const location = useLocation();
  const [q, setQ] = React.useState('');
  const [data, setData] = React.useState<SearchResponse | null>(null);
  const [open, setOpen] = React.useState(false);
  const [loading, setLoading] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [active, setActive] = React.useState(-1);

  const query = q.trim();
  // Ограничение дублируется на клиенте: даже ошибочный ответ API не раздует подсказку.
  const displaySections = React.useMemo(() => data?.sections
    .map((section) => ({ ...section, items: section.items.slice(0, 3) }))
    .filter((section) => section.items.length > 0 || section.unavailable) || [], [data]);
  const hits = React.useMemo(() => displaySections.flatMap((section) => section.items), [displaySections]);

  React.useEffect(() => {
    setQ(new URLSearchParams(location.search).get('q') || '');
  }, [location.pathname, location.search]);

  React.useEffect(() => {
    if (query.length < 2 || !open) {
      setData(null);
      setLoading(false);
      return;
    }
    const controller = new AbortController();
    const timeout = window.setTimeout(() => {
      setLoading(true);
      setError(null);
      apiGet<SearchResponse>('/api/search/suggest?q=' + encodeURIComponent(query), { signal: controller.signal })
        .then((response) => {
          if (!controller.signal.aborted) {
            setData(response);
            setActive(-1);
          }
        })
        .catch((e: Error) => {
          if (!controller.signal.aborted) setError(e.message || 'Не удалось выполнить поиск');
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, 250);
    return () => {
      window.clearTimeout(timeout);
      controller.abort();
    };
  }, [query, open]);

  function openAll() {
    if (query.length >= 2) navigate('/search?q=' + encodeURIComponent(query));
    setOpen(false);
  }

  function choose(hit: SearchHit) {
    setOpen(false);
    openSearchHit(hit, navigate);
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      setOpen(false);
      setActive(-1);
      return;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (query.length < 2) return;
      event.preventDefault();
      const count = hits.length + 1; // Последний «вариант» — полная выдача.
      setOpen(true);
      setActive((current) => {
        if (event.key === 'ArrowDown') return current >= count - 1 ? 0 : current + 1;
        return current <= 0 ? count - 1 : current - 1;
      });
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      if (active >= 0 && active < hits.length) choose(hits[active]);
      else openAll();
    }
  }

  const showMenu = open && query.length >= 2;
  return (
    <header className="topbar">
      <div className="tb-title">
        {kicker && <div className="tb-kicker">{kicker}</div>}
        <div className="tb-h1">{title}</div>
      </div>
      <div className="tb-search" onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false);
      }}>
        <span className="si"><Icon.search size={20} /></span>
        <input
          type="search"
          role="combobox"
          aria-label="Поиск по BarkCloud"
          aria-autocomplete="list"
          aria-expanded={showMenu}
          aria-controls={LISTBOX_ID}
          aria-activedescendant={active >= 0 ? `${LISTBOX_ID}-${active}` : undefined}
          placeholder="Поиск по BarkCloud"
          value={q}
          onFocus={() => setOpen(true)}
          onChange={(e) => {
            setQ(e.target.value);
            setOpen(true);
            setActive(-1);
          }}
          onKeyDown={onKeyDown}
        />
        <span className="kbd">⌘ K</span>
        {showMenu && (
          <div className="search-suggest" id={LISTBOX_ID} role="listbox" aria-label="Результаты поиска">
            {loading && <div className="search-suggest-state">Ищем…</div>}
            {!loading && error && <div className="search-suggest-state error">{error}</div>}
            {!loading && !error && hits.length === 0 && <div className="search-suggest-state">Ничего не найдено</div>}
            {!loading && displaySections.map((section) => (
              <div className="search-suggest-group" key={section.key}>
                <div className="search-suggest-label">{SECTION_LABEL[section.key]}</div>
                {section.unavailable ? (
                  <div className="search-suggest-unavailable">Временно недоступно</div>
                ) : section.items.map((hit) => {
                  const index = hits.indexOf(hit);
                  return (
                    <button
                      type="button"
                      id={`${LISTBOX_ID}-${index}`}
                      role="option"
                      aria-selected={active === index}
                      className={'search-suggest-hit' + (active === index ? ' active' : '')}
                      key={`${hit.kind}:${hit.id}`}
                      onMouseDown={(e) => e.preventDefault()}
                      onClick={() => choose(hit)}
                    >
                      <HitGlyph hit={hit} />
                      <span className="search-suggest-copy"><b>{hit.title}</b>{hit.subtitle && <small>{hit.subtitle}</small>}</span>
                      {hit.favorite && <span className="search-favorite" aria-label="Избранное">★</span>}
                    </button>
                  );
                })}
              </div>
            ))}
            <button
              type="button"
              id={`${LISTBOX_ID}-${hits.length}`}
              role="option"
              aria-selected={active === hits.length}
              className={'search-all' + (active === hits.length ? ' active' : '')}
              onMouseDown={(e) => e.preventDefault()}
              onClick={openAll}
            >Все результаты по «{query}»</button>
          </div>
        )}
      </div>
      <div className="tb-actions">
        {actions}
        <UploadIndicator />
        <button className="icon-btn" title="Уведомления">
          <Icon.bell size={22} />
          <span className="dot-badge" />
        </button>
      </div>
    </header>
  );
}

function HitGlyph({ hit }: { hit: SearchHit }) {
  const Glyph = Icon[searchHitIconName(hit)];
  return <span className="search-hit-glyph"><Glyph size={18} /></span>;
}
