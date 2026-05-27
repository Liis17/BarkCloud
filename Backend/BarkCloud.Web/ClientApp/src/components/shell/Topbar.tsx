import { Icon } from '../Icon';
import type { PageHeader } from '../../hooks/usePageHeader';

export function Topbar({ kicker, title, actions, search = true }: PageHeader) {
  return (
    <header className="topbar">
      <div className="tb-title">
        {kicker && <div className="tb-kicker">{kicker}</div>}
        <div className="tb-h1">{title}</div>
      </div>
      {search && (
        <div className="tb-search">
          <span className="si">
            <Icon.search size={20} />
          </span>
          <input type="text" placeholder="Найти в облаке: файлы, люди, теги…" />
          <span className="kbd">⌘ K</span>
        </div>
      )}
      <div className="tb-actions">
        {actions}
        <button className="icon-btn" title="Уведомления">
          <Icon.bell size={22} />
          <span className="dot-badge" />
        </button>
      </div>
    </header>
  );
}
