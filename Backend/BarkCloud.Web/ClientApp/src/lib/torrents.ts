// API-хелперы вкладки «Торренты» (проксируются веб-бэкендом в торрент-сервис).
import { api, apiPost, ApiError } from './api';
import type { Torrent, TorrentFile } from '../hooks/useTorrentStream';

export const listTorrents = () => api<{ torrents: Torrent[] }>('/api/torrents');

export const addMagnet = (magnet: string) =>
  apiPost<Torrent>('/api/torrents/magnet', { magnet });

/** Загрузка .torrent-файла (multipart, минуя JSON-обёртку api()). */
export async function addTorrentFile(file: File): Promise<Torrent> {
  const fd = new FormData();
  fd.append('file', file, file.name);
  const res = await fetch('/api/torrents/file', { method: 'POST', credentials: 'same-origin', body: fd });
  if (res.status === 401) {
    window.location.href = '/login';
    throw new ApiError('unauthorized', { status: 401 });
  }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new ApiError((data as { error?: string }).error || `Ошибка ${res.status}`, { status: res.status });
  return data as Torrent;
}

export const listFiles = (id: string) =>
  api<{ files: TorrentFile[] }>(`/api/torrents/${id}/files`);

export const pauseTorrent = (id: string) => apiPost(`/api/torrents/${id}/pause`);
export const resumeTorrent = (id: string) => apiPost(`/api/torrents/${id}/resume`);

export const removeTorrent = (id: string, deleteFiles: boolean) =>
  api(`/api/torrents/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' });

export const setFilePriority = (id: string, index: number, priority: number) =>
  apiPost(`/api/torrents/${id}/files/${index}/priority`, { priority });

export const importToCloud = (id: string, dir?: string, fileIndex?: number) =>
  apiPost<{ files: { fileId: string; name: string }[] }>(`/api/torrents/${id}/import`, { dir, fileIndex });

export function downloadUrl(id: string, fileIndex: number): string {
  return `/api/torrents/${id}/download?file=${fileIndex}`;
}
