import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MusicPage } from './MusicPage';

const mocks = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  enqueue: vi.fn(),
  player: { current: null, isPlaying: false, playQueue: vi.fn() },
}));

vi.mock('../lib/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/api')>()),
  apiGet: mocks.apiGet,
  apiPost: mocks.apiPost,
}));
vi.mock('../hooks/useUploadManager', () => ({
  useUploadActions: () => ({ enqueue: mocks.enqueue, attachVersion: 0 }),
}));
vi.mock('../hooks/useAudioPlayer', () => ({ useAudioPlayer: () => mocks.player }));
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));

afterEach(() => vi.clearAllMocks());

beforeEach(() => {
  mocks.apiGet.mockImplementation((url: string) => {
    if (url.startsWith('/api/music/tracks')) return Promise.resolve({ items: [], nextCursorAt: '', nextCursorId: '' });
    if (url.startsWith('/api/music/playlists?')) {
      return Promise.resolve({
        items: [{ id: 'playlist-1', name: 'Мой плейлист', count: 0, canReorder: true, coverUrl: '' }],
      });
    }
    if (url === '/api/music/shared/with-me') return Promise.resolve({ items: [] });
    return Promise.resolve({});
  });
  mocks.apiPost.mockResolvedValue({});
});

function transfer() {
  return {
    types: ['Files'],
    files: [new File(['audio'], 'song.mp3', { type: 'audio/mpeg' })],
  } as unknown as DataTransfer;
}

describe('MusicPage drag-and-drop', () => {
  it('routes a drop on an own playlist and ignores a stale playlist after switching to tracks', async () => {
    const { container } = render(<MemoryRouter><MusicPage /></MemoryRouter>);
    fireEvent.click(await screen.findByRole('button', { name: 'Плейлисты' }));
    const card = (await screen.findByText('Мой плейлист')).closest('.music-playlist-card')!;
    const dataTransfer = transfer();

    fireEvent.drop(card, { dataTransfer });
    expect(mocks.enqueue).toHaveBeenCalledWith(expect.any(Array), {
      routeByMediaKind: true,
      playlistId: 'playlist-1',
    });

    fireEvent.dragEnter(card, { dataTransfer });
    fireEvent.click(screen.getByRole('button', { name: 'Треки' }));
    fireEvent.drop(container.querySelector('.dropzone')!, { dataTransfer });
    expect(mocks.enqueue).toHaveBeenLastCalledWith(expect.any(Array), {
      routeByMediaKind: true,
      playlistId: undefined,
    });
  });
});
