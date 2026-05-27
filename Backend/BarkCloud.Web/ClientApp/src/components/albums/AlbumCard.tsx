import { plural } from '../../lib/format';
import type { Album } from '../../lib/types';

interface AlbumCardProps {
  album: Album;
  onOpen: (album: Album) => void;
}

export function AlbumCard({ album, onOpen }: AlbumCardProps) {
  return (
    <div className="album-card" onClick={() => onOpen(album)}>
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
