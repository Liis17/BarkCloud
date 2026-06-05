import { apiPost } from './api';
import type { ShareLink } from './types';
import type { ToastPush } from '../hooks/useToast';

/**
 * Создать постоянную публичную ссылку на файл и скопировать её в буфер.
 * Используется из контекстных меню «Файлы» и галерей «Фото»/«Видео».
 */
export async function createShare(fileId: string, name: string, toast: ToastPush): Promise<void> {
  try {
    const link = await apiPost<ShareLink>('/api/shares', { fileId, name });
    await navigator.clipboard.writeText(link.url);
    toast('Публичная ссылка скопирована');
  } catch (e) {
    toast((e as Error).message || 'Не удалось создать ссылку', 'err');
  }
}

/** Сделать папку публичной (идемпотентно) и скопировать ссылку /f/{token} в буфер. */
export async function createFolderShare(directoryId: string, name: string, toast: ToastPush): Promise<void> {
  try {
    const link = await apiPost<{ url: string }>('/api/folder-shares', { directoryId, name });
    await navigator.clipboard.writeText(link.url);
    toast('Ссылка на папку скопирована');
  } catch (e) {
    toast((e as Error).message || 'Не удалось сделать папку публичной', 'err');
  }
}

/** Сделать альбом публичным (идемпотентно) и скопировать ссылку /al/{token} в буфер. */
export async function createAlbumShare(albumId: string, name: string, toast: ToastPush): Promise<void> {
  try {
    const link = await apiPost<{ url: string }>('/api/album-shares', { albumId, name });
    await navigator.clipboard.writeText(link.url);
    toast('Ссылка на альбом скопирована');
  } catch (e) {
    toast((e as Error).message || 'Не удалось сделать альбом публичным', 'err');
  }
}
