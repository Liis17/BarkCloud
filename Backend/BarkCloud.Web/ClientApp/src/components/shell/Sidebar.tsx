import { NavLink, Link } from 'react-router-dom';
import { Icon } from '../Icon';
import { StorageStatsStatus } from '../ui/StorageStatsStatus';
import { useShell } from '../../hooks/useShell';
import type { SidebarStorage } from '../../lib/types';

interface NavItem {
  key: string;
  to: string;
  label: string;
  icon: string;
}

const NAV_PRIMARY: NavItem[] = [
  { key: 'photos', to: '/photos', label: 'Фото', icon: 'photo' },
  { key: 'videos', to: '/videos', label: 'Видео', icon: 'video' },
  { key: 'music', to: '/music', label: 'Музыка', icon: 'music' },
  { key: 'albums', to: '/albums', label: 'Альбомы', icon: 'grid' },
  { key: 'files', to: '/files', label: 'Файлы', icon: 'folder' },
  { key: 'torrents', to: '/torrents', label: 'Торренты', icon: 'torrent' },
];
const NAV_SEARCH: NavItem = { key: 'search', to: '/search', label: 'Поиск', icon: 'search' };
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
    <NavLink to={item.to} aria-label={item.label} title={item.label} className={({ isActive }) => 'sb-item' + (isActive ? ' active' : '')}>
      <span className="ico">
        <IconC size={22} />
      </span>
      <span>{item.label}</span>
    </NavLink>
  );
}

export function Sidebar({ profileError = false, storageError = false, statistics }: { profileError?: boolean; storageError?: boolean; statistics?: SidebarStorage | null }) {
  const shell = useShell();
  const user = shell?.user;
  const storage = statistics === undefined ? shell?.storage : statistics;
  const diskAvailable = !!storage && storage.state !== 'loading' && !(storage.state === 'error' && !storage.updatedAt);
  const diskState = storageError ? 'error' : storage?.state ?? (storage ? 'ready' : 'loading');
  const s3State = storageError ? 'error' : storage?.allS3State ?? (storage ? storage.allS3StatsAvailable ? 'ready' : 'error' : 'loading');
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
          <div className="sb-items"><NavRow item={NAV_SEARCH} /></div>
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
        <div className="sb-storage-row">
          <div className="sb-storage-head">
            <span>Хранилище</span>
            <span className="used">
              {diskAvailable ? `${storage!.usedLabel} / ${storage!.totalLabel}` : storageError ? 'Ошибка загрузки' : diskState === 'error' ? 'Ошибка подсчёта' : 'Подсчитываем…'}
            </span>
          </div>
          <div className={"bar" + (diskState === 'loading' ? " pending" : "")}>
            <div style={{ display: 'flex', height: '100%', width: '100%' }}>
              <div style={{ width: (diskAvailable ? storage!.otherPct : 0) + '%', background: 'var(--md-on-surface-variant)' }} />
              <div style={{ width: (diskAvailable ? storage!.s3Pct : 0) + '%', background: 'var(--md-primary)' }} />
            </div>
          </div>
          {diskAvailable && <div className="sb-storage-foot"><span>{storage!.percent}% использовано</span></div>}
          {(storageError || storage && storage.state !== 'loading') && <StorageStatsStatus state={storageError ? 'error' : storage?.state} updatedAt={storage?.updatedAt} />}
        </div>
        <div className="sb-storage-row">
          <div className="sb-storage-head">
            <span>S3-бакеты</span>
            <span className="used">
              {!storage?.allS3StatsAvailable ? s3State === 'loading' ? 'Подсчитываем…' : s3State === 'not_configured' ? 'не настроен' : 'недоступно' : storage.allS3HasFiniteQuota
                ? `${storage.allS3UsedLabel} / ${storage.allS3QuotaLabel}`
                : `${storage.allS3UsedLabel} · безлимит`}
            </span>
          </div>
          <div className={"bar" + (s3State === "loading" ? " pending" : "")} role="progressbar" aria-label="Использование S3-хранилища"
            aria-valuemin={0} aria-valuemax={100}
            aria-valuenow={!storage?.allS3StatsAvailable ? undefined : storage.allS3HasFiniteQuota
              ? storage.allS3Percent : 100}>
            <div className="bar-fill" style={{
              width: (!storage?.allS3StatsAvailable ? 0 : storage.allS3HasFiniteQuota
                ? storage.allS3Percent : 100) + '%',
              background: 'var(--md-primary)',
            }} />
          </div>
          {storage?.allS3StatsAvailable && <div className="sb-storage-foot">
            <span>{storage.allS3HasFiniteQuota ? `${storage.allS3Percent}% использовано` : 'безлимит'}</span>
          </div>}
          {s3State !== 'loading' && <StorageStatsStatus state={s3State} updatedAt={storage?.allS3UpdatedAt} />}
        </div>
      </div>

      <Link className="sb-user" to="/settings" aria-label={user?.displayName ? `Аккаунт ${user.displayName}` : 'Аккаунт'} title="Аккаунт">
        {user?.avatarUrl ? (
          <img className="avatar" src={user.avatarUrl} alt="" />
        ) : (
          <div className="avatar">{user?.initials || <Icon.user size={20} />}</div>
        )}
        <div className="who">
          <div className="uname">{user?.displayName || (profileError ? 'Профиль недоступен' : 'Загрузка профиля…')}</div>
          <div className="uhost">
            {[user?.role, shell?.server.host].filter(Boolean).join(' · ')}
          </div>
        </div>
        <span className="chev">
          <Icon.chev size={18} />
        </span>
      </Link>
    </aside>
  );
}
