import React from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { Icon } from '../Icon';
import { UploadIndicator } from '../upload/UploadIndicator';
import type { PageHeader } from '../../hooks/usePageHeader';

export function Topbar({ kicker, title, actions, search = true }: PageHeader) {
  const navigate = useNavigate();
  const location = useLocation();
  const [q, setQ] = React.useState('');

  // На Фото/Видео ищем в текущей вкладке (медиа-сетка), иначе — в «Файлах».
  const isMediaTab = location.pathname.startsWith('/photos') || location.pathname.startsWith('/videos');

  // Синхронизация поля с текущим ?q= (например, после «Очистить» на странице результатов).
  React.useEffect(() => {
    setQ(new URLSearchParams(location.search).get('q') || '');
  }, [location.pathname, location.search]);

  function submit(e: React.FormEvent) {
    e.preventDefault();
    const query = q.trim();
    if (query.length === 0) return;
    const target = isMediaTab ? location.pathname : '/files';
    navigate(`${target}?q=${encodeURIComponent(query)}`);
  }

  return (
    <header className="topbar">
      <div className="tb-title">
        {kicker && <div className="tb-kicker">{kicker}</div>}
        <div className="tb-h1">{title}</div>
      </div>
      {search && (
        <form className="tb-search" onSubmit={submit}>
          <span className="si">
            <Icon.search size={20} />
          </span>
          <input
            type="text"
            placeholder={isMediaTab ? 'Найти фото и видео по имени…' : 'Найти файлы по имени…'}
            value={q}
            onChange={(e) => setQ(e.target.value)}
          />
          <span className="kbd">⏎</span>
        </form>
      )}
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
