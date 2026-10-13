import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiGet, apiPost } from '../../lib/api';
import type { MusicPlaylist } from '../../lib/types';
import type { ToastPush } from '../../hooks/useToast';
import { PlaylistPickerModal } from './PlaylistPickerModal';

vi.mock('../../lib/api', () => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
}));

const apiGetMock = vi.mocked(apiGet);
const apiPostMock = vi.mocked(apiPost);

function playlist(id: string, name: string): MusicPlaylist {
  return {
    id,
    name,
    description: '',
    coverFileId: '',
    coverUrl: '',
    count: 0,
    ownerUserId: 1,
    canReorder: true,
    createdAt: null,
    updatedAt: null,
  };
}

afterEach(() => vi.resetAllMocks());

describe('PlaylistPickerModal', () => {
  it('загружает следующую страницу и добавляет файл в выбранный с неё плейлист', async () => {
    apiGetMock
      .mockResolvedValueOnce({ items: [playlist('first', 'Первый')], nextCursorAt: '2026-02-03T10:00:00Z', nextCursorId: 'first' })
      .mockResolvedValueOnce({ items: [playlist('second', 'Архив')], nextCursorAt: null, nextCursorId: null });
    apiPostMock.mockResolvedValue({});
    const onClose = vi.fn();
    const toast = vi.fn() as ToastPush;

    render(<PlaylistPickerModal fileId="file-1" onClose={onClose} toast={toast} />);

    fireEvent.click(await screen.findByRole('button', { name: /Архив/ }));

    expect(apiGetMock).toHaveBeenNthCalledWith(1, '/api/music/playlists?limit=200');
    expect(apiGetMock).toHaveBeenNthCalledWith(2, '/api/music/playlists?limit=200&cursorAt=2026-02-03T10%3A00%3A00Z&cursorId=first');
    expect(apiPostMock).toHaveBeenCalledWith('/api/music/playlists/tracks/add', { playlistId: 'second', fileIds: ['file-1'] });
    await waitFor(() => expect(toast).toHaveBeenCalledWith('Добавлено в плейлист'));
    expect(onClose).toHaveBeenCalledOnce();
  });
});
