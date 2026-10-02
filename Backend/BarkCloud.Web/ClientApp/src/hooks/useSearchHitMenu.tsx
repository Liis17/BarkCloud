import React from 'react';
import { useNavigate } from 'react-router-dom';
import { ContextMenu, type ContextItem } from '../components/ui/ContextMenu';
import { RenameModal } from '../components/ui/RenameModal';
import { ConfirmModal } from '../components/ui/ConfirmModal';
import { ShareWithUserModal } from '../components/ui/ShareWithUserModal';
import { useMediaActions } from './useMediaActions';
import { apiGet, apiPost, downloadArchive } from '../lib/api';
import { createAlbumShare, createFolderShare, createMusicPlaylistShare } from '../lib/share';
import { openSearchHit, searchHitToMediaItem, type SearchHit } from '../lib/search';
import * as T from '../lib/torrents';
import type { Album } from '../lib/types';
import type { ToastPush } from './useToast';

interface UseSearchHitMenuArgs {
  toast: ToastPush;
  onRenamed: (hit: SearchHit, title: string) => void;
  onRemoved: (hit: SearchHit) => void;
  onPatched: (hit: SearchHit, patch: Partial<SearchHit>) => void;
}

interface PendingConfirm {
  title: string;
  message: string;
  confirmLabel: string;
  run: () => Promise<void>;
}

/** Хит — файл владельца (есть что передать в useMediaActions). */
function isOwnedFileHit(hit: SearchHit): boolean {
  return (hit.kind === 'photo' || hit.kind === 'video' || hit.kind === 'file' || hit.kind === 'track') && !!hit.fileId;
}

