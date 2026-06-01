import React from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
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

function ownerName(o: SharedOwner): string {
  return [o.firstName, o.lastName].filter(Boolean).join(' ') || o.username || `id ${o.id}`;
}

function LinkCard({ link, onCopy, onRevoke }: { link: ShareLink; onCopy: (l: ShareLink) => void; onRevoke: (l: ShareLink) => void }) {
  return (
    <div className="link-card">
      <div className="link-icon">
        <Icon.link size={22} />
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

export function SharedPage() {
  const [tab, setTab] = React.useState<'public' | 'mine'>('public');
  const [links, setLinks] = React.useState<ShareLink[] | null>(null);
  const [shared, setShared] = React.useState<SharedItem[] | null>(null);
  const [toastNode, toast] = useToast();

  const loadPublic = React.useCallback(() => {
    setLinks(null);
    apiGet<Page<ShareLink>>('/api/shares')
      .then((d) => setLinks(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setLinks([]);
      });
  }, [toast]);
  const loadShared = React.useCallback(() => {
    setShared(null);
    apiGet<{ items: SharedItem[] }>('/api/shared/with-me')
      .then((d) => setShared(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setShared([]);
      });
  }, [toast]);

  React.useEffect(() => {
    if (tab === 'public') loadPublic();
    else loadShared();
  }, [tab, loadPublic, loadShared]);

  async function copy(link: ShareLink) {
    try {
      await navigator.clipboard.writeText(link.url);
      toast('Ссылка скопирована');
    } catch {
      toast('Не удалось скопировать', 'err');
    }
  }
  async function revoke(link: ShareLink) {
    if (!window.confirm(`Отозвать ссылку на «${link.name || 'файл'}»? Ссылка перестанет работать.`)) return;
    try {
      await apiPost('/api/shares/revoke', { shareId: link.id });
      setLinks((prev) => (prev ? prev.filter((l) => l.id !== link.id) : prev));
      toast('Ссылка отозвана');
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

  usePageHeader(
    () => ({
      title: 'Общий доступ',
      kicker: (
        <>
          <span>Совместное</span>
          <span className="sep">/</span>
          <span className="cur">Общие</span>
        </>
      ),
    }),
    [],
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
        <button className={'sh-tab' + (tab === 'mine' ? ' on' : '')} onClick={() => setTab('mine')}>
          <Icon.share size={18} />
          Мне доступны
          {shared && shared.length > 0 && <span className="count">{shared.length}</span>}
        </button>
      </div>

      {tab === 'public' ? (
        links === null ? (
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
        )
      ) : shared === null ? (
        <Loading />
      ) : shared.length === 0 ? (
        <EmptyState icon="share" title="Пока ничего не расшарено вам" hint="Файлы, которыми с вами поделились другие пользователи, появятся здесь." />
      ) : (
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
  );
}
