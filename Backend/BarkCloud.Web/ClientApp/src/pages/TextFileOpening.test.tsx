import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FilesPage } from './FilesPage';
import { FavoritesPage } from './FavoritesPage';
import { SearchPage } from './SearchPage';
import { SharedPage } from './SharedPage';
import { PublicViewPage } from './PublicViewPage';
import { PublicFolderPage } from './PublicFolderPage';
import { DynamicFolderDetail } from '../components/dynamic-folders/DynamicFolderDetail';
import { SharedFolderModal } from '../components/ui/SharedFolderModal';
import type { DynamicFolder } from '../lib/types';

vi.mock('../hooks/useUploadManager', () => ({ useUploadActions: () => ({ enqueue: vi.fn(), attachVersion: 0 }) }));
vi.mock('../hooks/useAudioPlayer', () => ({ useAudioPlayer: () => ({ playQueue: vi.fn() }) }));
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));
vi.mock('../components/text/TextSource', () => ({ default: ({ text }: { text: string }) => <pre aria-label="Содержимое файла">{text}</pre> }));

const card = { id: 'file-1', name: 'notes.txt', ext: 'txt', kind: 'document', iconKind: 'doc', size: 4, sizeLabel: '4 Б', previews: [], width: 0, height: 0, createdAt: null, uploadedAt: null };
const entry = { entryId: 'entry-1', fileId: card.id, directoryId: '', name: card.name, media: card, createdAt: null };
const publicFile = { fileId: card.id, name: card.name, mediaKind: 'document', downloadUrl: 'https://files.example/download/temp', previewUrl: '', fileSize: 4, imageWidth: 0, imageHeight: 0 };
const hit = { kind: 'file', id: entry.entryId, fileId: card.id, entryId: entry.entryId, title: card.name, subtitle: '', previewUrl: '', mediaKind: 'document', favorite: false, matchField: 'name', matchValue: '', createdAt: null, size: 4 };
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  vi.stubGlobal('IntersectionObserver', vi.fn(() => ({ observe: vi.fn(), disconnect: vi.fn() })));
  vi.stubGlobal('open', vi.fn());
  fetchMock = vi.fn(async (url: string) => {
    if (url.includes('/text')) return { ok: true, arrayBuffer: async () => new TextEncoder().encode('text').buffer };
    let body: unknown = {};
    if (url.startsWith('/api/cloud/list')) body = { dirs: [], files: [entry] };
    else if (url === '/api/cloud/favorites') body = { items: [card] };
    else if (url.startsWith('/api/dynamic-folders/items')) body = { items: [{ ...card, entryIds: [entry.entryId], entryNames: [card.name], entriesCount: 1 }] };
    else if (url === '/api/albums') body = { albums: [] };
    else if (url === '/api/dynamic-folders') body = { folders: [] };
    else if (url === '/api/shared/with-me') body = { items: [{ grantId: 'grant-1', file: card, sharedAt: null, owner: { id: 1, username: 'owner' } }] };
    else if (url.startsWith('/api/shared/dir')) body = { found: true, directoryId: 'dir', name: 'Folder', subdirs: [], files: [publicFile] };
    else if (url.startsWith('/s/token/info')) body = { ...publicFile, found: true, downloadPath: '/s/token' };
    else if (url.startsWith('/f/token/list')) body = { found: true, folderName: 'Folder', currentDir: '', currentName: 'Folder', subdirs: [], files: [publicFile] };
    else if (url.startsWith('/api/search')) body = { query: 'notes', sections: [{ key: 'files', items: [hit], hasMore: false }] };
    else body = { items: [] };
    return { ok: true, text: async () => JSON.stringify(body), json: async () => body };
  });
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks(); });

async function expectViewer(url: string) {
  expect(await screen.findByLabelText('Содержимое файла')).toHaveProperty('textContent', 'text');
  expect(screen.getByRole('dialog', { name: 'notes.txt' })).toBeTruthy();
  expect(fetchMock.mock.calls.map(([path]) => path)).toContain(url);
  expect(window.open).not.toHaveBeenCalled();
}

