import React from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { SharedFolderModal } from '../components/ui/SharedFolderModal';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet, apiPost } from '../lib/api';
import { plural } from '../lib/format';
import type { Page, ShareLink } from '../lib/types';

const ruDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
function fmtDate(iso: string | null): string {
  return iso ? ruDate.format(new Date(iso)) : '—';
}

interface SharedOwner {
  id: number;
  username: string;
  firstName: string;
  lastName: string;
  avatar: string;
}
interface SharedFile {
  id: string;
  name: string;
  previews?: { url: string }[];
}
interface SharedItem {
  grantId: string;
  file: SharedFile;
  sharedAt: string | null;
  owner: SharedOwner;
}

interface ISharedItem {
  grantId: string;
  file: SharedFile;
  sharedAt: string | null;
  recipient: SharedOwner;
}
interface ISharedGroup {
  file: SharedFile;
  recipients: { grantId: string; user: SharedOwner; sharedAt: string | null }[];
}

function ownerName(o: SharedOwner): string {
  return [o.firstName, o.lastName].filter(Boolean).join(' ') || o.username || `id ${o.id}`;
}

interface IFolderShareItem {
  grantId: string;
  directoryId: string;
  name: string;
  sharedAt: string | null;
  recipient: SharedOwner;
}
interface IFolderGroup {
  directoryId: string;
  name: string;
  recipients: { grantId: string; user: SharedOwner }[];
}
interface SharedFolderItem {
  grantId: string;
  directoryId: string;
  name: string;
  sharedAt: string | null;
  owner: SharedOwner;
}

/** Группировка плоского списка исходящих грантов по файлу (порядок файлов — по первому появлению). */
function groupIShared(items: ISharedItem[]): ISharedGroup[] {
  const byFile = new Map<string, ISharedGroup>();
  for (const it of items) {
    let g = byFile.get(it.file.id);
    if (!g) {
      g = { file: it.file, recipients: [] };
      byFile.set(it.file.id, g);
    }
    g.recipients.push({ grantId: it.grantId, user: it.recipient, sharedAt: it.sharedAt });
  }
  return Array.from(byFile.values());
}

/** Группировка исходящих грантов на папки по папке. */
function groupIFolders(items: IFolderShareItem[]): IFolderGroup[] {
  const byDir = new Map<string, IFolderGroup>();
  for (const it of items) {
    let g = byDir.get(it.directoryId);
    if (!g) {
      g = { directoryId: it.directoryId, name: it.name, recipients: [] };
      byDir.set(it.directoryId, g);
    }
    g.recipients.push({ grantId: it.grantId, user: it.recipient });
  }
  return Array.from(byDir.values());
}

function LinkCard({ link, onCopy, onRevoke }: { link: ShareLink; onCopy: (l: ShareLink) => void; onRevoke: (l: ShareLink) => void }) {
  return (
    <div className="link-card">
      <div className="link-icon" style={link.previewUrl ? { overflow: 'hidden' } : undefined}>
        {link.previewUrl ? (
          <img src={link.previewUrl} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
        ) : link.kind === 'folder' ? (
          <Icon.folder size={22} />
        ) : link.kind === 'album' ? (
          <Icon.photo size={22} />
        ) : (
          <Icon.link size={22} />
        )}
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{link.name || 'Без имени'}</div>
        <div className="link-url">
          <Icon.link size={12} />
          <span>{link.url}</span>
        </div>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div className="meta-line">
          <span className="k">Переходов</span> <span style={{ color: 'var(--md-on-surface)', fontWeight: 500 }}>{link.clickCount}</span>
        </div>
        <div className="meta-line">
          <span className="k">Создана</span> <span style={{ color: 'var(--md-on-surface)' }}>{fmtDate(link.createdAt)}</span>
        </div>
      </div>
      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 4 }}>
        <button className="icon-btn" title="Скопировать ссылку" onClick={() => onCopy(link)}>
          <Icon.link size={18} />
        </button>
        <button className="icon-btn" title="Отозвать ссылку" onClick={() => onRevoke(link)}>
          <Icon.trash size={18} />
        </button>
      </div>
    </div>
  );
}

