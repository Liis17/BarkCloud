// Data-слой: same-origin /api (бэкенд проксирует в Files/Users/Identity с токеном из cookie).
// Перенос из Pages/shared.jsx с типизацией.

export class ApiError extends Error {
  code?: string;
  status?: number;
  constructor(message: string, opts?: { code?: string; status?: number }) {
    super(message);
    this.name = 'ApiError';
    this.code = opts?.code;
    this.status = opts?.status;
  }
}

/** Same-origin прокси байтов картинки (обходит CORS/tainted-canvas для копирования в буфер). */
export function proxiedImageUrl(url: string): string {
  return '/api/files/image?url=' + encodeURIComponent(url);
}

export async function api<T = unknown>(path: string, opts: RequestInit = {}): Promise<T> {
  const res = await fetch(path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
    ...opts,
  });
  if (res.status === 401) {
    window.location.href = '/login';
    throw new ApiError('unauthorized', { status: 401 });
  }

  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      /* не-JSON ответ */
    }
  }

  if (!res.ok) {
    const obj = (data ?? {}) as { error?: string; message?: string; code?: string };
    const err = new ApiError(obj.error || obj.message || `Ошибка ${res.status}`, {
      code: obj.code,
      status: res.status,
    });
    throw err;
  }
  return data as T;
}

export const apiGet = <T = unknown>(path: string, opts?: RequestInit) => api<T>(path, opts);
export const apiPost = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: JSON.stringify(body || {}) });
export const apiPut = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body || {}) });

/** Открыть системный диалог выбора файлов. */
export function pickFiles({ accept, multiple = true }: { accept?: string; multiple?: boolean } = {}): Promise<File[]> {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = multiple;
    if (accept) input.accept = accept;
    input.style.display = 'none';
    document.body.appendChild(input);
    input.onchange = () => {
      const files = Array.from(input.files || []);
      document.body.removeChild(input);
      resolve(files);
    };
    input.click();
  });
}

export interface DuplicateLocation {
  entryId: string;
  name: string;
  directoryId: string;
  directoryName: string;
}

export interface BatchSummary {
  total: number;
  succeeded: number;
  failed: number;
  invalidIds?: string[];
  succeededIds?: string[];
  failedIds?: string[];
}

export async function deleteEntriesBatch(entryIds: string[]): Promise<BatchSummary> {
  if (!entryIds.length) return { total: 0, succeeded: 0, failed: 0, invalidIds: [] };
  return apiPost<BatchSummary>('/api/cloud/entries/delete', { entryIds });
}

export async function deleteMediaBatch(fileIds: string[]): Promise<BatchSummary> {
  if (!fileIds.length) return { total: 0, succeeded: 0, failed: 0, invalidIds: [] };
  return apiPost<BatchSummary>('/api/cloud/media/delete-batch', { fileIds });
}

export interface ArchivePayload {
  entryIds?: string[];
  fileIds?: string[];
  directoryId?: string;
  albumId?: string;
  name?: string;
}

/** Собрать выбранное / папку / альбом в ZIP на сервере (архив кладётся в корзину на 3 дня)
 *  и открыть скачивание. Запрос синхронный — может занять время на больших объёмах. */
export async function downloadArchive(payload: ArchivePayload): Promise<void> {
  const d = await apiPost<{ url?: string; fileName?: string }>('/api/cloud/archive', payload);
  if (!d.url) throw new Error('Ссылка на архив недоступна');
  window.open(d.url, '_blank');
}

/** Проверка наличия контента по SHA256 (без побочных эффектов): есть ли уже такой файл
 *  у пользователя и где он лежит. Хеш вычисляется потоковым Worker до этого вызова. */
export async function checkDuplicateHash(hash: string): Promise<{ exists: boolean; locations: DuplicateLocation[] }> {
  try {
    const r = await apiPost<{ exists?: boolean; locations?: DuplicateLocation[] }>('/api/files/check-hash', { hash });
    return { exists: !!r.exists, locations: r.locations || [] };
  } catch {
    return { exists: false, locations: [] };
  }
}
