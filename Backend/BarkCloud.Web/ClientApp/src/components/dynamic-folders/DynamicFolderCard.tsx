import React from 'react';
import { Icon } from '../Icon';
import { plural } from '../../lib/format';
import type { DynamicFolder } from '../../lib/types';

// iconKey с бэкенда → доступная иконка набора (clock/server/photo), иначе папка.
const ICONS: Record<string, (p?: { size?: number }) => React.ReactNode> = {
  clock: Icon.clock,
  hdd: Icon.server,
  camera: Icon.photo,
};

interface Props {
  folder: DynamicFolder;
  onOpen: (folder: DynamicFolder) => void;
}

/** Квадратная плитка умной папки: обложка (или цветной tint + иконка), имя, счётчик. */
export function DynamicFolderCard({ folder, onOpen }: Props) {
  const ic = ICONS[folder.iconKey] || Icon.folder;
  return (
    <div className="df-card" onClick={() => onOpen(folder)}>
      {folder.coverUrl ? (
        <img className="thumb" src={folder.coverUrl} alt="" loading="lazy" style={{ objectFit: 'cover' }} />
      ) : (
        <div className="thumb df-tint" style={{ background: folder.coverColor || 'var(--md-surface-container-high)' }} />
      )}
      <div className="df-icon">{ic({ size: 18 })}</div>
      <div className="overlay">
        <div className="df-name">{folder.name}</div>
        <div className="df-meta">
          {folder.count} {plural(folder.count, 'файл', 'файла', 'файлов')}
        </div>
      </div>
    </div>
  );
}
