import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FilesPage } from './FilesPage';

const mocks = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  enqueue: vi.fn(),
}));

let intersectionCallback: IntersectionObserverCallback | null = null;

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

afterEach(() => {
  vi.clearAllMocks();
  vi.unstubAllGlobals();
  intersectionCallback = null;
});

beforeEach(() => {
  vi.stubGlobal('IntersectionObserver', vi.fn().mockImplementation((callback: IntersectionObserverCallback) => {
    intersectionCallback = callback;
    return {
      observe: vi.fn(),
      disconnect: vi.fn(),
    } as unknown as IntersectionObserver;
  }));
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

function triggerDirectoryEnd() {
  intersectionCallback?.([{ isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver);
}

function LocationProbe() {
  const location = useLocation();
  const navigate = useNavigate();
  return <>
    <output data-testid="route-location">{JSON.stringify({ pathname: location.pathname, search: location.search, state: location.state })}</output>
    <button onClick={() => navigate(-1)}>Browser Back</button>
    <button onClick={() => navigate(1)}>Browser Forward</button>
  </>;
}

function renderFilesHistory(initialEntries: Array<string | { pathname: string; search?: string; state?: unknown }>) {
  render(
    <MemoryRouter initialEntries={initialEntries}>
      <LocationProbe />
      <Routes>
        <Route path="/files" element={<FilesPage />} />
        <Route path="/photos" element={<div>Фотографии</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

function currentRoute() {
  return JSON.parse(screen.getByTestId('route-location').textContent || '{}');
}

function mockDirectoryListings(listings: Record<string, unknown>) {
  mocks.apiGet.mockImplementation((url: string) => {
    if (url.startsWith('/api/cloud/list')) {
      const dir = new URL(url, 'http://localhost').searchParams.get('dir') || '';
      return Promise.resolve(listings[dir] || { dirs: [], files: [] });
    }
    if (url === '/api/albums') return Promise.resolve({ albums: [] });
    if (url === '/api/dynamic-folders') return Promise.resolve({ folders: [] });
    return Promise.resolve({});
  });
}

describe('FilesPage folder history', () => {
  it('supports browser Back and Forward across folders and the previous page', async () => {
    mockDirectoryListings({
      '': { dirs: [{ id: 'dir-a', name: 'Папка А', updatedAt: '2026-01-01T00:00:00Z' }], files: [] },
      'dir-a': { dirs: [{ id: 'dir-b', name: 'Папка Б', updatedAt: '2026-01-01T00:00:00Z' }], files: [] },
      'dir-b': { dirs: [], files: [fileEntry('entry-b', 'Файл B')] },
    });
    renderFilesHistory(['/photos', '/files']);

    fireEvent.click(await screen.findByText('Папка А'));
    fireEvent.click(await screen.findByText('Папка Б'));
    expect(await screen.findByText('Файл B')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));
    expect(await screen.findByText('Папка Б')).toBeTruthy();
    expect(screen.queryByText('Файл B')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));
    expect(await screen.findByText('Папка А')).toBeTruthy();
    expect(screen.queryByText('Папка Б')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));
    expect(await screen.findByText('Фотографии')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Browser Forward' }));
    expect(await screen.findByText('Папка А')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Browser Forward' }));
    expect(await screen.findByText('Папка Б')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Browser Forward' }));
    expect(await screen.findByText('Файл B')).toBeTruthy();
  });

  it('pushes breadcrumb navigation into browser history', async () => {
    mockDirectoryListings({
      '': { dirs: [{ id: 'dir-a', name: 'Папка А', updatedAt: '2026-01-01T00:00:00Z' }], files: [] },
      'dir-a': { dirs: [{ id: 'dir-b', name: 'Папка Б', updatedAt: '2026-01-01T00:00:00Z' }], files: [] },
      'dir-b': { dirs: [], files: [fileEntry('entry-b', 'Файл B')] },
    });
    renderFilesHistory(['/files']);

    fireEvent.click(await screen.findByText('Папка А'));
    fireEvent.click(await screen.findByText('Папка Б'));
    expect(await screen.findByText('Файл B')).toBeTruthy();

    fireEvent.click(screen.getByText('Папка А'));
    expect(await screen.findByText('Папка Б')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));
    expect(await screen.findByText('Файл B')).toBeTruthy();
  });

  it('does not add a history entry when the root breadcrumb is already current', async () => {
    mockDirectoryListings({ '': { dirs: [], files: [] } });
    renderFilesHistory(['/photos', '/files']);

    await screen.findByText('Папка пуста');
    fireEvent.click(document.querySelector('.files-bar .breadcrumb > a')!);
    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));

    expect(await screen.findByText('Фотографии')).toBeTruthy();
  });

  it('keeps a direct directory query as the parent entry when opening a child', async () => {
    mockDirectoryListings({
      'dir-d': { dirs: [{ id: 'dir-child', name: 'Дочерняя', updatedAt: '2026-01-01T00:00:00Z' }], files: [] },
      'dir-child': { dirs: [], files: [] },
    });
    renderFilesHistory(['/files?dir=dir-d']);

    fireEvent.click(await screen.findByText('Дочерняя'));
    expect(currentRoute()).toEqual({
      pathname: '/files',
      search: '',
      state: { stack: [{ id: 'dir-d', name: 'Папка' }, { id: 'dir-child', name: 'Дочерняя' }] },
    });
    expect(mocks.apiGet).toHaveBeenCalledWith('/api/cloud/list?dir=dir-child&limit=50');

    fireEvent.click(screen.getByRole('button', { name: 'Browser Back' }));
    expect(await screen.findByText('Папка')).toBeTruthy();
    expect(currentRoute()).toMatchObject({ pathname: '/files', search: '?dir=dir-d' });
    expect(mocks.apiGet).toHaveBeenCalledWith('/api/cloud/list?dir=dir-d&limit=50');
  });

  it('uses incoming stack and initial selection without copying selectEntryId into child navigation', async () => {
    mockDirectoryListings({
      'dir-a': {
        dirs: [{ id: 'dir-b', name: 'Папка Б', updatedAt: '2026-01-01T00:00:00Z' }],
        files: [fileEntry('entry-selected', 'Выбранный файл')],
      },
      'dir-b': { dirs: [], files: [fileEntry('entry-b', 'Файл B')] },
    });
    renderFilesHistory([{
      pathname: '/files',
      state: { stack: [{ id: 'dir-a', name: 'Папка А' }], selectEntryId: 'entry-selected' },
    }]);

    const selectedRow = (await screen.findAllByText('Выбранный файл'))[0].closest('tr')!;
    await waitFor(() => expect(selectedRow.className).toContain('selected'));
    fireEvent.click(screen.getByText('Папка Б'));
    expect(await screen.findByText('Файл B')).toBeTruthy();
    expect(currentRoute().state).toEqual({
      stack: [{ id: 'dir-a', name: 'Папка А' }, { id: 'dir-b', name: 'Папка Б' }],
    });
    expect(currentRoute().state).not.toHaveProperty('selectEntryId');
  });
});

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
    await act(async () => triggerDirectoryEnd());

    expect(await screen.findByText('b.txt')).toBeTruthy();
    expect(firstRow.className).toContain('selected');
    expect(listUrls).toContain('/api/cloud/list?dir=&limit=50&cursorName=a.txt&cursorId=entry-1');
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).toBeNull();
  });

  it('показывает пункт «Свойства» в контекстном меню папки', async () => {
    render(<MemoryRouter initialEntries={['/files']}><FilesPage /></MemoryRouter>);

    const folderRow = (await screen.findByText('Документы')).closest('tr')!;
    fireEvent.contextMenu(folderRow);

    expect(await screen.findByText('Свойства')).toBeTruthy();
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
