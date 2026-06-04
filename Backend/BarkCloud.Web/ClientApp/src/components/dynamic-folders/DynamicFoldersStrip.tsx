import { Icon } from '../Icon';
import { DynamicFolderCard } from './DynamicFolderCard';
import type { DynamicFolder } from '../../lib/types';

interface Props {
  folders: DynamicFolder[];
  onOpen: (folder: DynamicFolder) => void;
  onCreate: () => void;
}

/** Горизонтальная лента умных папок (2 ряда, скролл вправо) + плитка создания. */
export function DynamicFoldersStrip({ folders, onOpen, onCreate }: Props) {
  return (
    <div className="dynamic-folders">
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
