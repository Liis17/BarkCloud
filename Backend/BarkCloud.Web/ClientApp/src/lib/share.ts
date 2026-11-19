import { apiPost } from './api';
import type { ShareLink } from './types';
import type { ToastPush } from '../hooks/useToast';
import { formatLinkExpiry, openPublicLinkDialog } from '../components/ui/PublicLinkModal';

/**
 * Спросить срок действия, создать публичную ссылку и скопировать её в буфер.
 * Ошибка показывается тостом, диалог при этом остаётся открытым.
 */
function createWithExpiry(title: string, url: string, body: object, copiedText: string, errorText: string, toast: ToastPush): void {
  openPublicLinkDialog({
    title,
    create: async (expiresAt) => {
      let link: ShareLink;
      try {
        link = await apiPost<ShareLink>(url, { ...body, expiresAt });
        await navigator.clipboard.writeText(link.url);
      } catch (e) {
        toast((e as Error).message || errorText, 'err');
        throw e;
      }
      toast(link.expiresAt ? `${copiedText} · действует до ${formatLinkExpiry(link.expiresAt)}` : copiedText);
    },
  });
}

/**
 * Создать публичную ссылку на файл и скопировать её в буфер.
 * Используется из контекстных меню «Файлы» и галерей «Фото»/«Видео».
 */
export function createShare(fileId: string, name: string, toast: ToastPush): void {
  createWithExpiry('Публичная ссылка на файл', '/api/shares', { fileId, name }, 'Публичная ссылка скопирована', 'Не удалось создать ссылку', toast);
}

/** Сделать папку публичной (идемпотентно, срок заменяется выбранным) и скопировать ссылку /f/{token}. */
export function createFolderShare(directoryId: string, name: string, toast: ToastPush): void {
  createWithExpiry('Публичная ссылка на папку', '/api/folder-shares', { directoryId, name }, 'Ссылка на папку скопирована', 'Не удалось сделать папку публичной', toast);
}

/** Сделать альбом публичным (идемпотентно, срок заменяется выбранным) и скопировать ссылку /al/{token}. */
export function createAlbumShare(albumId: string, name: string, toast: ToastPush): void {
  createWithExpiry('Публичная ссылка на альбом', '/api/album-shares', { albumId, name }, 'Ссылка на альбом скопирована', 'Не удалось сделать альбом публичным', toast);
}

/** Сделать музыкальный плейлист публичным (идемпотентно, срок заменяется выбранным) и скопировать ссылку /mpl/{token}. */
export function createMusicPlaylistShare(playlistId: string, name: string, toast: ToastPush): void {
  createWithExpiry('Публичная ссылка на плейлист', '/api/music/playlist-shares', { playlistId, name }, 'Ссылка на плейлист скопирована', 'Не удалось сделать плейлист публичным', toast);
}
