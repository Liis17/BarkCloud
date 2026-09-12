import 'fake-indexeddb/auto';
import { beforeEach, describe, expect, it } from 'vitest';
import { clearUploadTasks, loadUploadTasks, saveUploadTask } from './uploadQueueStore';

describe('uploadQueueStore', () => {
  beforeEach(async () => {
    await clearUploadTasks();
  });

  it('round-trips recovery metadata and strips volatile secrets', async () => {
    await saveUploadTask({
      version: 1,
      id: 'task-1',
      idempotencyKey: 'key-1',
      batchId: 'batch-1',
      fileName: 'movie.mp4',
      fileSize: 42,
      fileType: 'video/mp4',
      lastModified: 123,
      sha256: 'abc',
      sessionId: 'session-1',
      fileId: 'file-1',
      status: 'uploading',
      attachOptions: { routeByMediaKind: true },
      progress: 0.5,
      error: null,
      createdAt: 1,
      updatedAt: 2,
      uploadToken: 'must-not-be-persisted',
      file: new File(['secret'], 'movie.mp4'),
      abortCtrl: new AbortController(),
    } as never);

    const [stored] = await loadUploadTasks();
    expect(stored).toMatchObject({ id: 'task-1', sessionId: 'session-1', progress: 0.5 });
    expect(stored).not.toHaveProperty('uploadToken');
    expect(stored).not.toHaveProperty('file');
    expect(stored).not.toHaveProperty('abortCtrl');
  });

  it('does not let an older asynchronous snapshot overwrite newer state', async () => {
    const base = {
      version: 1 as const,
      id: 'task-1',
      idempotencyKey: 'key-1',
      batchId: 'batch-1',
      fileName: 'movie.mp4',
      fileSize: 42,
      fileType: 'video/mp4',
      lastModified: 123,
      sha256: 'abc',
      sessionId: 'session-1',
      fileId: 'file-1',
      attachOptions: {},
      progress: 1,
      error: null,
      createdAt: 1,
    };
    await saveUploadTask({ ...base, status: 'processing', updatedAt: 10 });
    await saveUploadTask({ ...base, status: 'uploading', updatedAt: 9 });

    const [stored] = await loadUploadTasks();
    expect(stored.status).toBe('processing');
    expect(stored.updatedAt).toBe(10);
  });
});
