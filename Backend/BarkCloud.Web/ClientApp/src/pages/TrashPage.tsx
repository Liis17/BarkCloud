import React from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { SelectionBar } from '../components/ui/SelectionBar';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { useSelection } from '../hooks/useSelection';
import { apiGet, apiPost, type BatchSummary } from '../lib/api';
import { plural } from '../lib/format';
import type { Page, TrashItem } from '../lib/types';

const ruDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' });
function fmtDate(iso: string | null): string {
  return iso ? ruDate.format(new Date(iso)) : '—';
}
function kindLabel(k: string | undefined): string {
  return k === 'photo' ? 'фото' : k === 'video' ? 'видео' : k === 'audio' ? 'аудио' : k === 'document' ? 'документ' : 'файл';
}

/* Сколько осталось до окончательного удаления */
function purgeLeft(iso: string | null): string {
  if (!iso) return '—';
  const ms = new Date(iso).getTime() - new Date().getTime();
  if (ms <= 0) return 'скоро';
  const days = Math.floor(ms / 86400000);
  if (days >= 1) return `через ${days} ${plural(days, 'день', 'дня', 'дней')}`;
  const hours = Math.max(1, Math.floor(ms / 3600000));
  return `через ${hours} ${plural(hours, 'час', 'часа', 'часов')}`;
}

function TrashRow({ item, bulkChecked, onBulkToggle, onRestore, onPurge }: {
  item: TrashItem;
  bulkChecked: boolean;
  onBulkToggle: (i: TrashItem) => void;
  onRestore: (i: TrashItem) => void;
  onPurge: (i: TrashItem) => void;
}) {
  const m = item.media || ({} as NonNullable<TrashItem['media']>);
  return (
    <tr className={bulkChecked ? 'checked' : ''}>
      <td className="selcell">
        <input type="checkbox" checked={bulkChecked} onChange={() => onBulkToggle(item)} />
      </td>
      <td className="name">
        <div className={'file-icon ' + (m.iconKind || 'doc')}>{m.ext || 'FILE'}</div>
        <div className="file-name-col">
          <div className="fn">{item.name}</div>
          <div className="meta">{kindLabel(m.kind)}</div>
        </div>
      </td>
      <td className="size">{m.sizeLabel || '—'}</td>
      <td className="when">{fmtDate(item.deletedAt)}</td>
      <td className="when">
        {purgeLeft(item.purgeAt)}
        <span className="left">{fmtDate(item.purgeAt)}</span>
      </td>
      <td>
        <span className="row-actions">
          <button title="Восстановить" onClick={() => onRestore(item)}>
            <Icon.refresh size={18} />
          </button>
          <button className="danger" title="Удалить навсегда" onClick={() => onPurge(item)}>
            <Icon.trash size={18} />
          </button>
        </span>
      </td>
    </tr>
  );
}

