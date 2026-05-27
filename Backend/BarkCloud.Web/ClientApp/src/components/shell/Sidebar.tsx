import { NavLink, Link } from 'react-router-dom';
import { Icon } from '../Icon';
import { useShell } from '../../hooks/useShell';

interface NavItem {
  key: string;
  to: string;
  label: string;
  icon: string;
}

const NAV_PRIMARY: NavItem[] = [
  { key: 'photos', to: '/photos', label: 'Фото', icon: 'photo' },
  { key: 'videos', to: '/videos', label: 'Видео', icon: 'video' },
  { key: 'files', to: '/files', label: 'Файлы', icon: 'folder' },
];
const NAV_SHARE: NavItem[] = [
  { key: 'shared', to: '/shared', label: 'Общие', icon: 'share' },
  { key: 'favorites', to: '/favorites', label: 'Избранное', icon: 'star' },
];
const NAV_OTHER: NavItem[] = [
  { key: 'trash', to: '/trash', label: 'Корзина', icon: 'trash' },
  { key: 'settings', to: '/settings', label: 'Настройки', icon: 'settings' },
];

function NavRow({ item }: { item: NavItem }) {
  const IconC = Icon[item.icon];
  return (
    <NavLink to={item.to} className={({ isActive }) => 'sb-item' + (isActive ? ' active' : '')}>
      <span className="ico">
        <IconC size={22} />
      </span>
      <span>{item.label}</span>
    </NavLink>
  );
}

export function Sidebar() {
  const shell = useShell();
  const user = shell?.user;
  const storage = shell?.storage;
  const app = shell?.app;

  return (
    <aside className="sidebar">
      <div className="sb-brand">
        <div className="mark">
          <Icon.cloud size={22} />
        </div>
        <div>
          <div className="name">BarkCloud</div>
          <div className="v">
            {app?.version} · {app?.edition}
          </div>
        </div>
      </div>

      <nav className="sb-nav">
        <div>
          <div className="sb-section-label">Библиотека</div>
          <div className="sb-items">{NAV_PRIMARY.map((i) => <NavRow key={i.key} item={i} />)}</div>
        </div>
        <div>
          <div className="sb-section-label">Совместное</div>
          <div className="sb-items">{NAV_SHARE.map((i) => <NavRow key={i.key} item={i} />)}</div>
        </div>
        <div>
          <div className="sb-section-label">Прочее</div>
          <div className="sb-items">{NAV_OTHER.map((i) => <NavRow key={i.key} item={i} />)}</div>
        </div>
      </nav>

      <div className="sb-storage">
        <div className="sb-storage-head">
          <span>Хранилище</span>
          <span className="used">
            {storage?.usedLabel} / {storage?.totalLabel}
          </span>
        </div>
        <div className="bar">
          <div className="bar-fill" style={{ width: (storage?.percent ?? 0) + '%' }} />
        </div>
        <div className="sb-storage-foot">
          <span>{storage?.percent ?? 0}% использовано</span>
          <Link to="/settings">Расширить</Link>
        </div>
      </div>

      <Link className="sb-user" to="/settings">
        {user?.avatarUrl ? (
          <img className="avatar" src={user.avatarUrl} alt="" />
        ) : (
          <div className="avatar">{user?.initials || '?'}</div>
        )}
        <div className="who">
          <div className="uname">{user?.displayName || 'Пользователь'}</div>
          <div className="uhost">
            {user?.role} · {shell?.server.host}
          </div>
        </div>
        <span className="chev">
          <Icon.chev size={18} />
        </span>
      </Link>
    </aside>
  );
}
