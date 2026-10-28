import React from 'react';
import { Icon } from '../Icon';
import { DynamicFolderCard } from './DynamicFolderCard';
import type { DynamicFolder } from '../../lib/types';

interface Props {
  folders: DynamicFolder[];
  onOpen: (folder: DynamicFolder) => void;
  onCreate: () => void;
}

/** Колесо над лентой крутит её по горизонтали; у края или без переполнения событие уходит странице. */
function useWheelToHorizontal() {
  const ref = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
      const max = el.scrollWidth - el.clientWidth;
      const canScroll = e.deltaY > 0 ? el.scrollLeft < max : el.scrollLeft > 0;
      if (!canScroll) return;
      e.preventDefault();
      el.scrollLeft += e.deltaY;
    };
    // passive: false нужен, иначе preventDefault браузер игнорирует
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);
  return ref;
}

/** Горизонтальная лента умных папок (2 ряда, скролл вправо) + плитка создания. */
export function DynamicFoldersStrip({ folders, onOpen, onCreate }: Props) {
  const ref = useWheelToHorizontal();
  return (
    <div ref={ref} className="dynamic-folders">
      {folders.map((f) => (
        <DynamicFolderCard key={f.id} folder={f} onOpen={onOpen} />
      ))}
      <div className="df-card new-df" onClick={onCreate}>
        <Icon.plus size={22} />
        <span>Умная папка</span>
      </div>
    </div>
  );
}

/** Заглушка ленты на время загрузки: та же сетка, что у ленты, чтобы список файлов не прыгал. */
export function DynamicFoldersSkeleton() {
  const ref = useWheelToHorizontal();
  return (
    <div ref={ref} className="dynamic-folders" aria-busy="true">
      {Array.from({ length: 14 }, (_, i) => (
        <div key={i} className="df-card df-skeleton" />
      ))}
    </div>
  );
}
