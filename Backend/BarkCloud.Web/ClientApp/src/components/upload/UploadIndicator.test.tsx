import React from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UploadIndicator } from './UploadIndicator';
import { UploadManagerProvider, useUploadActions, type AttachOptions } from '../../hooks/useUploadManager';
import { ApiError, apiPost, checkDuplicateHash, pickFiles } from '../../lib/api';
import { hashFile } from '../../lib/fileHasher';
import {
  cancelUploadSession,
  completeUploadSession,
  createUploadSession,
  getUploadSession,
  resumeUploadSession,
  uploadMissingParts,
  waitForUploadReady,
} from '../../lib/uploadSessions';
import { deleteUploadTask, loadUploadTasks, saveUploadTask } from '../../lib/uploadQueueStore';

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
const resumeSessionMock = vi.mocked(resumeUploadSession);
const cancelSessionMock = vi.mocked(cancelUploadSession);
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
  resumeSessionMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'uploading'));
  cancelSessionMock.mockImplementation(async (id) => ({ ...session(id.replace('session-', ''), 'uploading'), status: 'cancelled' }));
  completeSessionMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'ready'));
  waitForReadyMock.mockImplementation(async (id) => session(id.replace('session-', ''), 'ready'));
  uploadPartsMock.mockImplementation(async (_file, _session, onProgress) => { onProgress?.(1); });
  loadTasksMock.mockResolvedValue([]);
  apiPostMock.mockResolvedValue({});
  pickFilesMock.mockResolvedValue([]);
});

afterEach(() => vi.clearAllMocks());

