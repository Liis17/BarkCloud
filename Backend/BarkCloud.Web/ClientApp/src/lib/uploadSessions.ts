import { ApiError, api, apiGet, apiPost } from './api';
import { hashFile } from './fileHasher';

export type UploadSessionStatus = 'uploading' | 'processing' | 'ready' | 'failed' | 'cancelled' | 'expired';

export interface UploadedPart {
  partNumber: number;
  size: number;
}

export interface UploadSession {
  sessionId: string;
  fileId: string;
  status: UploadSessionStatus;
  fileSize: number;
  partSize: number;
  expiresAt: string;
  uploadToken: string | null;
  error: { code: string; message: string } | null;
  uploadedParts: UploadedPart[];
}

export interface CreateUploadSessionInput {
  idempotencyKey: string;
  fileName: string;
  fileSize: number;
  contentType: string;
  sha256: string;
}

export const UPLOAD_PARTS_INCOMPLETE_CODE = '09BF4D7B-7DB9-4284-BDB0-85457B22A589';

export const createUploadSession = (input: CreateUploadSessionInput) =>
  apiPost<UploadSession>('/api/files/uploads', input);

export const getUploadSession = (sessionId: string) =>
  apiGet<UploadSession>(`/api/files/uploads/${encodeURIComponent(sessionId)}`);

export const resumeUploadSession = (sessionId: string) =>
  apiPost<UploadSession>(`/api/files/uploads/${encodeURIComponent(sessionId)}/resume`);

export const completeUploadSession = (sessionId: string) =>
  apiPost<UploadSession>(`/api/files/uploads/${encodeURIComponent(sessionId)}/complete`);

