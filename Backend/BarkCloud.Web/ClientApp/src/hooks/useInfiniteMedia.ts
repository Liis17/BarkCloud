import React from 'react';
import { apiGet } from '../lib/api';
import type { MediaItem, Page } from '../lib/types';
import type { ToastPush } from './useToast';

interface CacheEntry { items: MediaItem[]; cursor: { at: string; id: string } | null }
const _cache = new Map<string, CacheEntry>();

/** Бесконечная прокрутка галереи: cursor-пагинация /api/cloud/media + IntersectionObserver. */
export function useInfiniteMedia(kind: 'photo' | 'video', toast?: ToastPush) {
  const init = React.useRef(_cache.get(kind) || null);
  const [items, setItems] = React.useState<MediaItem[]>(init.current ? init.current.items : []);
  const [loading, setLoading] = React.useState(!init.current);
  const [done, setDone] = React.useState(false);
  const cursorRef = React.useRef<{ at: string; id: string } | null>(null);
  const busyRef = React.useRef(false);
  const doneRef = React.useRef(false);
  const observerRef = React.useRef<IntersectionObserver | null>(null);
  const itemsRef = React.useRef(items);
  itemsRef.current = items;

  React.useEffect(() => {
    return () => {
      _cache.set(kind, { items: itemsRef.current, cursor: cursorRef.current });
    };
  }, [kind]);

  const loadMore = React.useCallback(async () => {
    if (busyRef.current || doneRef.current) return;
    busyRef.current = true;
    setLoading(true);
    try {
      const c = cursorRef.current;
      let q = '/api/cloud/media?kind=' + encodeURIComponent(kind) + '&limit=60';
      if (c) q += '&cursorAt=' + encodeURIComponent(c.at) + '&cursorId=' + encodeURIComponent(c.id);
      const d = await apiGet<Page<MediaItem>>(q);
      const batch = d.items || [];
      setItems((prev) => (c ? prev.concat(batch) : batch));
      if (d.nextCursorAt) cursorRef.current = { at: d.nextCursorAt, id: d.nextCursorId! };
      else {
        cursorRef.current = null;
        doneRef.current = true;
        setDone(true);
      }
    } catch (e) {
      toast && toast((e as Error).message, 'err');
      doneRef.current = true;
      setDone(true);
    } finally {
      busyRef.current = false;
      setLoading(false);
    }
  }, [kind, toast]);

  React.useEffect(() => {
    cursorRef.current = null;
    busyRef.current = false;
    doneRef.current = false;
    setDone(false);
    setLoading(true);
    if (!init.current) setItems([]);
    loadMore();
    init.current = null;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind]);

  const loadMoreRef = React.useRef(loadMore);
  loadMoreRef.current = loadMore;

  const sentinelRef = React.useCallback((node: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    if (!node) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) loadMoreRef.current();
      },
      { rootMargin: '600px' },
    );
    io.observe(node);
    observerRef.current = io;
  }, []);

  const removeItem = React.useCallback((id: string) => setItems((prev) => prev.filter((x) => x.id !== id)), []);
  const updateItem = React.useCallback(
    (id: string, patch: Partial<MediaItem>) => setItems((prev) => prev.map((x) => (x.id === id ? { ...x, ...patch } : x))),
    [],
  );
  const reload = React.useCallback(() => {
    cursorRef.current = null;
    busyRef.current = false;
    doneRef.current = false;
    setItems([]);
    setDone(false);
    setLoading(true);
    loadMore();
  }, [loadMore]);

  const prependItems = React.useCallback((fresh: MediaItem[]) => {
    setItems((prev) => {
      const existing = new Set(prev.map((x) => x.id));
      const newItems = fresh.filter((x) => !existing.has(x.id));
      if (!newItems.length) return prev;
      return [...newItems, ...prev];
    });
  }, []);

  return { items, loading, done, sentinelRef, removeItem, updateItem, reload, prependItems };
}
