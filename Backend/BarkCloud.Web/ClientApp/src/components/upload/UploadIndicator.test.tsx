import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UploadIndicator } from './UploadIndicator';
import { UploadManagerProvider, useUploadActions, type AttachOptions } from '../../hooks/useUploadManager';
import { ApiError, apiPost, checkDuplicateHash, pickFiles } from '../../lib/api';
import { hashFile } from '../../lib/fileHasher';
import {
  completeUploadSession,
  createUploadSession,
  getUploadSession,
  uploadMissingParts,
  waitForUploadReady,
} from '../../lib/uploadSessions';
import { loadUploadTasks } from '../../lib/uploadQueueStore';

vi.mock('../../lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/api')>()),
  apiPost: vi.fn(),
  checkDuplicateHash: vi.fn(),
  pickFiles: vi.fn(),
}));
vi.mock('../../lib/fileHasher', () => ({ hashFile: vi.fn() }));
vi.mock('../../lib/uploadSessions', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../lib/uploadSessions')>()),
  createUploadSession: vi.fn(),
  getUploadSession: vi.fn(),
  resumeUploadSession: vi.fn(),
  completeUploadSession: vi.fn(),
  cancelUploadSession: vi.fn(),
  uploadMissingParts: vi.fn(),
  waitForUploadReady: vi.fn(),
}));
vi.mock('../../lib/uploadQueueStore', () => ({
  loadUploadTasks: vi.fn().mockResolvedValue([]),
  saveUploadTask: vi.fn().mockResolvedValue(undefined),
  deleteUploadTask: vi.fn().mockResolvedValue(undefined),
}));

const apiPostMock = vi.mocked(apiPost);
const checkDuplicateMock = vi.mocked(checkDuplicateHash);
const pickFilesMock = vi.mocked(pickFiles);
const hashFileMock = vi.mocked(hashFile);
const createSessionMock = vi.mocked(createUploadSession);
const getSessionMock = vi.mocked(getUploadSession);
const completeSessionMock = vi.mocked(completeUploadSession);
const uploadPartsMock = vi.mocked(uploadMissingParts);
const waitForReadyMock = vi.mocked(waitForUploadReady);
const loadTasksMock = vi.mocked(loadUploadTasks);
const EMPTY_ATTACH_OPTIONS: AttachOptions = {};

function EnqueueFiles({ files, attachOptions = EMPTY_ATTACH_OPTIONS }: { files: File[]; attachOptions?: AttachOptions }) {
  const { enqueue } = useUploadActions();
  React.useEffect(() => enqueue(files, attachOptions), [enqueue, files, attachOptions]);
  return null;
}

function renderUploads(files: File[], attachOptions: AttachOptions = {}) {
  return render(
    <UploadManagerProvider>
      <EnqueueFiles files={files} attachOptions={attachOptions} />
      <UploadIndicator />
    </UploadManagerProvider>,
  );
}

function EnqueueBatches({ batches }: { batches: File[][] }) {
  const { enqueue } = useUploadActions();
  return <>{batches.map((files, index) => (
    <button key={index} onClick={() => enqueue(files, {})}>Добавить партию {index + 1}</button>
  ))}</>;
}

function renderBatches(batches: File[][], strict = false) {
  const content = (
    <UploadManagerProvider>
      <EnqueueBatches batches={batches} />
      <UploadIndicator />
    </UploadManagerProvider>
  );
  return render(strict ? <React.StrictMode>{content}</React.StrictMode> : content);
}

function duplicateFiles(count: number, prefix = 'duplicate') {
  return Array.from({ length: count }, (_, index) =>
    new File(['file'], `${prefix}-${index}.txt`, { type: 'text/plain' }));
}

beforeEach(() => {
  hashFileMock.mockResolvedValue('a'.repeat(64));
  checkDuplicateMock.mockResolvedValue({ exists: false, locations: [] });
  createSessionMock.mockImplementation(async (input) => session(input.fileName, 'uploading'));
  getSessionMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'processing'));
  completeSessionMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'ready'));
  waitForReadyMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'ready'));
  uploadPartsMock.mockImplementation(async (_file, _session, onProgress) => { onProgress?.(1); });
  loadTasksMock.mockResolvedValue([]);
  apiPostMock.mockResolvedValue({});
  pickFilesMock.mockResolvedValue([]);
});