describe('UploadIndicator', () => {
  it.each([false, true])('pauses active transfers and new batches, then resumes existing sessions (StrictMode: %s)', async (strict) => {
    uploadPartsMock.mockImplementationOnce(async (_file, _session, onProgress, signal) => {
      onProgress?.(0.42);
      return new Promise((_, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
      });
    });
    renderBatches([
      [new File(['file'], 'first.txt')],
      [new File(['file'], 'second.txt')],
    ], strict);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(uploadPartsMock).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole('button', { name: 'Приостановить все загрузки' }));
    await screen.findByText('На паузе');
    fireEvent.click(screen.getByText('Добавить партию 2'));
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 10)); });

    expect(screen.getAllByText('На паузе')).toHaveLength(2);
    expect(hashFileMock).toHaveBeenCalledTimes(1);
    expect(cancelSessionMock).not.toHaveBeenCalled();
    expect(screen.queryByText('Отменено')).toBeNull();
    expect(document.querySelector('.upload-popup-bar .bar-fill')?.getAttribute('style')).toContain('width: 21%');

    fireEvent.click(screen.getByRole('button', { name: 'Продолжить все загрузки' }));
    await waitFor(() => expect(screen.getAllByText('Загружен')).toHaveLength(2));
    expect(resumeSessionMock).toHaveBeenCalledWith('session-first.txt');
    expect(createSessionMock).toHaveBeenCalledTimes(2);
    expect(hashFileMock).toHaveBeenCalledTimes(2);
  });

  it('clears active and waiting uploads without restoring records after a late hash result', async () => {
    let finishHash!: (hash: string) => void;
    hashFileMock.mockImplementation(() => new Promise((resolve) => { finishHash = resolve; }));
    renderBatches([duplicateFiles(5)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(hashFileMock).toHaveBeenCalledTimes(4));

    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    const saves = vi.mocked(saveUploadTask).mock.calls.length;
    await act(async () => finishHash('a'.repeat(64)));

    expect(screen.queryByTitle(/Загрузка/)).toBeNull();
    expect(deleteUploadTask).toHaveBeenCalledTimes(5);
    expect(saveUploadTask).toHaveBeenCalledTimes(saves);
    expect(checkDuplicateMock).not.toHaveBeenCalled();
    expect(createSessionMock).not.toHaveBeenCalled();
    expect(hashFileMock).toHaveBeenCalledTimes(4);
  });

  it('clears completed rows while keeping active transfers', async () => {
    uploadPartsMock.mockImplementation(async (file) => {
      if (file.name === 'active.txt') return new Promise(() => undefined);
    });
    renderUploads([new File(['file'], 'done.txt'), new File(['file'], 'active.txt')]);
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByText('Загружен');

    const clearButton = screen.getByRole('button', { name: 'Очистить завершённые' });
    expect(clearButton.textContent).toBe('');
    expect(clearButton.getAttribute('title')).toBe('Очистить завершённые');
    fireEvent.click(clearButton);

    expect(screen.queryByText('done.txt')).toBeNull();
    expect(screen.getByText('active.txt')).toBeTruthy();
    expect(cancelSessionMock).not.toHaveBeenCalled();
  });

  it('pauses hashing and queued files without exceeding concurrency after an immediate resume', async () => {
    hashFileMock.mockImplementation((_file, _onProgress, signal) => new Promise((_, reject) => {
      signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
    }));
    renderBatches([duplicateFiles(6)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(hashFileMock).toHaveBeenCalledTimes(4));
    fireEvent.click(screen.getByRole('button', { name: 'Приостановить все загрузки' }));
    expect(screen.getAllByText('На паузе')).toHaveLength(6);
    hashFileMock.mockResolvedValue('a'.repeat(64));
    fireEvent.click(screen.getByRole('button', { name: 'Продолжить все загрузки' }));

    await waitFor(() => expect(screen.getAllByText('Загружен')).toHaveLength(6));
    expect(createSessionMock).toHaveBeenCalledTimes(6);
    expect(uploadPartsMock).toHaveBeenCalledTimes(6);
  });

  it('closes duplicate questions on pause and asks again after resuming', async () => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(2)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    fireEvent.click(screen.getByRole('button', { name: 'Приостановить все загрузки' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getAllByText('На паузе')).toHaveLength(2);
    expect(createSessionMock).not.toHaveBeenCalled();
    expect(document.querySelector('.upload-popup-bar .bar-fill')?.getAttribute('style')).toContain('width: 0%');

    fireEvent.click(screen.getByRole('button', { name: 'Продолжить все загрузки' }));
    await screen.findByRole('dialog');
    fireEvent.click(screen.getByText('Загрузить все'));
    await waitFor(() => expect(screen.getAllByText('Загружен')).toHaveLength(2));
  });

  it('cancels a session whose create response arrives after clearing the queue', async () => {
    let finishCreate!: (result: ReturnType<typeof session>) => void;
    createSessionMock.mockImplementationOnce(() => new Promise((resolve) => { finishCreate = resolve; }));
    renderUploads([new File(['file'], 'late.txt')]);
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(createSessionMock).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    const saves = vi.mocked(saveUploadTask).mock.calls.length;
    await act(async () => finishCreate(session('late.txt', 'uploading')));

    expect(cancelSessionMock).toHaveBeenCalledWith('session-late.txt');
    expect(saveUploadTask).toHaveBeenCalledTimes(saves);
    expect(uploadPartsMock).not.toHaveBeenCalled();
    expect(apiPostMock).not.toHaveBeenCalled();
  });

  it('removes restored processing tasks without attaching a late ready result', async () => {
    let finishReady!: (result: ReturnType<typeof session>) => void;
    loadTasksMock.mockResolvedValueOnce([persistedTask()]);
    waitForReadyMock.mockImplementationOnce(() => new Promise((resolve) => { finishReady = resolve; }));
    renderUploads([]);
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(waitForReadyMock).toHaveBeenCalledTimes(1));
    expect((screen.getByRole('button', { name: 'Приостановить все загрузки' }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    await act(async () => finishReady(session('restored.txt', 'ready')));

    expect(apiPostMock).not.toHaveBeenCalled();
    expect(cancelSessionMock).not.toHaveBeenCalled();
    expect(deleteUploadTask).toHaveBeenCalledWith('task-1');
    expect(screen.queryByTitle(/Загрузка/)).toBeNull();
  });

  it('does not restore stored tasks when clearing before IndexedDB finishes loading', async () => {
    let finishLoad!: (tasks: Awaited<ReturnType<typeof loadUploadTasks>>) => void;
    loadTasksMock.mockImplementationOnce(() => new Promise((resolve) => { finishLoad = resolve; }));
    renderBatches([duplicateFiles(1)]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    await act(async () => finishLoad([persistedTask()]));

    expect(deleteUploadTask).toHaveBeenCalledWith('task-1');
    expect(screen.queryByTitle(/Загрузка/)).toBeNull();
    expect(getSessionMock).not.toHaveBeenCalled();
  });

  it('requires file reselection for a paused upload after reloading', async () => {
    loadTasksMock.mockResolvedValueOnce([persistedTask({ status: 'paused' })]);
    renderUploads([]);
    fireEvent.click(await screen.findByTitle('Загрузки'));

    await screen.findByText('Нужен файл');
    expect(screen.getByTitle('Выбрать файл')).toBeTruthy();
    expect(resumeSessionMock).not.toHaveBeenCalled();
  });

  it.each([false, true])('cancels the session when clearing transfers (paused: %s)', async (paused) => {
    uploadPartsMock.mockImplementationOnce(async (_file, _session, _onProgress, signal) => new Promise((_, reject) => {
      signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
    }));
    renderBatches([[new File(['file'], 'cancel.txt')], [new File(['file'], 'new.txt')]]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await waitFor(() => expect(uploadPartsMock).toHaveBeenCalledTimes(1));
    if (paused) fireEvent.click(screen.getByRole('button', { name: 'Приостановить все загрузки' }));
    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    await waitFor(() => expect(cancelSessionMock).toHaveBeenCalledWith('session-cancel.txt'));

    fireEvent.click(screen.getByText('Добавить партию 2'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByText('Загружен');
    expect(screen.queryByText('cancel.txt')).toBeNull();
    expect(completeSessionMock).toHaveBeenCalledTimes(1);
  });

  it('clears duplicate prompts and releases workers for the next batch', async () => {
    checkDuplicateMock.mockResolvedValue({ exists: true, locations: [] });
    renderBatches([duplicateFiles(5), [new File(['file'], 'new.txt')]]);
    fireEvent.click(screen.getByText('Добавить партию 1'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByRole('dialog');
    fireEvent.click(screen.getByRole('button', { name: 'Очистить всю очередь' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    checkDuplicateMock.mockResolvedValue({ exists: false, locations: [] });
    fireEvent.click(screen.getByText('Добавить партию 2'));
    fireEvent.click(await screen.findByTitle(/Загрузка/));
    await screen.findByText('Загружен');
    expect(createSessionMock).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

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
