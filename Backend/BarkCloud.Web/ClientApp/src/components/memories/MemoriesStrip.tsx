import React from 'react';
import { Icon } from '../Icon';
import { MediaThumb } from '../media/MediaThumb';
import { Lightbox } from '../media/Lightbox';
import { apiGet } from '../../lib/api';
import { plural } from '../../lib/format';
import type { MediaActionsApi } from '../../hooks/useMediaActions';
import type { MemoryGroup } from '../../lib/types';

function yearsAgoLabel(n: number): string {
  if (n <= 0) return 'В этом году';
  return `${n} ${plural(n, 'год', 'года', 'лет')} назад`;
}

function MemoryCard({ group, onOpen }: { group: MemoryGroup; onOpen: () => void }) {
  const cover = group.items[0];
  return (
    <div className="mem-card" onClick={onOpen} title={`${group.totalCount} ${plural(group.totalCount, 'снимок', 'снимка', 'снимков')}`}>
      <MediaThumb media={cover} sizes="220px" className="mem-thumb" />
      <div className="mem-meta">
        <span className="mem-when">{yearsAgoLabel(group.yearsAgo)}</span>
        <span className="mem-year">{group.year}</span>
      </div>
      <span className="mem-count">{group.totalCount}</span>
    </div>
  );
}

/** Лента «Воспоминания — В этот день»: фото/видео за сегодняшнюю дату прошлых лет.
 *  Скрывается, если воспоминаний нет. Клик по году открывает Lightbox с его снимками.
 *  refreshKey: инкремент — перезагрузить группы (например, после удаления фото в галерее).
 *  actions: панель действий в Lightbox (useMediaActions().api). */
export function MemoriesStrip({ refreshKey = 0, actions }: { refreshKey?: number; actions?: MediaActionsApi }) {
  const [groups, setGroups] = React.useState<MemoryGroup[] | null>(null);
  const [open, setOpen] = React.useState<MemoryGroup | null>(null);

  React.useEffect(() => {
    apiGet<{ groups: MemoryGroup[] }>('/api/cloud/memories')
      .then((d) => setGroups(d.groups || []))
      .catch(() => setGroups([]));
  }, [refreshKey]);

  if (!groups || groups.length === 0) return null;

  return (
    <div className="memories">
      <div className="mem-head">
        <Icon.clock size={18} />
        <h3>В этот день</h3>
      </div>
      <div className="mem-strip">
        {groups.map((g) => (
          <MemoryCard key={g.year} group={g} onOpen={() => setOpen(g)} />
        ))}
      </div>
      {open && <Lightbox items={open.items} index={0} actions={actions} onClose={() => setOpen(null)} />}
    </div>
  );
}
