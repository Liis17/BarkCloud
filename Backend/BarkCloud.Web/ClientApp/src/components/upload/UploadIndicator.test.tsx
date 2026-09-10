import React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { UploadIndicator } from './UploadIndicator';
import { UploadManagerProvider, useUploadActions } from '../../hooks/useUploadManager';
import { apiPost, checkDuplicate, uploadFile } from '../../lib/api';

vi.mock('../../lib/api', () => ({
  apiPost: vi.fn(),
  checkDuplicate: vi.fn(),
  uploadFile: vi.fn(),
}));

const apiPostMock = vi.mocked(apiPost);
const checkDuplicateMock = vi.mocked(checkDuplicate);
const uploadFileMock = vi.mocked(uploadFile);

function EnqueueFiles({ files }: { files: File[] }) {
  const { enqueue } = useUploadActions();

  React.useEffect(() => {
    enqueue(files, {});
  }, [enqueue, files]);

  return null;
}

function renderUploads(files: File[]) {
  return render(
    <UploadManagerProvider>
      <EnqueueFiles files={files} />
      <UploadIndicator />
    </UploadManagerProvider>,
  );
}

beforeEach(() => {
  checkDuplicateMock.mockResolvedValue({ exists: false, locations: [] });
  apiPostMock.mockResolvedValue({});
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('UploadIndicator', () => {
  it('places active uploads before completed tasks', async () => {
    const activeProgress = vi.fn();
    uploadFileMock.mockImplementation(async (file, onProgress) => {
      if (file.name === 'done.txt') {
        onProgress?.(1);
        return { fileId: 'done-id', name: file.name };
      }

      onProgress?.(0.42);
      activeProgress(onProgress);
      return new Promise(() => {});
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

    expect(activeProgress).toHaveBeenCalled();
    expect(document.querySelector('.upload-task-bar .bar-fill')?.getAttribute('style')).toContain('width: 42%');
  });

  it('keeps the overall progress at 100% while the file is attaching', async () => {
    uploadFileMock.mockResolvedValue({ fileId: 'file-id', name: 'file.txt' });
    apiPostMock.mockReturnValue(new Promise(() => {}));

    renderUploads([new File(['file'], 'file.txt', { type: 'text/plain' })]);

    const indicator = await screen.findByTitle(/Загрузка/);
    fireEvent.click(indicator);
    await screen.findByText('Прикрепление…');

    expect(document.querySelector('.upload-popup-bar .bar-fill')?.getAttribute('style')).toContain('width: 100%');
  });
});