afterEach(() => vi.clearAllMocks());

describe('UploadIndicator', () => {
  it.each([4, 7])('skips all %i duplicates after one batch decision', async (count) => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(count)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByText('Пропустить все'));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(count));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(checkDuplicateMock).toHaveBeenCalledTimes(count);
    expect(createSessionMock).not.toHaveBeenCalled();
    expect(uploadPartsMock).not.toHaveBeenCalled();
  });

  it('uploads all queued and subsequent duplicates after one batch decision', async () => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(7)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByText('Загрузить все'));

    await waitFor(() => expect(screen.getAllByText('Загружен')).toHaveLength(7));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(createSessionMock).toHaveBeenCalledTimes(7);
    expect(uploadPartsMock).toHaveBeenCalledTimes(7);
  });

  it.each([false, true])('keeps single-file decisions separate (StrictMode: %s)', async (strict) => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(3)], strict);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(3));

    fireEvent.click(screen.getByText('Пропустить'));
    await waitFor(() => expect(screen.getByRole('dialog').textContent).toContain('duplicate-1.txt'));
    expect(screen.getAllByText('Пропущен')).toHaveLength(1);
    fireEvent.click(screen.getByText('Загрузить ещё раз'));
    await waitFor(() => expect(screen.getByRole('dialog').textContent).toContain('duplicate-2.txt'));
    await screen.findByText('Загружен');
    fireEvent.click(screen.getByText('Пропустить'));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(2));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(uploadPartsMock).toHaveBeenCalledTimes(1);
  });

  it('uploads new files in a batch whose duplicates were skipped', async () => {
    hashFileMock.mockImplementation(async (file) => file.name === 'new.txt' ? 'b'.repeat(64) : 'a'.repeat(64));
    checkDuplicateMock.mockImplementation(async (hash) => ({ exists: hash === 'a'.repeat(64), locations: [] }));
    renderBatches([[...duplicateFiles(5), new File(['file'], 'new.txt', { type: 'text/plain' })]]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByText('Пропустить все'));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(5));
    await screen.findByText('Загружен');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(createSessionMock).toHaveBeenCalledTimes(1);
    expect(createSessionMock).toHaveBeenCalledWith(expect.objectContaining({ fileName: 'new.txt' }));
  });

  it('preserves queued questions belonging to a different batch', async () => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(2, 'first'), duplicateFiles(2, 'second')]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByText('Добавить партию 2'));
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByText('Пропустить все'));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(2));
    expect(screen.getByRole('dialog').textContent).toContain('second-0.txt');
    expect(createSessionMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Загрузить все'));
    await waitFor(() => expect(screen.getAllByText('Загружен')).toHaveLength(2));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(uploadPartsMock).toHaveBeenCalledTimes(2);
  });

  it('applies the batch decision to duplicate checks that finish later', async () => {
    let finishCheck!: (result: { exists: boolean; locations: [] }) => void;
    checkDuplicateMock.mockResolvedValueOnce({ exists: true, locations: [] });
    checkDuplicateMock.mockImplementationOnce(() => new Promise((resolve) => { finishCheck = resolve; }));
    renderBatches([duplicateFiles(2)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(2));

    fireEvent.click(screen.getByText('Пропустить все'));
    await act(async () => finishCheck({ exists: true, locations: [] }));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(2));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(createSessionMock).not.toHaveBeenCalled();
  });

  it('skips queued duplicates once in StrictMode', async () => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(7)], true);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    await waitFor(() => expect(checkDuplicateMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByText('Пропустить все'));

    await waitFor(() => expect(screen.getAllByText('Пропущен')).toHaveLength(7));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(checkDuplicateMock).toHaveBeenCalledTimes(7);
    expect(createSessionMock).not.toHaveBeenCalled();
  });

  it('places active uploads before completed tasks', async () => {
    uploadPartsMock.mockImplementation(async (file, _session, onProgress) => {
      if (file.name === 'done.txt') {
        onProgress?.(1);
        return;
      }
      onProgress?.(0.42);
      return new Promise(() => undefined);
    });

    renderUploads([
      new File(['done'], 'done.txt', { type: 'text/plain' }),
      new File(['active'], 'active.txt', { type: 'text/plain' }),
    ]);

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Загружен');

    await waitFor(() => {
      const rows = Array.from(document.querySelectorAll('.upload-task'));
      expect(rows.map((row) => row.querySelector('.upload-task-name')?.textContent)).toEqual([
        'active.txt',
        'done.txt',
      ]);
    });
    expect(document.querySelector('.upload-task-bar .bar-fill')?.getAttribute('style')).toContain('width: 42%');
  });

  it('keeps the overall progress at 100% while the file is attaching', async () => {
    apiPostMock.mockReturnValue(new Promise(() => undefined));
    renderUploads([new File(['file'], 'file.txt', { type: 'text/plain' })]);

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Прикрепление…');

    expect(document.querySelector('.upload-popup-bar .bar-fill')?.getAttribute('style')).toContain('width: 100%');
  });

  it('retries only AttachFile after upload succeeded', async () => {
    apiPostMock
      .mockRejectedValueOnce(new Error('attach failed'))
      .mockResolvedValueOnce({});
    renderUploads([new File(['file'], 'file.txt', { type: 'text/plain' })]);

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Загружен, не прикреплён');
    fireEvent.click(screen.getByTitle('Повторить'));

    await screen.findByText('Загружен');
    expect(uploadPartsMock).toHaveBeenCalledTimes(1);
    expect(apiPostMock).toHaveBeenCalledTimes(2);
    expect(apiPostMock).toHaveBeenLastCalledWith('/api/cloud/attach', expect.objectContaining({
      isUploadRetry: true,
      uploadSessionId: expect.stringMatching(/^session-/),
    }));
  });

  it('persists collection targets and retries post-attach actions after a partial failure', async () => {
    apiPostMock
      .mockResolvedValueOnce({})
      .mockRejectedValueOnce(new Error('album failed'))
      .mockRejectedValueOnce(new ApiError('already attached', {
        code: 'F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B',
        status: 400,
      }))
      .mockResolvedValueOnce({})
      .mockResolvedValueOnce({});

    renderUploads([new File(['file'], 'file.mp3', { type: 'audio/mpeg' })], {
      albumId: 'album-1',
      playlistId: 'playlist-1',
    });

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Загружен, не прикреплён');
    expect(apiPostMock).toHaveBeenNthCalledWith(2, '/api/albums/items/add', {
      album: 'album-1',
      fileIds: [expect.stringMatching(/^file-/)],
    });

    fireEvent.click(screen.getByTitle('Повторить'));
    await screen.findByText('Загружен');
    expect(apiPostMock).toHaveBeenNthCalledWith(3, '/api/cloud/attach', expect.objectContaining({ isUploadRetry: true }));
    expect(apiPostMock).toHaveBeenNthCalledWith(4, '/api/albums/items/add', {
      album: 'album-1',
      fileIds: [expect.stringMatching(/^file-/)],
    });
    expect(apiPostMock).toHaveBeenNthCalledWith(5, '/api/music/playlists/tracks/add', {
      playlistId: 'playlist-1',
      fileIds: [expect.stringMatching(/^file-/)],
    });
  });

  it('treats already-attached replay as success', async () => {
    apiPostMock.mockRejectedValueOnce(new ApiError('already attached', {
      code: 'F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B',
      status: 400,
    }));
    renderUploads([new File(['file'], 'file.txt', { type: 'text/plain' })]);

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);

    await screen.findByText('Загружен');
    expect(uploadPartsMock).toHaveBeenCalledTimes(1);
  });

  it('runs at most four files concurrently', async () => {
    hashFileMock.mockImplementation(() => new Promise(() => undefined));
    renderUploads(Array.from({ length: 5 }, (_, index) =>
      new File([`file-${index}`], `file-${index}.txt`, { type: 'text/plain' })));

    await waitFor(() => expect(hashFileMock).toHaveBeenCalledTimes(4));
    expect(hashFileMock).not.toHaveBeenCalledTimes(5);
  });

  it('restores processing tasks from IndexedDB and attaches only after ready', async () => {
    loadTasksMock.mockResolvedValueOnce([{
      version: 1,
      id: 'task-1',
      idempotencyKey: 'key-1',
      batchId: 'batch-1',
      fileName: 'restored.txt',
      fileSize: 4,
      fileType: 'text/plain',
      lastModified: 123,
      sha256: 'a'.repeat(64),
      sessionId: 'session-restored.txt',
      fileId: 'file-restored.txt',
      status: 'processing',
      attachOptions: {},
      progress: 1,
      error: null,
      createdAt: 1,
      updatedAt: 2,
    }]);

    render(
      <UploadManagerProvider>
        <UploadIndicator />
      </UploadManagerProvider>,
    );

    const indicator = await screen.findByTitle('Загрузки');
    fireEvent.click(indicator);
    await screen.findByText('Загружен');

    expect(getSessionMock).toHaveBeenCalledWith('session-restored.txt');
    expect(waitForReadyMock).toHaveBeenCalledWith('session-restored.txt', expect.any(AbortSignal));
    expect(uploadPartsMock).not.toHaveBeenCalled();
    expect(apiPostMock).toHaveBeenCalledTimes(1);
  });

  it('does not offer cancellation after multipart entered processing', async () => {
    loadTasksMock.mockResolvedValueOnce([{
      version: 1,
      id: 'task-1',
      idempotencyKey: 'key-1',
      batchId: 'batch-1',
      fileName: 'processing.txt',
      fileSize: 4,
      fileType: 'text/plain',
      lastModified: 123,
      sha256: 'a'.repeat(64),
      sessionId: 'session-processing.txt',
      fileId: 'file-processing.txt',
      status: 'processing',
      attachOptions: {},
      progress: 1,
      error: null,
      createdAt: 1,
      updatedAt: 2,
    }]);
    waitForReadyMock.mockReturnValueOnce(new Promise(() => undefined));

    render(
      <UploadManagerProvider>
        <UploadIndicator />
      </UploadManagerProvider>,
    );

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Обработка…');

    expect(screen.queryByTitle('Отменить')).toBeNull();
  });

  it('rejects a reselected file with a different hash even when Create response was lost', async () => {
    loadTasksMock.mockResolvedValueOnce([persistedTask({
      status: 'needs_file',
      sessionId: null,
      fileId: null,
      fileName: 'original.txt',
    })]);
    pickFilesMock.mockResolvedValueOnce([
      new File(['evil'], 'original.txt', { type: 'text/plain', lastModified: 123 }),
    ]);
    hashFileMock.mockResolvedValueOnce('b'.repeat(64));

    render(
      <UploadManagerProvider>
        <UploadIndicator />
      </UploadManagerProvider>,
    );

    fireEvent.click(await screen.findByTitle('Загрузки'));
    fireEvent.click(await screen.findByTitle('Выбрать файл'));

    await screen.findByText('Выбран другой файл: SHA-256 не совпадает');
    expect(createSessionMock).not.toHaveBeenCalled();
  });

  it('asks for the file when a restored processing session failed permanently', async () => {
    loadTasksMock.mockResolvedValueOnce([persistedTask({ status: 'processing' })]);
    getSessionMock.mockResolvedValueOnce({
      ...session('restored.txt', 'processing'),
      status: 'failed',
      error: { code: 'integrity_mismatch', message: 'SHA-256 не совпадает' },
    });

    render(
      <UploadManagerProvider>
        <UploadIndicator />
      </UploadManagerProvider>,
    );

    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByText('Нужен файл');
    expect(screen.getByTitle('Выбрать файл')).toBeTruthy();
  });
});

function persistedTask(overrides: Record<string, unknown> = {}) {
  return {
    version: 1 as const,
    id: 'task-1',
    idempotencyKey: 'key-1',
    batchId: 'batch-1',
    fileName: 'restored.txt',
    fileSize: 4,
    fileType: 'text/plain',
    lastModified: 123,
    sha256: 'a'.repeat(64),
    sessionId: 'session-restored.txt',
    fileId: 'file-restored.txt',
    status: 'processing' as const,
    attachOptions: {},
    progress: 1,
    error: null,
    createdAt: 1,
    updatedAt: 2,
    ...overrides,
  };
}

function session(fileName: string, status: 'uploading' | 'processing' | 'ready') {
  return {
    sessionId: `session-${fileName}`,
    fileId: `file-${fileName}`,
    status,
    fileSize: 4,
    partSize: 16 * 1024 * 1024,
    expiresAt: '',
    uploadToken: status === 'uploading' ? 'token' : null,
    error: null,
    uploadedParts: [],
  };
}
