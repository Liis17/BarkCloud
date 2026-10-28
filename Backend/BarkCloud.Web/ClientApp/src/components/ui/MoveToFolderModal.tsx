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
  progress,
}: {
  count: number;
  currentDir: string;
  onPick: (dirId: string) => void;
  onClose: () => void;
  /** Идущее массовое перемещение: пока задан, модалку закрыть нельзя. */
  progress?: { done: number; total: number };
}) {
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>([]);
  const [dirs, setDirs] = React.useState<DirInfo[] | null>(null);

  const here = stack.length ? stack[stack.length - 1].id : '';

  // Каждое открытие и каждый шаг вглубь — отдельная запись истории; «назад» снимает её и шагает вверх по дереву,
  // на корне закрывает модалку. Иначе «назад» уходит к предыдущему списку файлов под модалкой.
  const depthRef = React.useRef(0);
  depthRef.current = stack.length;
  const onCloseRef = React.useRef(onClose);
  onCloseRef.current = onClose;
  const busyRef = React.useRef(false);
  busyRef.current = !!progress;
  const pendingRef = React.useRef<(() => void) | null>(null);
  React.useEffect(() => {
    window.history.pushState({ ...window.history.state, moveFolderLayer: true }, '');
    const onPop = () => {
      // Во время перемещения «назад» не должен ни закрыть модалку, ни сдвинуть дерево: возвращаем слой.
      if (busyRef.current) {
        window.history.pushState({ ...window.history.state, moveFolderLayer: true }, '');
        return;
      }
      const pending = pendingRef.current;
      if (pending) {
        pendingRef.current = null;
        pending();
      } else if (depthRef.current > 0) {
        setStack((s) => s.slice(0, -1));
      } else {
        onCloseRef.current();
      }
    };
    window.addEventListener('popstate', onPop);
    return () => window.removeEventListener('popstate', onPop);
  }, []);

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

  // Снимаем свои слои истории и только потом выполняем действие (закрытие или перенос).
  const leave = (after: () => void) => {
    if (pendingRef.current) return;
    pendingRef.current = after;
    window.history.go(-(depthRef.current + 1));
  };
  const enter = (dir: { id: string; name: string }) => {
    window.history.pushState({ ...window.history.state, moveFolderLayer: true }, '');
    setStack((s) => [...s, dir]);
  };
  const gotoIndex = (i: number) => {
    if (progress) return;
    const steps = stack.length - (i + 1);
    if (steps > 0) {
      pendingRef.current = () => {};
      window.history.go(-steps);
    }
    setStack((s) => s.slice(0, i + 1));
  };
  const isCurrent = here === currentDir;
  const percent = progress && progress.total ? Math.round((progress.done / progress.total) * 100) : 0;

  return (
    <Modal
      title={`Переместить (${count})`}
      onClose={progress ? undefined : () => leave(onClose)}
      actions={
        <>
          <button className="btn text" onClick={() => leave(onClose)} disabled={!!progress}>
            Отмена
          </button>
          <button className="btn primary" disabled={isCurrent || !!progress} onClick={() => leave(() => onPick(here))}>
            {progress ? 'Перемещение…' : isCurrent ? 'Файлы уже здесь' : 'Переместить сюда'}
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
        <ul className="move-folder-list" style={{ listStyle: 'none', padding: 0, margin: 0, maxHeight: 320, overflowY: 'auto' }}>
          {dirs.map((d) => (
            <li key={d.id}>
              <button
                className="album-pick-row"
                style={{ width: '100%', display: 'flex', alignItems: 'center', gap: 10 }}
                onClick={() => {
                  if (progress) return;
                  enter({ id: d.id, name: d.name });
                }}
              >
                <Icon.folder size={18} />
                <span style={{ flex: 1, textAlign: 'left' }}>{d.name}</span>
                <Icon.chev size={16} />
              </button>
            </li>
          ))}
        </ul>
      )}

      {progress && (
        <div style={{ marginTop: 12 }}>
          <div
            className="bar"
            role="progressbar"
            aria-label="Перемещение файлов"
            aria-valuemin={0}
            aria-valuemax={progress.total}
            aria-valuenow={progress.done}
          >
            <div className="bar-fill" style={{ width: percent + '%' }} />
          </div>
          <div style={{ fontSize: 12, color: 'var(--md-on-surface-variant)', marginTop: 6 }}>
            Перемещено {progress.done} из {progress.total}
          </div>
        </div>
      )}
    </Modal>
  );
}