export function TrashPage() {
  const [items, setItems] = React.useState<TrashItem[] | null>(null);
  const [bulkPurgeConfirm, setBulkPurgeConfirm] = React.useState(false);
  const [toastNode, toast] = useToast();
  const fsel = useSelection();

  const load = React.useCallback(() => {
    setItems(null);
    fsel.clear();
    apiGet<Page<TrashItem>>('/api/cloud/trash')
      .then((d) => setItems(d.items || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setItems([]);
      });
  }, [toast, fsel.clear]);
  React.useEffect(load, [load]);

  async function restore(item: TrashItem) {
    try {
      await apiPost('/api/cloud/trash/restore', { entryId: item.entryId });
      load();
      toast('Восстановлено');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function purge(item: TrashItem) {
    if (!window.confirm(`Удалить «${item.name}» навсегда? Это действие необратимо.`)) return;
    try {
      await apiPost('/api/cloud/trash/purge', { entryId: item.entryId });
      load();
      toast('Удалено навсегда');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function bulkRestore() {
    const chosen = (items || []).filter((i) => fsel.has(i.entryId));
    try {
      const result = await apiPost<BatchSummary>('/api/cloud/trash/restore-batch', { entryIds: chosen.map((it) => it.entryId) });
      fsel.clear();
      if (result.succeeded) {
        toast(result.failed ? `Восстановлено: ${result.succeeded}, не удалось: ${result.failed}` : `Восстановлено: ${result.succeeded}`);
        load();
      } else if (result.failed) {
        toast('Не удалось восстановить выбранные файлы', 'err');
      }
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function bulkPurge() {
    const chosen = (items || []).filter((i) => fsel.has(i.entryId));
    try {
      const result = await apiPost<BatchSummary>('/api/cloud/trash/purge-batch', { entryIds: chosen.map((it) => it.entryId) });
      setBulkPurgeConfirm(false);
      fsel.clear();
      if (result.succeeded) {
        toast(result.failed ? `Удалено навсегда: ${result.succeeded}, не удалось: ${result.failed}` : `Удалено навсегда: ${result.succeeded}`);
        load();
      } else if (result.failed) {
        toast('Не удалось удалить выбранные файлы навсегда', 'err');
      }
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }
  async function empty() {
    if (!items || !items.length) return;
    if (!window.confirm('Очистить корзину? Все файлы будут удалены навсегда.')) return;
    try {
      await apiPost('/api/cloud/trash/empty', {});
      load();
      toast('Корзина очищена');
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  const isEmpty = items && !items.length;
  const allChecked = !!items && items.length > 0 && items.every((i) => fsel.has(i.entryId));

  usePageHeader(
    () => ({
      title: 'Корзина',
      kicker: (
        <>
          <span>Прочее</span>
          <span className="sep">/</span>
          <span className="cur">Корзина</span>
        </>
      ),
      contentClass: 'content-flush',
      actions: (
        <button className="btn outlined" onClick={empty} disabled={!items || !items.length}>
          <Icon.trash size={16} /> Очистить корзину
        </button>
      ),
    }),
    [items],
  );

  return (
    <>
      {toastNode}
      <SelectionBar
        count={fsel.count}
        onClear={fsel.clear}
        actions={[
          { label: 'Восстановить', icon: 'refresh', onClick: bulkRestore },
          { label: 'Удалить навсегда', icon: 'trash', danger: true, onClick: () => setBulkPurgeConfirm(true) },
        ]}
      />
      <div className="trash-shell">
        <div className="trash-main">
          <div className="trash-bar">
            <span>Удалённые файлы хранятся 14 дней, затем удаляются навсегда.</span>
          </div>
          <div className="trash-list">
            {items === null ? (
              <Loading />
            ) : isEmpty ? (
              <EmptyState icon="trash" title="Корзина пуста" hint="Удалённые файлы появятся здесь и будут храниться 14 дней." />
            ) : (
              <table className="ftable">
                <thead>
                  <tr>
                    <th style={{ width: 40 }} className="selcell">
                      <input
                        type="checkbox"
                        checked={allChecked}
                        onChange={(e) => fsel.setAll(items!.map((i) => i.entryId), e.target.checked)}
                      />
                    </th>
                    <th>Имя</th>
                    <th style={{ width: 120 }}>Размер</th>
                    <th style={{ width: 150 }}>Удалён</th>
                    <th style={{ width: 180 }}>Будет удалён</th>
                    <th style={{ width: 110 }}></th>
                  </tr>
                </thead>
                <tbody>
                  {items!.map((it) => (
                    <TrashRow
                      key={it.entryId}
                      item={it}
                      bulkChecked={fsel.has(it.entryId)}
                      onBulkToggle={(t) => fsel.toggle(t.entryId)}
                      onRestore={restore}
                      onPurge={purge}
                    />
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </div>
      </div>
      {bulkPurgeConfirm && (
        <ConfirmModal
          title="Удалить навсегда?"
          danger
          confirmLabel="Удалить навсегда"
          message={`Выбранные файлы (${fsel.count}) будут удалены без возможности восстановления.`}
          onClose={() => setBulkPurgeConfirm(false)}
          onConfirm={bulkPurge}
        />
      )}
    </>
  );
}
