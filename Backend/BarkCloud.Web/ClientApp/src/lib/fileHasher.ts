type HashWorkerResponse =
  | { type: 'progress'; id: string; progress: number }
  | { type: 'done'; id: string; hash: string }
  | { type: 'error'; id: string; name: string; message: string };

export function hashFile(
  file: File,
  onProgress?: (fraction: number) => void,
  signal?: AbortSignal,
): Promise<string> {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const worker = new Worker(new URL('./sha256.worker.ts', import.meta.url), { type: 'module' });
    const cleanup = () => {
      signal?.removeEventListener('abort', abort);
      worker.terminate();
    };
    const abort = () => {
      worker.postMessage({ type: 'cancel', id });
      cleanup();
      reject(new DOMException('Aborted', 'AbortError'));
    };

    if (signal?.aborted) {
      abort();
      return;
    }
    signal?.addEventListener('abort', abort, { once: true });
    worker.onmessage = (event: MessageEvent<HashWorkerResponse>) => {
      const response = event.data;
      if (response.id !== id) return;
      if (response.type === 'progress') {
        onProgress?.(response.progress);
      } else if (response.type === 'done') {
        cleanup();
        resolve(response.hash);
      } else {
        cleanup();
        const error = new Error(response.message);
        error.name = response.name;
        reject(error);
      }
    };
    worker.onerror = () => {
      cleanup();
      reject(new Error('Не удалось запустить потоковое хэширование'));
    };
    worker.postMessage({ type: 'hash', id, file });
  });
}
