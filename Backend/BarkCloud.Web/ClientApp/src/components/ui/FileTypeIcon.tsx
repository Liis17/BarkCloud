import { fileTypeIconKey } from '../../lib/fileTypeIcons';
import { findFolderIcon } from '../../lib/folderIcons';
import type { CardFile } from '../../lib/types';

interface FileTypeIconProps {
  media: Pick<CardFile, 'ext' | 'iconKind' | 'kind'> | null | undefined;
  size?: number;
  className?: string;
}

/** Плитка файла без превью: глиф типа из пака FolderIcons на цвете iconKind и подпись расширения. */
export function FileTypeIcon({ media, size = 40, className = '' }: FileTypeIconProps) {
  const icon = findFolderIcon(fileTypeIconKey(media));
  return (
    <div className={'file-icon file-type-icon ' + (media?.iconKind || 'doc') + ' ' + className}>
      {icon && <span className="folder-glyph" style={{ width: size, height: size }} aria-hidden="true" dangerouslySetInnerHTML={{ __html: icon.svg }} />}
      <span className="file-type-ext">{media?.ext || 'FILE'}</span>
    </div>
  );
}
