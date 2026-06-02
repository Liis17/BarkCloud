import React from 'react';
import { Modal } from './Modal';
import { Icon } from '../Icon';
import { Loading } from './EmptyState';
import { apiGet } from '../../lib/api';
import type { DirInfo, Listing } from '../../lib/types';

/**
 * Выбор папки назначения: навигация по дереву облака (только папки) с хлебными крошками.
 * onPick получает id выбранной папки ('' = корень). currentDir подсвечивается как «уже здесь».
 */
export function MoveToFolderModal({
  count,
  currentDir,
  onPick,
  onClose,
}: {
  count: number;
  currentDir: string;
  onPick: (dirId: string) => void;
  onClose: () => void;
}) {
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>([]);
  const [dirs, setDirs] = React.useState<DirInfo[] | null>(null);

  const here = stack.length ? stack[stack.length - 1].id : '';

  React.useEffect(() => {
    let alive = true;
    setDirs(null);
    apiGet<Listing>('/api/cloud/list?dir=' + encodeURIComponent(here))
      .then((d) => alive && setDirs(d.dirs || []))
      .catch(() => alive && setDirs([]));
    return () => {
      alive = false;
    };
  }, [here]);

  const gotoIndex = (i: number) => setStack((s) => s.slice(0, i + 1));
  const isCurrent = here === currentDir;

  return (
    <Modal
      title={`Переместить (${count})`}
      onClose={onClose}
      actions={
        <>
          <button className="btn text" onClick={onClose}>
            Отмена
          </button>
          <button className="btn primary" disabled={isCurrent} onClick={() => onPick(here)}>
            {isCurrent ? 'Файлы уже здесь' : 'Переместить сюда'}
          </button>
        </>
      }
    >
      <div className="breadcrumb" style={{ marginBottom: 12 }}>
        <a onClick={() => gotoIndex(-1)} className={stack.length ? '' : 'cur'} style={{ cursor: 'pointer' }}>
          <Icon.folder size={18} />
        </a>
        {stack.map((s, i) => (
          <React.Fragment key={s.id}>
            <span className="sep">/</span>
            {i === stack.length - 1 ? (
              <span className="cur">{s.name}</span>
            ) : (
              <a onClick={() => gotoIndex(i)} style={{ cursor: 'pointer' }}>
                {s.name}
              </a>
            )}
          </React.Fragment>
        ))}
      </div>

      {dirs === null ? (
        <Loading />
      ) : dirs.length === 0 ? (
        <p style={{ color: 'var(--md-on-surface-variant)', fontSize: 13, margin: '8px 2px' }}>Вложенных папок нет.</p>
      ) : (
        <ul style={{ listStyle: 'none', padding: 0, margin: 0, maxHeight: 320, overflowY: 'auto' }}>
          {dirs.map((d) => (
            <li key={d.id}>
              <button
                className="album-pick-row"
                style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 10 }}
                onClick={() => setStack((s) => [...s, { id: d.id, name: d.name }])}
              >
                <Icon.folder size={18} />
                <span style={{ flex: 1, textAlign: 'left' }}>{d.name}</span>
                <Icon.chev size={16} />
              </button>
            </li>
          ))}
        </ul>
      )}
    </Modal>
  );
}
