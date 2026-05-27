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

/** Загрузка одного файла с прогрессом (XHR — fetch не отдаёт upload-progress). */
export function uploadFile(file: File, onProgress?: (frac: number) => void): Promise<UploadResult> {
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
