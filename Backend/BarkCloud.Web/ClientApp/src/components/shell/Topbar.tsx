import React from 'react';
import { useNavigate } from 'react-router-dom';
import { Icon } from '../Icon';
import { UploadIndicator } from '../upload/UploadIndicator';
import type { PageHeader } from '../../hooks/usePageHeader';

export function Topbar({ kicker, title, actions, search = true }: PageHeader) {
  const navigate = useNavigate();
  const [q, setQ] = React.useState('');

  function submit(e: React.FormEvent) {
    e.preventDefault();
    const query = q.trim();
    if (query.length > 0) navigate(`/files?q=${encodeURIComponent(query)}`);
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
            placeholder="Найти файлы по имени…"
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
