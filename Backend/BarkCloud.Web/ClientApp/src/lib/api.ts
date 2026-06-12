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

export const apiGet = <T = unknown>(path: string) => api<T>(path);
export const apiPost = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: 'POST', body: JSON.stringify(body || {}) });

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

export interface UploadResult {
  fileId: string;
  name: string;
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

/** SHA256 файла в hex. Читает файл целиком в память — допустимо при лимите 512 МБ;
 *  Web Crypto не умеет инкрементальный digest. null — если crypto недоступен (http) или ошибка. */
async function sha256Hex(file: File): Promise<string | null> {
  if (!globalThis.crypto?.subtle) return null;
  try {
    const buf = await file.arrayBuffer();
    const digest = await crypto.subtle.digest('SHA-256', buf);
    return Array.from(new Uint8Array(digest))
      .map((b) => b.toString(16).padStart(2, '0'))
      .join('');
  } catch {
    return null;
  }
}

/** Проверка наличия контента по SHA256 (без побочных эффектов): есть ли уже такой файл
 *  у пользователя и где он лежит. Нет crypto.subtle (http-контекст) → считаем «не дубликат». */
export async function checkDuplicate(file: File): Promise<{ exists: boolean; locations: DuplicateLocation[] }> {
  const hash = await sha256Hex(file);
  if (!hash) return { exists: false, locations: [] };
  try {
    const r = await apiPost<{ exists?: boolean; locations?: DuplicateLocation[] }>('/api/files/check-hash', { hash });
    return { exists: !!r.exists, locations: r.locations || [] };
  } catch {
    return { exists: false, locations: [] };
  }
}

/** Загрузка файла (новый блоб). Серверный дедуп снят — каждая загрузка создаёт копию;
 *  предварительную проверку дубликата делает вызывающий код через checkDuplicate. */
export async function uploadFile(file: File, onProgress?: (frac: number) => void, signal?: AbortSignal): Promise<UploadResult> {
  return uploadXhr(file, onProgress, signal);
}

function uploadXhr(file: File, onProgress?: (frac: number) => void, signal?: AbortSignal): Promise<UploadResult> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/files/upload');
    xhr.withCredentials = true;
    if (signal) {
      if (signal.aborted) { xhr.abort(); reject(new DOMException('Aborted', 'AbortError')); return; }
      signal.addEventListener('abort', () => { xhr.abort(); reject(new DOMException('Aborted', 'AbortError')); }, { once: true });
    }
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total);
    };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText));
        } catch {
          reject(new Error('Некорректный ответ загрузки'));
        }
      } else if (xhr.status === 401) {
        window.location.href = '/login';
        reject(new ApiError('unauthorized', { status: 401 }));
      } else {
        let msg = 'Ошибка ' + xhr.status;
        try {
          const d = JSON.parse(xhr.responseText);
          if (d.error) msg = d.error;
        } catch {
          /* ignore */
        }
        reject(new Error(msg));
      }
    };
    xhr.onerror = () => reject(new Error('Сетевая ошибка загрузки'));
    const fd = new FormData();
    fd.append('file', file, file.name);
    xhr.send(fd);
  });
}