describe('text file opening across Web', () => {
  it('opens Files by double click and from the inspector', async () => {
    const view = render(<MemoryRouter><FilesPage /></MemoryRouter>);
    const row = (await screen.findByText('notes.txt')).closest('tr')!;
    fireEvent.doubleClick(row);
    await expectViewer('/api/files/text?id=file-1');
    fireEvent.click(within(screen.getByRole('dialog')).getAllByRole('button', { name: 'Закрыть' })[0]);
    expect(document.activeElement).toBe(row);
    fireEvent.click(row);
    const inspector = view.container.querySelector('.files-inspector')!;
    fireEvent.click(within(inspector as HTMLElement).getByRole('button', { name: 'Открыть' }));
    await expectViewer('/api/files/text?id=file-1');
  });
  it('opens Files from the context menu', async () => {
    render(<MemoryRouter><FilesPage /></MemoryRouter>);
    const row = (await screen.findByText('notes.txt')).closest('tr')!;
    fireEvent.contextMenu(row);
    // Pointer mousedown on the menu can blur its noninteractive opener.
    row.blur();
    fireEvent.click(screen.getByText('Открыть'));
    await expectViewer('/api/files/text?id=file-1');
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(document.activeElement).toBe(row);
  });
  it('opens Favorites', async () => {
    render(<MemoryRouter><FavoritesPage /></MemoryRouter>);
    const card = (await screen.findByText('notes.txt')).closest('.photo')!;
    fireEvent.click(card);
    await expectViewer('/api/files/text?id=file-1');
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(document.activeElement).toBe(card);
  });
  it('opens smart folders', async () => {
    const folder = { id: 'smart', name: 'Smart', viewMode: 1 } as DynamicFolder;
    render(<MemoryRouter><DynamicFolderDetail folder={folder} onBack={() => {}} onChanged={() => {}} toast={() => {}} /></MemoryRouter>);
    const row = (await screen.findByText('notes.txt')).closest('.df-list-row')!;
    fireEvent.click(row);
    await expectViewer('/api/files/text?id=file-1');
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(document.activeElement).toBe(row);
  });
  it('opens search results directly', async () => {
    render(<MemoryRouter initialEntries={['/search?q=notes']}><SearchPage /></MemoryRouter>);
    fireEvent.click(await screen.findByText('notes.txt'));
    await expectViewer('/api/files/text?id=file-1');
  });
  it('opens files shared with me', async () => {
    render(<MemoryRouter><SharedPage /></MemoryRouter>);
    fireEvent.click(screen.getByRole('button', { name: /Мне доступны/ }));
    await screen.findByText('notes.txt');
    fireEvent.click(screen.getByRole('button', { name: 'Открыть' }));
    await expectViewer('/api/shared/text?fileId=file-1');
  });
  it('opens inside a private shared folder', async () => {
    render(<MemoryRouter><SharedFolderModal rootDirId="dir" rootName="Folder" onClose={() => {}} /></MemoryRouter>);
    fireEvent.click(await screen.findByText('notes.txt'));
    await expectViewer('/api/shared/text?fileId=file-1');
    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(1));
  });
  it('opens a public file without downloading it', async () => {
    render(<MemoryRouter initialEntries={['/v/token']}><Routes><Route path="/v/:token" element={<PublicViewPage />} /></Routes></MemoryRouter>);
    fireEvent.click(await screen.findByRole('button', { name: 'Открыть файл' }));
    await expectViewer('/api/public/shares/token/text');
  });
  it('opens a public folder file using its share context', async () => {
    render(<MemoryRouter initialEntries={['/f/token']}><Routes><Route path="/f/:token" element={<PublicFolderPage />} /></Routes></MemoryRouter>);
    fireEvent.click(await screen.findByText('notes.txt'));
    await expectViewer('/api/public/folder-shares/token/text?dir=&fileId=file-1');
  });
});
