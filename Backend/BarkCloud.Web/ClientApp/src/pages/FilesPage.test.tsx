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

function fileEntry(entryId: string, name: string) {
  return {
    entryId,
    fileId: `file-${entryId}`,
    directoryId: '',
    name,
    createdAt: '2026-01-01T00:00:00Z',
    media: null,
  };
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

describe('FilesPage directory pagination', () => {
  it('loads the next page with the cursor and keeps already selected files', async () => {
    const first = fileEntry('entry-1', 'a.txt');
    const second = fileEntry('entry-2', 'b.txt');
    const listUrls: string[] = [];
    mocks.apiGet.mockImplementation((url: string) => {
      if (url.startsWith('/api/cloud/list')) {
        listUrls.push(url);
        return Promise.resolve(url.includes('cursorName=')
          ? { dirs: [], files: [second], nextCursorName: null, nextCursorId: '' }
          : { dirs: [], files: [first], nextCursorName: 'a.txt', nextCursorId: 'entry-1' });
      }
      if (url === '/api/albums') return Promise.resolve({ albums: [] });
      if (url === '/api/dynamic-folders') return Promise.resolve({ folders: [] });
      return Promise.resolve({});
    });

    render(<MemoryRouter initialEntries={['/files']}><FilesPage /></MemoryRouter>);

    const firstRow = (await screen.findByText('a.txt')).closest('tr')!;
    fireEvent.click(firstRow);
    fireEvent.click(await screen.findByRole('button', { name: 'Показать ещё' }));

    expect(await screen.findByText('b.txt')).toBeTruthy();
    expect(firstRow.className).toContain('selected');
    expect(listUrls).toContain('/api/cloud/list?dir=&limit=50&cursorName=a.txt&cursorId=entry-1');
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).toBeNull();
  });

  it('resets the folder cursor after navigating into a subdirectory', async () => {
    const listUrls: string[] = [];
    mocks.apiGet.mockImplementation((url: string) => {
      if (url.startsWith('/api/cloud/list')) {
        listUrls.push(url);
        if (url.includes('dir=dir-1')) return Promise.resolve({ dirs: [], files: [fileEntry('entry-2', 'inside.txt')] });
        return Promise.resolve({
          dirs: [{ id: 'dir-1', name: 'Документы', updatedAt: '2026-01-01T00:00:00Z' }],
          files: [fileEntry('entry-1', 'root.txt')],
          nextCursorName: 'root.txt',
          nextCursorId: 'entry-1',
        });
      }
      if (url === '/api/albums') return Promise.resolve({ albums: [] });
      if (url === '/api/dynamic-folders') return Promise.resolve({ folders: [] });
      return Promise.resolve({});
    });

    render(<MemoryRouter initialEntries={['/files']}><FilesPage /></MemoryRouter>);
    fireEvent.click(await screen.findByText('Документы'));

    expect(await screen.findByText('inside.txt')).toBeTruthy();
    expect(listUrls).toContain('/api/cloud/list?dir=dir-1&limit=50');
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).toBeNull();
  });
});