export const cancelUploadSession = (sessionId: string) =>
  api<UploadSession>(`/api/files/uploads/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });

export interface UploadPartRequest {
  sessionId: string;
  token: string;
  partNumber: number;
  start: number;
  end: number;
  total: number;
  body: Blob;
  signal?: AbortSignal;
  onProgress: (loaded: number) => void;
}

export type UploadPartSender = (request: UploadPartRequest) => Promise<void>;

interface UploadSessionRecoveryOperations {
  complete: (sessionId: string) => Promise<UploadSession>;
  resume: (sessionId: string) => Promise<UploadSession>;
  upload: (
    file: File,
    session: UploadSession,
    onProgress?: (fraction: number) => void,
    signal?: AbortSignal,
  ) => Promise<void>;
}

/**
 * Reconcile a race or a lost part acknowledgement before exposing an upload error.
 * Resume always asks Files for the provider's actual part list, so a retry never
 * relies on the browser's previous progress snapshot.
 */
export async function completeUploadWithRecovery(
  file: File,
  initialSession: UploadSession,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
  operations: UploadSessionRecoveryOperations = {
    complete: completeUploadSession,
    resume: resumeUploadSession,
    upload: uploadMissingParts,
  },
): Promise<UploadSession> {
  let session = initialSession;
  for (let attempt = 0; ; attempt++) {
    try {
      return await operations.complete(session.sessionId);
    } catch (error) {
      if (!isUploadPartsIncompleteError(error)
          || attempt >= 1) {
        throw error;
      }

      session = await operations.resume(session.sessionId);
      if (session.status !== 'uploading') return session;
      await operations.upload(file, session, onProgress, signal);
    }
  }
}

export async function uploadMissingParts(
  file: File,
  session: UploadSession,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
  sender: UploadPartSender = uploadPartWithRetry,
): Promise<void> {
  if (!session.uploadToken) throw new Error('Сервер не выдал токен загрузки');
  if (file.size !== session.fileSize) throw new Error('Размер выбранного файла не совпадает');
  if (!Number.isSafeInteger(session.partSize) || session.partSize <= 0)
    throw new Error('Сервер вернул некорректный размер части');

  const partCount = Math.ceil(session.fileSize / session.partSize);
  const completed = new Set(
    session.uploadedParts
      .filter((part) => part.partNumber >= 1 && part.partNumber <= partCount
        && part.size === expectedPartSize(session, part.partNumber))
      .map((part) => part.partNumber),
  );
  let completedBytes = [...completed]
    .reduce((sum, partNumber) => sum + expectedPartSize(session, partNumber), 0);
  onProgress?.(completedBytes / session.fileSize);

  for (let partNumber = 1; partNumber <= partCount; partNumber++) {
    if (completed.has(partNumber)) continue;
    throwIfAborted(signal);
    const start = (partNumber - 1) * session.partSize;
    const endExclusive = Math.min(session.fileSize, start + session.partSize);
    const partSize = endExclusive - start;
    await sender({
      sessionId: session.sessionId,
      token: session.uploadToken,
      partNumber,
      start,
      end: endExclusive - 1,
      total: session.fileSize,
      body: file.slice(start, endExclusive),
      signal,
      onProgress: (loaded) => onProgress?.((completedBytes + Math.min(loaded, partSize)) / session.fileSize),
    });
    completedBytes += partSize;
    onProgress?.(completedBytes / session.fileSize);
  }
}

export async function waitForUploadReady(
  sessionId: string,
  signal?: AbortSignal,
  onPoll?: (session: UploadSession) => void,
): Promise<UploadSession> {
  while (true) {
    throwIfAborted(signal);
    const session = await getUploadSession(sessionId);
    onPoll?.(session);
    if (session.status === 'ready') return session;
    if (session.status === 'failed' || session.status === 'expired' || session.status === 'cancelled')
      throw new UploadSessionTerminalError(session);
    await delay(2_000, signal);
  }
}

/** Одноразовая V2-загрузка для обложек и ручных превью; в постоянную очередь не попадает. */
export async function uploadFile(
  file: File,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<{ fileId: string; name: string }> {
  let session: UploadSession | null = null;
  try {
    const sha256 = await hashFile(file, undefined, signal);
    session = await createUploadSession({
      idempotencyKey: randomId(),
      fileName: file.name,
      fileSize: file.size,
      contentType: file.type || 'application/octet-stream',
      sha256,
    });
    await uploadMissingParts(file, session, onProgress, signal);
    session = await completeUploadWithRecovery(file, session, onProgress, signal);
    if (session.status !== 'ready')
      session = await waitForUploadReady(session.sessionId, signal);
    return { fileId: session.fileId, name: file.name };
  } catch (error) {
    if (signal?.aborted && session?.status === 'uploading')
      void cancelUploadSession(session.sessionId).catch(() => undefined);
    throw error;
  }
}

export class UploadSessionTerminalError extends Error {
  readonly status: UploadSessionStatus;
  constructor(session: UploadSession) {
    super(session.error?.message || `Загрузка завершена со статусом ${session.status}`);
    this.name = 'UploadSessionTerminalError';
    this.status = session.status;
  }
}

class UploadPartHttpError extends ApiError {}

function isUploadPartsIncompleteError(error: unknown): boolean {
  return error instanceof ApiError
    && (error.code?.toUpperCase() === UPLOAD_PARTS_INCOMPLETE_CODE
      || error.message === 'Не все части файла загружены');
}

async function uploadPartWithRetry(request: UploadPartRequest): Promise<void> {
  let lastError: unknown;
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      await uploadPartXhr(request);
      return;
    } catch (error) {
      if ((error as Error).name === 'AbortError') throw error;
      lastError = error;
      const status = (error as ApiError).status;
      if (status !== undefined && status < 500 && status !== 408 && status !== 429) throw error;
      if (attempt < 2) await delay(500 * (2 ** attempt), request.signal);
    }
  }
  throw lastError;
}

function uploadPartXhr(request: UploadPartRequest): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    const cleanup = () => request.signal?.removeEventListener('abort', abort);
    xhr.open('PUT', `/file-upload/${encodeURIComponent(request.sessionId)}/parts/${request.partNumber}`);
    xhr.withCredentials = false;
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.setRequestHeader('Content-Range', `bytes ${request.start}-${request.end}/${request.total}`);
    xhr.setRequestHeader('X-Upload-Token', request.token);
    xhr.upload.onprogress = (event) => request.onProgress(event.loaded);
    xhr.onload = () => {
      cleanup();
      if (xhr.status >= 200 && xhr.status < 300) resolve();
      else reject(new UploadPartHttpError(readXhrError(xhr), { status: xhr.status }));
    };
    xhr.onerror = () => {
      cleanup();
      reject(new Error('Сетевая ошибка загрузки части'));
    };
    xhr.onabort = () => {
      cleanup();
      reject(new DOMException('Aborted', 'AbortError'));
    };
    if (request.signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    request.signal?.addEventListener('abort', abort, { once: true });
    xhr.send(request.body);
  });
}

function expectedPartSize(session: UploadSession, partNumber: number): number {
  const start = (partNumber - 1) * session.partSize;
  return Math.min(session.partSize, session.fileSize - start);
}

function readXhrError(xhr: XMLHttpRequest): string {
  try {
    const payload = JSON.parse(xhr.responseText) as { error?: string };
    return payload.error || `Ошибка ${xhr.status}`;
  } catch {
    return `Ошибка ${xhr.status}`;
  }
}

function delay(milliseconds: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const onAbort = () => {
      window.clearTimeout(timeout);
      signal?.removeEventListener('abort', onAbort);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    const timeout = window.setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, milliseconds);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
}

function randomId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
