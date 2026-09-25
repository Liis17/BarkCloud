import React from 'react';
import { apiGet } from '../lib/api';
import type { MediaStats } from '../lib/types';
import type { ToastPush } from './useToast';

/** Полная статистика галереи отдельно от cursor-пагинации списка. */
export function useMediaStats(kind: 'photo' | 'video', attachVersion: number, toast: ToastPush) {
  const [stats, setStats] = React.useState<MediaStats | null>(null);
  const requestId = React.useRef(0);

  const refresh = React.useCallback(async () => {
    const id = ++requestId.current;
    try {
      const result = await apiGet<MediaStats>(`/api/cloud/media/stats?kind=${kind}`);
      if (requestId.current === id) setStats(result);
    } catch (e) {
      if (requestId.current === id) toast((e as Error).message, 'err');
    }
  }, [kind, toast]);

  React.useEffect(() => {
    void refresh();
  }, [refresh, attachVersion]);

  return { stats, refresh };
}
