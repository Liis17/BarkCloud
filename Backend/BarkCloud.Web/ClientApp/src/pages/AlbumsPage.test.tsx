import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AlbumsPage } from './AlbumsPage';

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
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));

afterEach(() => vi.clearAllMocks());

beforeEach(() => {
  mocks.apiGet.mockImplementation((url: string) => {
    if (url === '/api/albums') {
      return Promise.resolve({ albums: [{ id: 'album-1', name: 'Отпуск', count: 0, coverUrl: '', description: '' }] });
    }
    return Promise.resolve({});
  });
  mocks.apiPost.mockResolvedValue({});
});

describe('AlbumsPage drag-and-drop', () => {
  it('routes a drop on an own album to the album post-attach target', async () => {
    const { container } = render(<MemoryRouter><AlbumsPage /></MemoryRouter>);
    const card = (await screen.findByText('Отпуск')).closest('.album-card')!;
    const dataTransfer = {
      types: ['Files'],
      files: [new File(['photo'], 'photo.jpg', { type: 'image/jpeg' })],
    } as unknown as DataTransfer;

    fireEvent.drop(card, { dataTransfer });

    expect(mocks.enqueue).toHaveBeenCalledWith(expect.any(Array), {
      routeByMediaKind: true,
      albumId: 'album-1',
    });
    expect(container.querySelector('.album-card.drop-target')).toBeNull();
  });
});
