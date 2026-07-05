import { useEffect, useRef, useState } from 'react';

export type TorrentStatus =
  | 'unknown' | 'metadata' | 'downloading' | 'seeding' | 'paused' | 'completed' | 'error';

export interface Torrent {
  id: string;
  infoHash: string;
  name: string;
  status: TorrentStatus;
  progress: number;
  totalSize: number;
  downloaded: number;
  uploaded: number;
  downloadSpeed: number;
  uploadSpeed: number;
  seeds: number;
  leechers: number;
  ratio: number;
  etaSeconds: number;
  completed: boolean;
}

export interface TorrentFile {
  index: number;
  path: string;
  size: number;
  downloaded: number;
  progress: number;
  priority: number;
}

/**
 * Живой прогресс торрентов через SSE (/api/torrents/stream). Бэкенд шлёт полный
 * снапшот списка каждые ~1.5 c. EventSource сам переподключается при обрыве.
 */
export function useTorrentStream(): { torrents: Torrent[]; connected: boolean } {
  const [torrents, setTorrents] = useState<Torrent[]>([]);
  const [connected, setConnected] = useState(false);
  const esRef = useRef<EventSource | null>(null);

  useEffect(() => {
    const es = new EventSource('/api/torrents/stream', { withCredentials: true });
    esRef.current = es;

    es.onopen = () => setConnected(true);
    es.onmessage = (e) => {
      try {
        const data = JSON.parse(e.data) as { torrents?: Torrent[] };
        setTorrents(data.torrents ?? []);
      } catch {
        /* пропускаем битый кадр */
      }
    };
    es.onerror = () => setConnected(false);

    return () => es.close();
  }, []);

  return { torrents, connected };
}
