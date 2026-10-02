import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MediaSearchResults } from './MediaSearchResults';

vi.mock('../media/MediaThumb', () => ({ MediaThumb: () => <div data-testid="media-thumb" /> }));
vi.mock('../media/Lightbox', () => ({ Lightbox: () => null }));

const entry = {
  entryId: 'entry-1',
  fileId: 'file-1',
  directoryId: 'dir-1',
  name: 'кот.jpg',
  createdAt: null,
  media: { id: 'file-1', name: 'кот.jpg', ext: 'jpg', kind: 'photo', iconKind: 'img', size: 1, sizeLabel: '1 Б', width: 1, height: 1, previews: [], createdAt: null, uploadedAt: null },
};

afterEach(() => vi.unstubAllGlobals());

describe('MediaSearchResults', () => {
  it('ПКМ по плитке открывает меню действий над медиа', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({
      ok: true,
      text: async () => JSON.stringify({ files: [entry], nextCursorAt: null, nextCursorId: '' }),
    })));
    render(<MemoryRouter><MediaSearchResults q="кот" albums={[]} toast={vi.fn()} /></MemoryRouter>);

    fireEvent.contextMenu(await screen.findByTestId('media-thumb'));

    expect(screen.getByText('Скачать')).toBeTruthy();
    expect(screen.getByText('Копировать ссылку')).toBeTruthy();
    expect(screen.getByText('Свойства')).toBeTruthy();
  });
});
