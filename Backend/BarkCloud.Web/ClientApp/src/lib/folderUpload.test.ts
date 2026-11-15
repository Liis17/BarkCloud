import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from './api';
import { ensureTree, treeFromPicked } from './folderUpload';

const mocks = vi.hoisted(() => ({ apiGet: vi.fn(), apiPost: vi.fn() }));
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  apiGet: mocks.apiGet,
  apiPost: mocks.apiPost,
}));

const CONFLICT = 'C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1';

afterEach(() => vi.clearAllMocks());

describe('ensureTree', () => {
  it('создаёт папки от родителя к детям и раскладывает файлы по id', async () => {
    let n = 0;
    mocks.apiPost.mockImplementation(async (_url: string, body: { parentId: string; name: string }) => ({ id: `id-${++n}-${body.name}` }));

    const dirs = await ensureTree({ paths: ['A/B', '', 'A'], dirs: ['A', 'A/Пусто'] }, 'base');

    expect(mocks.apiPost.mock.calls.map((c) => c[1])).toEqual([
      { parentId: 'base', name: 'A' },
      { parentId: 'id-1-A', name: 'Пусто' },
      { parentId: 'id-1-A', name: 'B' },
    ]);
    expect(dirs).toEqual(['id-3-B', 'base', 'id-1-A']);
  });

  it('дополняет существующую папку при конфликте имени, а другие ошибки не глотает', async () => {
    mocks.apiPost.mockRejectedValueOnce(new ApiError('exists', { code: CONFLICT, status: 409 }));
    mocks.apiGet.mockResolvedValueOnce({ dirs: [{ id: 'old-a', name: 'A' }], files: [] });
    expect(await ensureTree({ paths: ['A'], dirs: [] }, '')).toEqual(['old-a']);
    expect(mocks.apiGet).toHaveBeenCalledWith('/api/cloud/list?dir=&limit=1');

    mocks.apiPost.mockRejectedValueOnce(new ApiError('denied', { status: 403 }));
    await expect(ensureTree({ paths: ['A'], dirs: [] }, '')).rejects.toThrow('denied');
  });
});

describe('treeFromPicked', () => {
  it('берёт папку файла из webkitRelativePath', () => {
    const f = (rel: string) => Object.defineProperty(new File([''], rel.split('/').pop()!), 'webkitRelativePath', { value: rel });
    expect(treeFromPicked([f('Top/a.txt'), f('Top/Sub/b.txt')]).paths).toEqual(['Top', 'Top/Sub']);
  });
});
