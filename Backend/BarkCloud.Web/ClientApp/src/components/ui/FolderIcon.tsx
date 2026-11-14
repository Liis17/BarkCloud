import { Icon } from '../Icon';
import { findFolderIcon, foregroundFor } from '../../lib/folderIcons';
import { normalizeHex } from './TagColorPicker';

interface FolderIconProps {
  iconKey?: string | null;
  iconColor?: string | null;
  size?: number;
  className?: string;
}

/** Плитка папки: стандартная иконка в цветах темы или иконка из пака на выбранной подложке. */
export function FolderIcon({ iconKey, iconColor, size = 22, className = '' }: FolderIconProps) {
  const icon = findFolderIcon(iconKey);
  const color = iconColor ? normalizeHex(iconColor) : null;
  const style = color ? { background: color, color: foregroundFor(color) } : undefined;
  return (
    <div className={'file-icon folder ' + className} style={style}>
      {icon
        ? <span className="folder-glyph" style={{ width: size, height: size }} aria-hidden="true" dangerouslySetInnerHTML={{ __html: icon.svg }} />
        : <Icon.folder size={size} />}
    </div>
  );
}
