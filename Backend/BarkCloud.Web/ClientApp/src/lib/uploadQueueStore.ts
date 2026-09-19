export type PersistedUploadStatus =
  | 'hashing'
  | 'checking'
  | 'needs_file'
  | 'uploading'
  | 'processing'
  | 'attaching'
  | 'uploaded_not_attached'
  | 'done'
  | 'failed'
  | 'skipped';

export interface PersistedUploadTask {
  version: 1;
  id: string;
  idempotencyKey: string;
  batchId: string;
  fileName: string;
  fileSize: number;
  fileType: string;
  lastModified: number;
  sha256: string | null;
  sessionId: string | null;
  fileId: string | null;
  status: PersistedUploadStatus;
  attachOptions: {
    dir?: string;
    routeByMediaKind?: boolean;
    albumId?: string;
    playlistId?: string;
  };
  progress: number;
  error: string | null;
  createdAt: number;
  updatedAt: number;
}

const DB_NAME = 'barkcloud-upload-queue';
const STORE_NAME = 'tasks';
const DB_VERSION = 1;

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME))
        request.result.createObjectStore(STORE_NAME, { keyPath: 'id' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB unavailable'));
  });
}

export async function saveUploadTask(task: PersistedUploadTask): Promise<void> {
  const safe: PersistedUploadTask = {
    version: 1,
    id: task.id,
    idempotencyKey: task.idempotencyKey,
    batchId: task.batchId,
    fileName: task.fileName,
    fileSize: task.fileSize,
    fileType: task.fileType,
    lastModified: task.lastModified,
    sha256: task.sha256,
    sessionId: task.sessionId,
    fileId: task.fileId,
    status: task.status,
    attachOptions: { ...task.attachOptions },
    progress: task.progress,
    error: task.error,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
  };
  const db = await openDatabase();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(STORE_NAME, 'readwrite');
      const store = transaction.objectStore(STORE_NAME);
      const get = store.get(safe.id);
      get.onsuccess = () => {
        const current = get.result as PersistedUploadTask | undefined;
        if (!current || current.updatedAt <= safe.updatedAt) store.put(safe);
      };
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted'));
    });
  } finally {
    db.close();
  }
}

export async function loadUploadTasks(): Promise<PersistedUploadTask[]> {
  const db = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const request = db.transaction(STORE_NAME, 'readonly').objectStore(STORE_NAME).getAll();
      request.onsuccess = () => resolve(
        (request.result as PersistedUploadTask[]).filter((task) => task.version === 1),
      );
      request.onerror = () => reject(request.error);
    });
  } finally {
    db.close();
  }
}

export async function deleteUploadTask(id: string): Promise<void> {
  await runTransaction('readwrite', (store) => store.delete(id));
}

export async function clearUploadTasks(): Promise<void> {
  await runTransaction('readwrite', (store) => store.clear());
}

async function runTransaction(
  mode: IDBTransactionMode,
  action: (store: IDBObjectStore) => IDBRequest,
): Promise<void> {
  const db = await openDatabase();
  try {
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(STORE_NAME, mode);
      action(transaction.objectStore(STORE_NAME));
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
      transaction.onabort = () => reject(transaction.error ?? new Error('IndexedDB transaction aborted'));
    });
  } finally {
    db.close();
  }
}
