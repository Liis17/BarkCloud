import React from 'react';

export interface MigrationSource {
  id: string; serviceUrl: string; bucketName: string; roles: string[]; profileIds: string[];
}
export interface MigrationLocation {
  serviceUrl: string; bucketName: string; region: string; forcePathStyle: boolean; isR2: boolean;
}
export interface MigrationConnection extends MigrationLocation { accessKey: string; secretKey: string }
export interface MigrationJob {
  id: string; state: string; phase: string; source: MigrationSource; destination: MigrationLocation;
  totalBytes: string; copiedBytes: string; currentBytes: string; totalFiles: number; copiedFiles: number;
  currentKey: string | null; currentName: string | null; error: string | null; activeUploads: number;
  canCancel: boolean; canRetry: boolean; canApply: boolean; createdAt: string;
}
export interface MigrationCutover {
  id: string; state: string; source: MigrationSource; destination: MigrationLocation;
  activeUploads: number; canCancel: boolean; canRecover: boolean;
}
export class MigrationApiError extends Error {
  constructor(message: string, public status: number) { super(message); }
}
export async function migrationRequest<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', signal,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (response.status === 401) window.location.href = '/login';
  const data = await response.json().catch(() => null) as T & { message?: string };
  if (!response.ok) throw new MigrationApiError(data?.message || 'Не удалось выполнить операцию', response.status);
  return data;
}

/** Poll only the visible settings panel. The task itself is owned by Web. */
export function useStorageMigration(active: boolean, onExpired: () => void) {
  const [jobs, setJobs] = React.useState<MigrationJob[]>([]);
  const [cutovers, setCutovers] = React.useState<MigrationCutover[]>([]);
  const [error, setError] = React.useState('');
  const [loading, setLoading] = React.useState(true);
  const expired = React.useRef(onExpired);
  expired.current = onExpired;
  const refresh = React.useCallback(async (signal?: AbortSignal) => {
    const results = await Promise.allSettled([
      migrationRequest<MigrationJob[]>('/api/settings/migration/jobs', undefined, signal),
      migrationRequest<MigrationCutover[]>('/api/settings/migration/cutovers', undefined, signal),
    ]);
    if (signal?.aborted) return;
    let message = '';
    for (const result of results) if (result.status === 'rejected') {
      if (result.reason instanceof MigrationApiError && result.reason.status === 403) { expired.current(); return; }
      message = result.reason instanceof Error ? result.reason.message : 'Не удалось получить состояние';
    }
    if (results[0].status === 'fulfilled') setJobs(results[0].value);
    if (results[1].status === 'fulfilled') setCutovers(results[1].value);
    setError(message); setLoading(false);
  }, []);
  React.useEffect(() => {
    if (!active) return;
    let current: AbortController | undefined;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      if (document.visibilityState === 'hidden' || current || stopped) return;
      const request = new AbortController(); current = request;
      try { await refresh(request.signal); }
      finally {
        if (current === request) {
          current = undefined;
          if (!stopped && !document.hidden) timer = setTimeout(() => void poll(), 1000);
        }
      }
    };
    const visibility = () => {
      clearTimeout(timer); current?.abort(); current = undefined;
      if (document.visibilityState !== 'hidden') void poll();
    };
    document.addEventListener('visibilitychange', visibility);
    void poll();
    return () => { stopped = true; current?.abort(); clearTimeout(timer); document.removeEventListener('visibilitychange', visibility); };
  }, [active, refresh]);
  return { jobs, cutovers, error, loading, refresh };
}
