import React from 'react';
import { apiGet, apiPost } from '../../lib/api';
import type { MusicPlaylist, Page } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';
import { EmptyState } from '../ui/EmptyState';
import { Modal } from '../ui/Modal';

interface PlaylistPickerModalProps {
  fileId: string;
  onClose: () => void;
  toast: ToastPush;
}

/** Выбор плейлиста для добавления аудиофайла из раздела «Файлы». */
export function PlaylistPickerModal({ fileId, onClose, toast }: PlaylistPickerModalProps) {
  const [playlists, setPlaylists] = React.useState<MusicPlaylist[]>([]);
  const [loading, setLoading] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [adding, setAdding] = React.useState(false);

  React.useEffect(() => {
    let alive = true;
    async function loadAll() {
      const all: MusicPlaylist[] = [];
      let cursor: { at: string; id: string } | null = null;
      try {
        while (true) {
          const params = new URLSearchParams({ limit: '200' });
          if (cursor) {
            params.set('cursorAt', cursor.at);
            params.set('cursorId', cursor.id);
          }
          const page = await apiGet<Page<MusicPlaylist>>('/api/music/playlists?' + params.toString());
          all.push(...(page.items || []));
          cursor = page.nextCursorAt && page.nextCursorId
            ? { at: page.nextCursorAt, id: page.nextCursorId }
            : null;
          if (!cursor) break;
        }
        if (alive) setPlaylists(all);
      } catch (e) {
        if (alive) setError((e as Error).message || 'Не удалось загрузить плейлисты');
      } finally {
        if (alive) setLoading(false);
      }
    }
    void loadAll();
    return () => { alive = false; };
  }, []);

  async function add(playlistId: string) {
    if (adding) return;
    setAdding(true);
    try {
      await apiPost('/api/music/playlists/tracks/add', { playlistId, fileIds: [fileId] });
      toast('Добавлено в плейлист');
      onClose();
    } catch (e) {
      toast((e as Error).message || 'Не удалось добавить в плейлист', 'err');
    } finally {
      setAdding(false);
    }
  }

  return (
    <Modal title="Добавить в плейлист" onClose={onClose}>
      {loading && <div className="prop-empty">Загрузка плейлистов…</div>}
      {error && <div className="prop-err">Не удалось загрузить плейлисты: {error}</div>}
      {!loading && !error && (
        <div className="playlist-pick-list">
          {playlists.length === 0 ? <EmptyState icon="music" title="Плейлистов пока нет" /> : playlists.map((playlist) => (
            <button key={playlist.id} className="playlist-pick-row" disabled={adding} onClick={() => add(playlist.id)}>
              <span>{playlist.name}</span>
              <span>{playlist.count}</span>
            </button>
          ))}
        </div>
      )}
    </Modal>
  );
}