/** ПКМ-меню результатов поиска. Файлы/медиа — через useMediaActions, остальные типы — своё меню. */
export function useSearchHitMenu({ toast, onRenamed, onRemoved, onPatched }: UseSearchHitMenuArgs) {
  const navigate = useNavigate();
  const [menu, setMenu] = React.useState<{ x: number; y: number; hit: SearchHit } | null>(null);
  const [rename, setRename] = React.useState<SearchHit | null>(null);
  const [confirm, setConfirm] = React.useState<PendingConfirm | null>(null);
  const [shareWith, setShareWith] = React.useState<SearchHit | null>(null);
  const [albums, setAlbums] = React.useState<Album[]>([]);
  const albumsRequested = React.useRef(false);
  // Хит, по которому открыто меню useMediaActions: его колбэки получают MediaItem, а не хит.
  const mediaHit = React.useRef<SearchHit | null>(null);

  function loadAlbums() {
    apiGet<{ albums: Album[] }>('/api/albums')
      .then((d) => setAlbums(d.albums || []))
      .catch((e) => {
        toast((e as Error).message, 'err');
        setAlbums([]);
      });
  }

  const media = useMediaActions({
    albums,
    toast,
    deleteByEntry: true,
    reloadAlbums: loadAlbums,
    onRenamed: (_m, name) => mediaHit.current && onRenamed(mediaHit.current, name),
    onRemoved: () => mediaHit.current && onRemoved(mediaHit.current),
  });

  function openMenu(e: React.MouseEvent, hit: SearchHit) {
    if (isOwnedFileHit(hit)) {
      mediaHit.current = hit;
      if ((hit.kind === 'photo' || hit.kind === 'video') && !albumsRequested.current) {
        albumsRequested.current = true;
        loadAlbums();
      }
      media.openMenu(e, searchHitToMediaItem(hit));
      return;
    }
    e.preventDefault();
    e.stopPropagation();
    setMenu({ x: e.clientX, y: e.clientY, hit });
  }

  async function attempt(fn: () => Promise<unknown>, okMessage?: string): Promise<boolean> {
    try {
      await fn();
      if (okMessage) toast(okMessage);
      return true;
    } catch (e) {
      toast((e as Error).message, 'err');
      return false;
    }
  }

  async function doRename(hit: SearchHit, name: string) {
    const ok = await attempt(
      () => hit.kind === 'album'
        ? apiPost('/api/albums/update', { album: hit.id, name })
        : apiPost('/api/cloud/dir/rename', { id: hit.id, name }),
      'Переименовано',
    );
    if (!ok) return;
    setRename(null);
    onRenamed(hit, name);
  }

  async function runConfirmed(pending: PendingConfirm) {
    try {
      await pending.run();
      setConfirm(null);
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  /** Подтверждаемое удаление: после успеха хит убирается из выдачи. */
  function confirmRemoval(hit: SearchHit, spec: Omit<PendingConfirm, 'run'> & { request: () => Promise<unknown>; done: string }) {
    setConfirm({
      title: spec.title,
      message: spec.message,
      confirmLabel: spec.confirmLabel,
      run: async () => {
        await spec.request();
        onRemoved(hit);
        toast(spec.done);
      },
    });
  }

  async function archiveFolder(hit: SearchHit) {
    toast('Готовлю архив папки…');
    await attempt(() => downloadArchive({ directoryId: hit.id, name: hit.title }), 'Архив готов, скачивание началось');
  }

  async function downloadShared(hit: SearchHit) {
    await attempt(async () => {
      const r = await apiPost<{ downloadUrl: string }>('/api/shared/download', { fileId: hit.fileId });
      if (r.downloadUrl) window.location.href = r.downloadUrl;
    });
  }

  async function toggleTorrent(hit: SearchHit) {
    const paused = hit.status === 'paused';
    const ok = await attempt(() => (paused ? T.resumeTorrent(hit.id) : T.pauseTorrent(hit.id)));
    if (ok) onPatched(hit, { status: paused ? ((hit.progress ?? 0) >= 1 ? 'seeding' : 'downloading') : 'paused' });
  }

  function buildItems(hit: SearchHit): ContextItem[] {
    const out: ContextItem[] = [{ label: 'Открыть', icon: 'eye', onClick: () => openSearchHit(hit, navigate) }];
    switch (hit.kind) {
      case 'folder':
        out.push(
          { label: 'Скачать папку', icon: 'download', onClick: () => archiveFolder(hit) },
          { label: 'Сделать папку публичной', icon: 'share', onClick: () => createFolderShare(hit.id, hit.title, toast) },
          { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareWith(hit) },
          { label: 'Переименовать', icon: 'pencil', onClick: () => setRename(hit) },
          { divider: true },
          {
            label: 'Удалить', icon: 'trash', danger: true,
            onClick: () => confirmRemoval(hit, {
              title: 'Удалить папку?',
              message: `Папка «${hit.title}» и файлы внутри попадут в корзину (14 дней).`,
              confirmLabel: 'Удалить',
              request: () => apiPost('/api/cloud/dir/delete', { id: hit.id }),
              done: 'Папка удалена, файлы в корзине',
            }),
          },
        );
        break;
      case 'album':
        out.push(
          { label: 'Публичная ссылка', icon: 'link', onClick: () => createAlbumShare(hit.id, hit.title, toast) },
          { label: 'Переименовать', icon: 'pencil', onClick: () => setRename(hit) },
          { divider: true },
          {
            label: 'Удалить альбом', icon: 'trash', danger: true,
            onClick: () => confirmRemoval(hit, {
              title: 'Удалить альбом?',
              message: `Альбом «${hit.title}» будет удалён. Файлы останутся в облаке.`,
              confirmLabel: 'Удалить',
              request: () => apiPost('/api/albums/delete', { album: hit.id }),
              done: 'Альбом удалён',
            }),
          },
        );
        break;
      case 'playlist':
        out.push(
          { label: 'Публичная ссылка', icon: 'link', onClick: () => createMusicPlaylistShare(hit.id, hit.title, toast) },
          { label: 'Поделиться с пользователем', icon: 'user', onClick: () => setShareWith(hit) },
          { divider: true },
          {
            label: 'Удалить плейлист', icon: 'trash', danger: true,
            onClick: () => confirmRemoval(hit, {
              title: 'Удалить плейлист?',
              message: `Плейлист «${hit.title}» будет удалён. Файлы треков останутся в облаке.`,
              confirmLabel: 'Удалить',
              request: () => apiPost('/api/music/playlists/delete', { playlistId: hit.id }),
              done: 'Плейлист удалён',
            }),
          },
        );
        break;
      case 'trash': {
        const entryId = hit.entryId || hit.id;
        out.push(
          {
            label: 'Восстановить', icon: 'refresh',
            onClick: async () => {
              if (await attempt(() => apiPost('/api/cloud/trash/restore', { entryId }), 'Восстановлено')) onRemoved(hit);
            },
          },
          { divider: true },
          {
            label: 'Удалить навсегда', icon: 'trash', danger: true,
            onClick: () => confirmRemoval(hit, {
              title: 'Удалить навсегда?',
              message: `«${hit.title}» будет удалён навсегда. Это действие необратимо.`,
              confirmLabel: 'Удалить навсегда',
              request: () => apiPost('/api/cloud/trash/purge', { entryId }),
              done: 'Удалено навсегда',
            }),
          },
        );
        break;
      }
      case 'torrent':
        out.push(
          hit.status === 'paused'
            ? { label: 'Возобновить', icon: 'play', onClick: () => toggleTorrent(hit) }
            : { label: 'Пауза', icon: 'pause', onClick: () => toggleTorrent(hit) },
          { divider: true },
          {
            label: 'Удалить', icon: 'trash', danger: true,
            onClick: () => confirmRemoval(hit, {
              title: 'Удалить торрент?',
              message: `«${hit.title}» и скачанные файлы будут удалены с диска.`,
              confirmLabel: 'Удалить',
              request: () => T.removeTorrent(hit.id, true),
              done: 'Торрент и файлы удалены',
            }),
          },
        );
        break;
      case 'sharedFile':
        out.push({ label: 'Скачать', icon: 'download', onClick: () => downloadShared(hit) });
        break;
    }
    return out;
  }

  const overlay = (
    <>
      {media.overlay}
      {menu && <ContextMenu x={menu.x} y={menu.y} items={buildItems(menu.hit)} onClose={() => setMenu(null)} />}
      {rename && (
        <RenameModal title="Переименовать" label="Имя" initial={rename.title} onClose={() => setRename(null)} onSave={(name) => doRename(rename, name)} />
      )}
      {confirm && (
        <ConfirmModal
          title={confirm.title}
          danger
          confirmLabel={confirm.confirmLabel}
          message={confirm.message}
          onClose={() => setConfirm(null)}
          onConfirm={() => runConfirmed(confirm)}
        />
      )}
      {shareWith && (
        <ShareWithUserModal
          folderId={shareWith.kind === 'folder' ? shareWith.id : undefined}
          playlistId={shareWith.kind === 'playlist' ? shareWith.id : undefined}
          fileName={shareWith.title}
          onClose={() => setShareWith(null)}
          toast={toast}
        />
      )}
    </>
  );

  return { overlay, openMenu };
}
