import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FilesPage } from './FilesPage';

const mocks = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  enqueue: vi.fn(),
}));

vi.mock('../lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/api')>()),
  apiGet: mocks.apiGet,
  apiPost: mocks.apiPost,
}));
vi.mock('../hooks/useUploadManager', () => ({
  useUploadActions: () => ({ enqueue: mocks.enqueue, attachVersion: 0 }),
}));
vi.mock('../hooks/useAudioPlayer', () => ({
  useAudioPlayer: () => ({ playQueue: vi.fn() }),
}));
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));

afterEach(() => vi.clearAllMocks());

beforeEach(() => {
  mocks.apiGet.mockImplementation((url: string) => {
    if (url.startsWith('/api/cloud/list')) {
      return Promise.resolve({
        dirs: [{ id: 'dir-1', name: 'Документы', updatedAt: '2026-01-01T00:00:00Z' }],
        files: [],
      });
    }
    if (url === '/api/albums') return Promise.resolve({ albums: [] });
    if (url === '/api/dynamic-folders') return Promise.resolve({ folders: [] });
    return Promise.resolve({});
  });
  mocks.apiPost.mockResolvedValue({});
});

function transfer(fileName = 'notes.txt') {
  return {
    types: ['Files'],
    files: [new File(['content'], fileName, { type: 'text/plain' })],
  } as unknown as DataTransfer;
}

describe('FilesPage drag-and-drop', () => {
  it('shows the current folder and uploads a drop there', async () => {
    const { container } = render(
      <MemoryRouter initialEntries={[{ pathname: '/files', state: { stack: [{ id: 'current-1', name: 'Текущая' }] } }]}>
        <FilesPage />
      </MemoryRouter>,
    );
    const root = container.querySelector('.files-shell')!;
    const dataTransfer = transfer();

    fireEvent.dragEnter(root, { dataTransfer });
    expect(await screen.findByText('Файлы будут загружены в папку «Текущая»')).toBeTruthy();
    fireEvent.drop(root, { dataTransfer });

    expect(mocks.enqueue).toHaveBeenCalledWith(expect.any(Array), { dir: 'current-1' });
  });

  it('uses a hovered folder as the target without opening it', async () => {
    render(<MemoryRouter initialEntries={['/files']}><FilesPage /></MemoryRouter>);
    const folderName = await screen.findByText('Документы');
    const row = folderName.closest('tr')!;
    const dataTransfer = transfer('report.pdf');

    fireEvent.dragEnter(row, { dataTransfer });
    await waitFor(() => expect(row.className).toContain('drop-target'));
    expect(await screen.findByText('Файлы будут загружены в папку «Документы»')).toBeTruthy();
    fireEvent.drop(row, { dataTransfer });

    expect(mocks.enqueue).toHaveBeenCalledWith(expect.any(Array), { dir: 'dir-1' });
    expect(mocks.apiGet.mock.calls.some(([url]) => url === '/api/cloud/list?dir=dir-1')).toBe(false);
  });
});
