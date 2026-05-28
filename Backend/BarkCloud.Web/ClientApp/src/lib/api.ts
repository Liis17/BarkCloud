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
  deduped?: boolean;
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

/** Загрузка файла с предварительной дедупликацией по хешу: если файл уже в хранилище —
 *  возвращаем существующий fileId, не передавая байты. Иначе — обычный XHR-аплоад. */
export async function uploadFile(file: File, onProgress?: (frac: number) => void): Promise<UploadResult> {
  const hash = await sha256Hex(file);
  if (hash) {
    try {
      const r = await apiPost<{ fileId: string }>('/api/files/check-hash', { hash });
      if (r.fileId) {
        onProgress?.(1);
        return { fileId: r.fileId, name: file.name, deduped: true };
      }
    } catch {
      /* проверка не критична — грузим обычным путём */
    }
  }
  return uploadXhr(file, onProgress);
}

/** Передача байтов файла через XHR (прогресс — fetch не отдаёт upload-progress). */
function uploadXhr(file: File, onProgress?: (frac: number) => void): Promise<UploadResult> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/files/upload');
    xhr.withCredentials = true;
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
