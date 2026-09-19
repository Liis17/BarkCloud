import { fireEvent, render, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TorrentsPage } from './TorrentsPage';

const mocks = vi.hoisted(() => ({
  addTorrentFile: vi.fn(),
  enqueue: vi.fn(),
  push: vi.fn(),
}));

vi.mock('../hooks/useTorrentStream', () => ({
  useTorrentStream: () => ({ torrents: [] }),
}));
vi.mock('../hooks/useUploadManager', () => ({
  useUploadActions: () => ({ enqueue: mocks.enqueue }),
}));
vi.mock('../hooks/usePageHeader', () => ({ usePageHeader: vi.fn() }));
vi.mock('../hooks/useToast', () => ({
  useToast: () => [null, mocks.push],
}));
vi.mock('../lib/torrents', () => ({
  addTorrentFile: mocks.addTorrentFile,
}));

afterEach(() => vi.clearAllMocks());

describe('TorrentsPage drag-and-drop', () => {
  it('splits a mixed drop between the torrent API and the cloud queue', async () => {
    mocks.addTorrentFile.mockResolvedValue({});
    const torrent = new File(['torrent'], 'linux.TORRENT', { type: 'application/octet-stream' });
    const documentFile = new File(['notes'], 'notes.txt', { type: 'text/plain' });
    const dataTransfer = {
      types: ['Files'],
      files: [torrent, documentFile],
    } as unknown as DataTransfer;

    const { container } = render(<MemoryRouter><TorrentsPage /></MemoryRouter>);
    const dropzone = container.querySelector('.dropzone');
    expect(dropzone).toBeTruthy();

    fireEvent.drop(dropzone!, { dataTransfer });

    expect(mocks.enqueue).toHaveBeenCalledWith([documentFile], { routeByMediaKind: true });
    await waitFor(() => expect(mocks.addTorrentFile).toHaveBeenCalledWith(torrent));
  });
});
