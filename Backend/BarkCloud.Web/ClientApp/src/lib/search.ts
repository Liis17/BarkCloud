import type { NavigateFunction } from 'react-router-dom';
import type { CardFile, MediaKind } from './types';

export type SearchSectionKey = 'photos' | 'videos' | 'files' | 'tracks' | 'albums' | 'playlists' | 'folders' | 'shared' | 'trash' | 'torrents';
export type SearchHitKind = 'photo' | 'video' | 'file' | 'track' | 'album' | 'playlist' | 'folder' | 'dynamicFolder' | 'sharedFile' | 'sharedFolder' | 'sharedPlaylist' | 'trash' | 'torrent';

export interface SearchHit {
  kind: SearchHitKind;
  id: string;
  fileId: string;
  entryId: string;
  title: string;
  subtitle: string;
  previewUrl: string;
  mediaKind: string;
  favorite: boolean;
  matchField: string;
  matchValue: string;
  createdAt: string | null;
  size: number;
  status?: string;
  progress?: number;
}

export interface SearchSection {
  key: SearchSectionKey;
  items: SearchHit[];
  nextCursor: string;
  hasMore: boolean;
  unavailable: boolean;
}

export interface SearchResponse {
  query: string;
  sections: SearchSection[];
}

export const SECTION_LABEL: Record<SearchSectionKey, string> = {
  photos: 'Фото', videos: 'Видео', files: 'Файлы', tracks: 'Музыка', albums: 'Альбомы', playlists: 'Плейлисты',
  folders: 'Папки', shared: 'Доступно мне', trash: 'Корзина', torrents: 'Торренты',
};

export function isGridSection(key: SearchSectionKey): boolean {
  return key === 'photos' || key === 'videos' || key === 'albums' || key === 'playlists';
}

export function matchLabel(hit: SearchHit): string {
  const labels: Record<string, string> = {
    alias: 'алиас', tag: 'тег', artist: 'исполнитель', album: 'альбом', title: 'название',
    documentTitle: 'заголовок документа', documentAuthor: 'автор документа', documentSubject: 'тема документа', description: 'описание',
  };
  return labels[hit.matchField] || '';
}

export function searchHitIconName(hit: SearchHit): string {
  switch (hit.kind) {
    case 'photo': return 'photo';
    case 'video': return 'video';
    case 'track': case 'playlist': case 'sharedPlaylist': return 'music';
    case 'folder': case 'dynamicFolder': case 'sharedFolder': return 'folder';
    case 'torrent': return 'torrent';
    default: return 'file';
  }
}

export function openSearchHit(hit: SearchHit, navigate: NavigateFunction): void {
  switch (hit.kind) {
    case 'photo': navigate(`/photos?open=${encodeURIComponent(hit.fileId)}`); break;
    case 'video': navigate(`/videos?open=${encodeURIComponent(hit.fileId)}`); break;
    case 'file': navigate('/files', { state: { selectEntryId: hit.entryId } }); break;
    case 'track': navigate(`/music?track=${encodeURIComponent(hit.fileId)}`); break;
    case 'album': navigate(`/albums?album=${encodeURIComponent(hit.id)}`); break;
    case 'playlist': navigate(`/music?playlist=${encodeURIComponent(hit.id)}`); break;
    case 'folder': navigate(`/files?dir=${encodeURIComponent(hit.id)}`); break;
    case 'dynamicFolder': navigate(`/files?smart=${encodeURIComponent(hit.id)}`); break;
    case 'sharedFile': case 'sharedFolder': case 'sharedPlaylist': navigate(`/shared?open=${encodeURIComponent(hit.id)}`); break;
    case 'trash': navigate(`/trash?open=${encodeURIComponent(hit.id)}`); break;
    case 'torrent': navigate(`/torrents?open=${encodeURIComponent(hit.id)}`); break;
  }
}

/** Минимальная карточка для прямого открытия результата, которого ещё нет в пагинации галереи. */
export function searchHitToCardFile(hit: SearchHit): CardFile {
  const kind: MediaKind = hit.kind === 'photo' ? 'photo' : hit.kind === 'video' ? 'video' : 'other';
  return {
    id: hit.fileId || hit.id,
    name: hit.title,
    ext: '',
    kind,
    iconKind: kind === 'photo' ? 'img' : 'vid',
    size: hit.size,
    sizeLabel: '',
    width: 0,
    height: 0,
    previews: hit.previewUrl ? [{ w: 512, target: 512, url: hit.previewUrl }] : [],
    createdAt: hit.createdAt,
    uploadedAt: hit.createdAt,
  };
}
