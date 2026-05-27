import React from 'react';
import { apiGet } from '../lib/api';
import type { Album, CardFile, Page } from '../lib/types';

const EMPTY_SET: Set<string> = new Set();

/** Членство файлов в альбомах: лениво строит fileId -> Set(albumId), перебирая ListAlbumItems. */
export function useAlbumMembership(albums: Album[] | undefined) {
  const [map, setMap] = React.useState<Map<string, Set<string>>>(() => new Map());
  const loadedRef = React.useRef(false);

  const ensureLoaded = React.useCallback(async () => {
    if (loadedRef.current || !albums || !albums.length) return;
    loadedRef.current = true;
    try {
      const next = new Map<string, Set<string>>();
      for (const a of albums) {
        const d = await apiGet<Page<CardFile>>('/api/albums/items?album=' + encodeURIComponent(a.id) + '&limit=200');
        for (const it of d.items || []) {
          if (!next.has(it.id)) next.set(it.id, new Set());
          next.get(it.id)!.add(a.id);
        }
      }
      setMap(next);
    } catch {
      loadedRef.current = false;
    }
  }, [albums]);

  const of = React.useCallback((fileId: string) => map.get(fileId) || EMPTY_SET, [map]);
  const addLocal = React.useCallback(
    (fileId: string, albumId: string) =>
      setMap((m) => {
        const n = new Map(m);
        const s = new Set(n.get(fileId) || []);
        s.add(albumId);
        n.set(fileId, s);
        return n;
      }),
    [],
  );
  const removeLocal = React.useCallback(
    (fileId: string, albumId: string) =>
      setMap((m) => {
        const n = new Map(m);
        const s = new Set(n.get(fileId) || []);
        s.delete(albumId);
        n.set(fileId, s);
        return n;
      }),
    [],
  );

  return { of, ensureLoaded, addLocal, removeLocal };
}