function SharedCard({ item, onDownload }: { item: SharedItem; onDownload: (it: SharedItem) => void }) {
  const preview = item.file.previews && item.file.previews[0];
  return (
    <div className="link-card">
      <div className="link-icon" style={{ overflow: 'hidden' }}>
        {preview ? (
          <img src={preview.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
        ) : (
          <Icon.file size={22} />
        )}
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{item.file.name}</div>
        <div className="meta-line">
          <span className="k">От кого</span> <span style={{ color: 'var(--md-on-surface)' }}>{ownerName(item.owner)}</span>
        </div>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div className="meta-line">
          <span className="k">Когда</span> <span style={{ color: 'var(--md-on-surface)' }}>{fmtDate(item.sharedAt)}</span>
        </div>
      </div>
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <button className="btn text" onClick={() => onDownload(item)}>
          <Icon.download size={16} /> Скачать
        </button>
      </div>
    </div>
  );
}

function ISharedCard({ group, onRevoke }: { group: ISharedGroup; onRevoke: (grantId: string, file: SharedFile, user: SharedOwner) => void }) {
  const preview = group.file.previews && group.file.previews[0];
  return (
    <div className="link-card">
      <div className="link-icon" style={{ overflow: 'hidden' }}>
        {preview ? (
          <img src={preview.url} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
        ) : (
          <Icon.file size={22} />
        )}
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{group.file.name}</div>
        <div className="meta-line">
          <span className="k">Доступ у</span>{' '}
          <span style={{ color: 'var(--md-on-surface)' }}>
            {group.recipients.length} {plural(group.recipients.length, 'пользователя', 'пользователей', 'пользователей')}
          </span>
        </div>
      </div>
      <div style={{ gridColumn: '2 / -1', display: 'flex', flexWrap: 'wrap', gap: 6 }}>
        {group.recipients.map((r) => (
          <span
            key={r.grantId}
            style={{
              display: 'inline-flex',
              alignItems: 'center',
              gap: 6,
              padding: '4px 8px 4px 10px',
              borderRadius: 16,
              background: 'var(--md-surface-container-high)',
              fontSize: 13,
            }}
          >
            {ownerName(r.user)}
            <button
              className="icon-btn"
              title="Отозвать доступ"
              style={{ width: 22, height: 22 }}
              onClick={() => onRevoke(r.grantId, group.file, r.user)}
            >
              <Icon.x size={14} />
            </button>
          </span>
        ))}
      </div>
    </div>
  );
}

function IFolderCard({ group, onRevoke }: { group: IFolderGroup; onRevoke: (grantId: string, name: string, user: SharedOwner) => void }) {
  return (
    <div className="link-card">
      <div className="link-icon">
        <Icon.folder size={22} />
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{group.name}</div>
        <div className="meta-line">
          <span className="k">Доступ у</span>{' '}
          <span style={{ color: 'var(--md-on-surface)' }}>
            {group.recipients.length} {plural(group.recipients.length, 'пользователя', 'пользователей', 'пользователей')}
          </span>
        </div>
      </div>
      <div style={{ gridColumn: '2 / -1', display: 'flex', flexWrap: 'wrap', gap: 6 }}>
        {group.recipients.map((r) => (
          <span key={r.grantId} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, padding: '4px 8px 4px 10px', borderRadius: 16, background: 'var(--md-surface-container-high)', fontSize: 13 }}>
            {ownerName(r.user)}
            <button className="icon-btn" title="Отозвать доступ" style={{ width: 22, height: 22 }} onClick={() => onRevoke(r.grantId, group.name, r.user)}>
              <Icon.x size={14} />
            </button>
          </span>
        ))}
      </div>
    </div>
  );
}

function SharedFolderCard({ item, onOpen }: { item: SharedFolderItem; onOpen: (it: SharedFolderItem) => void }) {
  return (
    <div className="link-card">
      <div className="link-icon">
        <Icon.folder size={22} />
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{item.name}</div>
        <div className="meta-line">
          <span className="k">От кого</span> <span style={{ color: 'var(--md-on-surface)' }}>{ownerName(item.owner)}</span>
        </div>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div className="meta-line">
          <span className="k">Когда</span> <span style={{ color: 'var(--md-on-surface)' }}>{fmtDate(item.sharedAt)}</span>
        </div>
      </div>
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <button className="btn text" onClick={() => onOpen(item)}>
          <Icon.folder size={16} /> Открыть
        </button>
      </div>
    </div>
  );
}

