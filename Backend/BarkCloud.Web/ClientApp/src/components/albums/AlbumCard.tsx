import { plural } from '../../lib/format';
import type { Album } from '../../lib/types';
import type { FileDropHandlers } from '../../hooks/useFileDrop';

interface AlbumCardProps {
  album: Album;
  onOpen: (album: Album) => void;
  dropHandlers?: FileDropHandlers;
  active?: boolean;
}

export function AlbumCard({ album, onOpen, dropHandlers, active }: AlbumCardProps) {
  return (
    <div className={'album-card' + (active ? ' drop-target' : '')} {...dropHandlers} onClick={() => onOpen(album)}>
      {album.coverUrl ? (
        <img className="thumb" src={album.coverUrl} alt="" loading="lazy" style={{ objectFit: 'cover' }} />
      ) : (
        <div className="thumb" style={{ '--tint-a': '#B4A3D6', '--tint-b': '#5B4889' } as React.CSSProperties} />
      )}
      <div className="overlay">
        <div className="badge">Альбом</div>
        <div className="a-name">{album.name}</div>
        <div className="a-meta">
          {album.count} {plural(album.count, 'элемент', 'элемента', 'элементов')}
          {album.description ? ' · ' + album.description : ''}
        </div>
      </div>
    </div>
  );
}
