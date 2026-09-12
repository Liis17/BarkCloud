import { hashBlobInChunks } from './sha256Core';

type WorkerRequest =
  | { type: 'hash'; id: string; file: File }
  | { type: 'cancel'; id: string };

type WorkerResponse =
  | { type: 'progress'; id: string; progress: number }
  | { type: 'done'; id: string; hash: string }
  | { type: 'error'; id: string; name: string; message: string };

const controllers = new Map<string, AbortController>();
const scope = self as unknown as {
  onmessage: ((event: MessageEvent<WorkerRequest>) => void) | null;
  postMessage: (message: WorkerResponse) => void;
};

scope.onmessage = (event) => {
  const request = event.data;
  if (request.type === 'cancel') {
    controllers.get(request.id)?.abort();
    return;
  }

  const controller = new AbortController();
  controllers.set(request.id, controller);
  void hashBlobInChunks(request.file, {
    signal: controller.signal,
    onProgress: (progress) => scope.postMessage({ type: 'progress', id: request.id, progress }),
  }).then(
    (hash) => scope.postMessage({ type: 'done', id: request.id, hash }),
    (error: Error) => scope.postMessage({
      type: 'error',
      id: request.id,
      name: error.name,
      message: error.message,
    }),
  ).finally(() => controllers.delete(request.id));
};