export function SharedPage() {
  const [tab, setTab] = React.useState<'public' | 'ishared' | 'mine'>('public');
  const [links, setLinks] = React.useState<ShareLink[] | null>(null);
  const [iShared, setIShared] = React.useState<ISharedItem[] | null>(null);
  const [iSharedFolders, setISharedFolders] = React.useState<IFolderShareItem[] | null>(null);
  const [shared, setShared] = React.useState<SharedItem[] | null>(null);
  const [sharedFolders, setSharedFolders] = React.useState<SharedFolderItem[] | null>(null);
  const [openFolder, setOpenFolder] = React.useState<{ id: string; name: string } | null>(null);
  const [toastNode, toast] = useToast();

  const loadPublic = React.useCallback(() => {
    setLinks(null);
    Promise.all([
      apiGet<Page<ShareLink>>('/api/shares').then((d) => (d.items || []).map((l) => ({ ...l, kind: 'file' as const }))),
      apiGet<Page<ShareLink>>('/api/folder-shares').then((d) => (d.items || []).map((l) => ({ ...l, kind: 'folder' as const }))),
      apiGet<Page<ShareLink>>('/api/album-shares').then((d) => (d.items || []).map((l) => ({ ...l, kind: 'album' as const }))),
    ])
      .then(([files, folders, albums]) => {
        const all = [...files, ...folders, ...albums].sort((a, b) => (b.createdAt || '').localeCompare(a.createdAt || ''));
        setLinks(all);
      })
      .catch((e) => {
        toast((e as Error).message, 'err');
        setLinks([]);
      });
  }, [toast]);
  const loadIShared = React.useCallback(() => {
    setIShared(null);
    setISharedFolders(null);
    apiGet<{ items: ISharedItem[] }>('/api/shared/i-shared')
      .then((d) => setIShared(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setIShared([]);
      });
    apiGet<{ items: IFolderShareItem[] }>('/api/shared/i-shared-folders')
      .then((d) => setISharedFolders(d.items || []))
      .catch(() => setISharedFolders([]));
  }, [toast]);
  const loadShared = React.useCallback(() => {
    setShared(null);
    setSharedFolders(null);
    apiGet<{ items: SharedItem[] }>('/api/shared/with-me')
      .then((d) => setShared(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setShared([]);
      });
    apiGet<{ items: SharedFolderItem[] }>('/api/shared/folders-with-me')
      .then((d) => setSharedFolders(d.items || []))
      .catch(() => setSharedFolders([]));
  }, [toast]);

  React.useEffect(() => {
    if (tab === 'public') loadPublic();
    else if (tab === 'ishared') loadIShared();
    else loadShared();
  }, [tab, loadPublic, loadIShared, loadShared]);

  const iSharedGroups = React.useMemo(() => (iShared ? groupIShared(iShared) : null), [iShared]);
  const iFolderGroups = React.useMemo(() => (iSharedFolders ? groupIFolders(iSharedFolders) : null), [iSharedFolders]);
  // Счётчик вкладки «Я поделился» = файлы + папки.
  const iSharedCount = (iSharedGroups?.length || 0) + (iFolderGroups?.length || 0);
  const mineCount = (shared?.length || 0) + (sharedFolders?.length || 0);

  async function copy(link: ShareLink) {
    try {
      await navigator.clipboard.writeText(link.url);
      toast('Ссылка скопирована');
    } catch {
      toast('Не удалось скопировать', 'err');
    }
  }
  async function revoke(link: ShareLink) {
    const isFolder = link.kind === 'folder';
    const isAlbum = link.kind === 'album';
    const what = isFolder ? 'папку' : isAlbum ? 'альбом' : 'файл';
    const extra = isFolder ? ' Публичные ссылки на файлы внутри тоже будут сняты.' : '';
    if (!window.confirm(`Отозвать ссылку на «${link.name || what}»? Ссылка перестанет работать.${extra}`)) return;
    try {
      if (isFolder) await apiPost('/api/folder-shares/revoke', { folderShareId: link.id });
      else if (isAlbum) await apiPost('/api/album-shares/revoke', { albumShareId: link.id });
      else await apiPost('/api/shares/revoke', { shareId: link.id });
      setLinks((prev) => (prev ? prev.filter((l) => l.id !== link.id) : prev));
      toast(isFolder ? 'Папка снова приватна' : isAlbum ? 'Альбом снова приватный' : 'Ссылка отозвана');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function revokeGrant(grantId: string, file: SharedFile, user: SharedOwner) {
    if (!window.confirm(`Отозвать доступ ${ownerName(user)} к «${file.name}»?`)) return;
    try {
      await apiPost('/api/shared/revoke-grant', { grantId });
      setIShared((prev) => (prev ? prev.filter((it) => it.grantId !== grantId) : prev));
      toast('Доступ отозван');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function revokeFolderGrant(grantId: string, name: string, user: SharedOwner) {
    if (!window.confirm(`Отозвать доступ ${ownerName(user)} к папке «${name}»?`)) return;
    try {
      await apiPost('/api/shared/revoke-folder-grant', { grantId });
      setISharedFolders((prev) => (prev ? prev.filter((it) => it.grantId !== grantId) : prev));
      toast('Доступ отозван');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function download(item: SharedItem) {
    try {
      const r = await apiPost<{ downloadUrl: string }>('/api/shared/download', { fileId: item.file.id });
      if (r.downloadUrl) window.location.href = r.downloadUrl;
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  const tabTitle = tab === 'ishared' ? 'Я поделился' : tab === 'mine' ? 'Мне доступны' : 'Мои публичные';

  usePageHeader(
    () => ({
      title: 'Общий доступ',
      documentTitle: `Общий доступ: ${tabTitle}`,
      kicker: (
        <>
          <span>Совместное</span>
          <span className="sep">/</span>
          <span className="cur">Общие</span>
        </>
      ),
    }),
    [tabTitle],
  );

  return (
    <>
      {toastNode}

      <div className="sh-tabs">
        <button className={'sh-tab' + (tab === 'public' ? ' on' : '')} onClick={() => setTab('public')}>
          <Icon.link size={18} />
          Мои публичные
          {links && links.length > 0 && <span className="count">{links.length}</span>}
        </button>
        <button className={'sh-tab' + (tab === 'ishared' ? ' on' : '')} onClick={() => setTab('ishared')}>
          <Icon.user size={18} />
          Я поделился
          {iSharedCount > 0 && <span className="count">{iSharedCount}</span>}
        </button>
        <button className={'sh-tab' + (tab === 'mine' ? ' on' : '')} onClick={() => setTab('mine')}>
          <Icon.share size={18} />
          Мне доступны
          {mineCount > 0 && <span className="count">{mineCount}</span>}
        </button>
      </div>

      {tab === 'public' &&
        (links === null ? (
          <Loading />
        ) : links.length === 0 ? (
          <EmptyState
            icon="link"
            title="Пока нет публичных ссылок"
            hint="Создайте ссылку из контекстного меню файла в «Файлах», «Фото» или «Видео» — она появится здесь."
          />
        ) : (
          <>
            <div className="section-head">
              <h2>Активные публичные ссылки</h2>
              <div className="meta">
                {links.length} {plural(links.length, 'ссылка', 'ссылки', 'ссылок')}
              </div>
            </div>
            {links.map((l) => (
              <LinkCard key={l.id} link={l} onCopy={copy} onRevoke={revoke} />
            ))}
          </>
        ))}

      {tab === 'ishared' &&
        (iSharedGroups === null || iFolderGroups === null ? (
          <Loading />
        ) : iSharedCount === 0 ? (
          <EmptyState
            icon="user"
            title="Вы пока ни с кем не делились"
            hint="Откройте контекстное меню файла или папки и выберите «Поделиться с пользователем» — он появится здесь."
          />
        ) : (
          <>
            {iFolderGroups.length > 0 && (
              <>
                <div className="section-head">
                  <h2>Папки, которыми я поделился</h2>
                  <div className="meta">
                    {iFolderGroups.length} {plural(iFolderGroups.length, 'папка', 'папки', 'папок')}
                  </div>
                </div>
                {iFolderGroups.map((g) => (
                  <IFolderCard key={g.directoryId} group={g} onRevoke={revokeFolderGrant} />
                ))}
              </>
            )}
            {iSharedGroups.length > 0 && (
              <>
                <div className="section-head">
                  <h2>Файлы, которыми я поделился</h2>
                  <div className="meta">
                    {iSharedGroups.length} {plural(iSharedGroups.length, 'файл', 'файла', 'файлов')}
                  </div>
                </div>
                {iSharedGroups.map((g) => (
                  <ISharedCard key={g.file.id} group={g} onRevoke={revokeGrant} />
                ))}
              </>
            )}
          </>
        ))}

      {tab === 'mine' &&
        (shared === null || sharedFolders === null ? (
          <Loading />
        ) : mineCount === 0 ? (
          <EmptyState icon="share" title="Пока ничего не расшарено вам" hint="Файлы и папки, которыми с вами поделились другие пользователи, появятся здесь." />
        ) : (
          <>
            {sharedFolders.length > 0 && (
              <>
                <div className="section-head">
                  <h2>Доступные мне папки</h2>
                  <div className="meta">
                    {sharedFolders.length} {plural(sharedFolders.length, 'папка', 'папки', 'папок')}
                  </div>
                </div>
                {sharedFolders.map((it) => (
                  <SharedFolderCard key={it.grantId} item={it} onOpen={(f) => setOpenFolder({ id: f.directoryId, name: f.name })} />
                ))}
              </>
            )}
            {shared.length > 0 && (
              <>
                <div className="section-head">
                  <h2>Доступные мне файлы</h2>
                  <div className="meta">
                    {shared.length} {plural(shared.length, 'файл', 'файла', 'файлов')}
                  </div>
                </div>
                {shared.map((it) => (
                  <SharedCard key={it.grantId} item={it} onDownload={download} />
                ))}
              </>
            )}
          </>
        ))}

      {openFolder && <SharedFolderModal rootDirId={openFolder.id} rootName={openFolder.name} onClose={() => setOpenFolder(null)} />}
    </>
  );
}
