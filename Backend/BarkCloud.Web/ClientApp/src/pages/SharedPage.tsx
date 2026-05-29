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

export function SharedPage() {
  const [links, setLinks] = React.useState<ShareLink[] | null>(null);
  const [toastNode, toast] = useToast();

  const load = React.useCallback(() => {
    setLinks(null);
    apiGet<Page<ShareLink>>('/api/shares')
      .then((d) => setLinks(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setLinks([]);
      });
  }, [toast]);
  React.useEffect(load, [load]);

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

  const count = links?.length ?? 0;

  return (
    <>
      {toastNode}

      <div className="sh-tabs">
        <button className="sh-tab on">
          <Icon.link size={18} />
          Мои публичные
          {count > 0 && <span className="count">{count}</span>}
        </button>
      </div>

      {links === null ? (
        <Loading />
      ) : count === 0 ? (
        <EmptyState
          icon="link"
          title="Пока нет публичных ссылок"
          hint="Создайте ссылку из контекстного меню файла в «Файлах», «Фото» или «Видео» — она появится здесь."
        />
      ) : (
        <>
          <div className="section-head">
            <h2>Активные публичные ссылки</h2>
            <div className="meta">{count} {plural(count, 'ссылка', 'ссылки', 'ссылок')}</div>
          </div>
          {links.map((l) => (
            <LinkCard key={l.id} link={l} onCopy={copy} onRevoke={revoke} />
          ))}
        </>
      )}
    </>
  );
}
