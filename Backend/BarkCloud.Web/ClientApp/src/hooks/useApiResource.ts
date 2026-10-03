import React from 'react';
import { apiGet, ApiError } from '../lib/api';

/** Данные живут вместе с панелью; повторный запрос объединяется, устаревший отменяется. */
export function useApiResource<T>(path: string | null, active = true, pollWhen?: (data: T) => boolean) {
  const [data, setData] = React.useState<T | null>(null);
  const [error, setError] = React.useState<ApiError | null>(null);
  const [loading, setLoading] = React.useState(false);
  const pending = React.useRef<{ controller: AbortController; promise: Promise<void> } | null>(null);

  const reload = React.useCallback(() => {
    if (!path) return Promise.resolve();
    if (pending.current) return pending.current.promise;
    const controller = new AbortController();
    const entry = { controller, promise: Promise.resolve() };
    pending.current = entry;
    setLoading(true);
    const timeout = window.setTimeout(() => controller.abort('timeout'), 10000);
    entry.promise = apiGet<T>(path, { signal: controller.signal })
      .then((value) => {
        if (pending.current !== entry || controller.signal.aborted) return;
        setData(value);
        setError(null);
      })
      .catch((reason: unknown) => {
        if (pending.current !== entry) return;
        if (controller.signal.aborted && controller.signal.reason !== 'timeout') return;
        setError(reason instanceof ApiError ? reason : new ApiError(
          controller.signal.reason === 'timeout' ? 'Сервер не ответил вовремя' : 'Не удалось загрузить данные'));
      })
      .finally(() => {
        window.clearTimeout(timeout);
        if (pending.current === entry) { pending.current = null; setLoading(false); }
      });
    return entry.promise;
  }, [path]);

  React.useEffect(() => {
    if (active) void reload();
    return () => {
      pending.current?.controller.abort();
      pending.current = null;
    };
  }, [active, reload]);

  React.useEffect(() => {
    if (!active || !path) return;
    let timer: number | undefined;
    const schedule = () => {
      window.clearTimeout(timer);
      if (!document.hidden && !loading && data && pollWhen?.(data))
        timer = window.setTimeout(() => { void reload(); }, 3000);
    };
    const onVisibility = () => {
      schedule();
      if (!document.hidden) void reload();
    };
    schedule();
    document.addEventListener('visibilitychange', onVisibility);
    return () => { window.clearTimeout(timer); document.removeEventListener('visibilitychange', onVisibility); };
  }, [active, path, data, loading, pollWhen, reload]);

  return { data, error, loading, reload };
}
