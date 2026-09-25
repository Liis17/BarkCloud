import { act, render, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MediaItem } from '../lib/types';
import { PhotosPage } from './PhotosPage';
import { VideosPage } from './VideosPage';

const mocks = vi.hoisted(() => ({
  apiGet: vi.fn(),
  items: [] as unknown[],
  attachVersion: 0,
  mediaRemoved: null as unknown,
  bulkRemoved: null as unknown,
  toast: vi.fn(),
  zeroStats: false,
  failNextStats: false,
}));

vi.mock('../lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/api')>()),
  apiGet: mocks.apiGet,
}));
vi.mock('../hooks/useInfiniteMedia', () => ({
  useInfiniteMedia: () => ({
    items: mocks.items,
    loading: false,
    done: false,
    sentinelRef: vi.fn(),
    removeItem: vi.fn(),
    updateItem: vi.fn(),
    prependItems: vi.fn(),
  }),
}));
vi.mock('../hooks/useMediaActions', () => ({
  useMediaActions: (args: { onRemoved?: unknown }) => {
    mocks.mediaRemoved = args.onRemoved;
    return { overlay: null, api: {}, openMenu: vi.fn() };
  },
}));
vi.mock('../hooks/useBulkMedia', () => ({
  useBulkMedia: (args: { onRemovedBatch?: unknown }) => {
    mocks.bulkRemoved = args.onRemovedBatch;
    return {
      bar: null,
      overlay: null,
      active: false,
      isSelected: () => false,
      setAnchor: vi.fn(),
      toggle: vi.fn(),
    };
  },
}));
vi.mock('../hooks/useFileDrop', () => ({ useFileDrop: () => ({ over: false, dropHandlers: {} }) }));
vi.mock('../hooks/useToast', () => ({ useToast: () => [null, mocks.toast] }));
vi.mock('../hooks/useUploadManager', () => ({ useUploadActions: () => ({ enqueue: vi.fn(), attachVersion: mocks.attachVersion }) }));
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));
vi.mock('../components/media/MediaThumb', () => ({ MediaThumb: () => <div data-testid="media-thumb" /> }));
vi.mock('../components/media/Lightbox', () => ({ Lightbox: () => null }));
vi.mock('../components/memories/MemoriesStrip', () => ({ MemoriesStrip: () => null }));
vi.mock('../components/search/MediaSearchResults', () => ({ MediaSearchResults: () => null }));

function makeItems(kind: 'photo' | 'video'): MediaItem[] {
  return Array.from({ length: 60 }, (_, index) => ({
    id: `${kind}-${index}`,
    name: `${kind}-${index}`,
    ext: kind === 'photo' ? 'jpg' : 'mp4',
    kind,
    iconKind: kind === 'photo' ? 'img' : 'vid',
    size: 1_000_000,
    sizeLabel: '1 МБ',
    width: 1920,
    height: 1080,
    previews: [],
    createdAt: '2026-09-01T00:00:00.000Z',
    uploadedAt: '2026-09-01T00:00:00.000Z',
    entriesCount: 1,
    entryNames: [`${kind}-${index}`],
    entryIds: [`entry-${kind}-${index}`],
  }));
}

function statValue(container: HTMLElement, label: string): string | undefined {
  const stat = Array.from(container.querySelectorAll('.stat')).find(
    (node) => node.querySelector('.k')?.textContent === label,
  );
  return stat?.querySelector('.v')?.textContent;
}

beforeEach(() => {
  mocks.items = [];
  mocks.attachVersion = 0;
  mocks.mediaRemoved = null;
  mocks.bulkRemoved = null;
  mocks.toast.mockReset();
  mocks.zeroStats = false;
  mocks.failNextStats = false;
  mocks.apiGet.mockImplementation((url: string) => {
    if (url === '/api/albums') return Promise.resolve({ albums: [] });
    if (url.includes('/api/cloud/media/stats?kind=')) {
      if (mocks.failNextStats) {
        mocks.failNextStats = false;
        return Promise.reject(new Error('Статистика временно недоступна'));
      }
      if (mocks.zeroStats) return Promise.resolve({ totalCount: 0, totalSizeBytes: 0 });
      if (url.includes('kind=photo')) return Promise.resolve({ totalCount: 236, totalSizeBytes: 0 });
      if (url.includes('kind=video')) return Promise.resolve({ totalCount: 236, totalSizeBytes: 10 * 1024 * 1024 * 1024 });
    }
    return Promise.resolve({ items: [] });
  });
});

afterEach(() => vi.clearAllMocks());

describe('gallery media statistics', () => {
  it('shows the full photo count when only the first page is loaded', async () => {
    mocks.items = makeItems('photo');
    const { container } = render(<MemoryRouter><PhotosPage /></MemoryRouter>);

    await waitFor(() => expect(container.querySelector('.photos-toolbar .count')?.textContent).toBe('236'));
    expect(mocks.apiGet).toHaveBeenCalledWith('/api/cloud/media/stats?kind=photo');
  });

  it('shows the full video count and size when only the first page is loaded', async () => {
    mocks.items = makeItems('video');
    const { container } = render(<MemoryRouter><VideosPage /></MemoryRouter>);

    await waitFor(() => {
      expect(container.querySelector('.vid-toolbar .count')?.textContent).toBe('236');
      expect(statValue(container, 'Всего видео')).toBe('236');
      expect(statValue(container, 'Занято видео')).toBe('10 ГБ');
    });
    expect(mocks.apiGet).toHaveBeenCalledWith('/api/cloud/media/stats?kind=video');
  });

  it('refreshes stats after upload and successful single or bulk deletion', async () => {
    mocks.items = makeItems('video');
    const view = render(<MemoryRouter><VideosPage /></MemoryRouter>);
    const statsCalls = () => mocks.apiGet.mock.calls.filter(([url]) => url === '/api/cloud/media/stats?kind=video').length;

    await waitFor(() => expect(statsCalls()).toBe(1));

    mocks.attachVersion = 1;
    view.rerender(<MemoryRouter><VideosPage /></MemoryRouter>);
    await waitFor(() => expect(statsCalls()).toBe(2));

    act(() => (mocks.mediaRemoved as (media: MediaItem) => void)(mocks.items[0] as MediaItem));
    await waitFor(() => expect(statsCalls()).toBe(3));

    act(() => (mocks.bulkRemoved as (ids: string[]) => void)(['video-1']));
    await waitFor(() => expect(statsCalls()).toBe(4));

    mocks.failNextStats = true;
    act(() => (mocks.mediaRemoved as (media: MediaItem) => void)(mocks.items[0] as MediaItem));
    await waitFor(() => expect(statsCalls()).toBe(5));
    expect(statValue(view.container, 'Всего видео')).toBe('236');
    expect(statValue(view.container, 'Занято видео')).toBe('10 ГБ');
  });

  it('shows loaded zero statistics instead of empty-page placeholders', async () => {
    mocks.zeroStats = true;
    const { container } = render(<MemoryRouter><VideosPage /></MemoryRouter>);

    await waitFor(() => {
      expect(container.querySelector('.vid-toolbar .count')?.textContent).toBe('0');
      expect(statValue(container, 'Всего видео')).toBe('0');
      expect(statValue(container, 'Занято видео')).toBe('0 Б');
    });
  });
});
