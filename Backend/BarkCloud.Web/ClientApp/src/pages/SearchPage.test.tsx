import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SearchPage } from './SearchPage';

const first = { kind: 'file', id: 'entry-1', fileId: 'file-1', entryId: 'entry-1', title: 'Отчёт', subtitle: '', previewUrl: '', mediaKind: 'document', favorite: false, matchField: 'alias', matchValue: 'отчёт', createdAt: null, size: 1 };
const second = { ...first, id: 'entry-2', fileId: 'file-2', entryId: 'entry-2', title: 'Второй отчёт' };
const response = (body: unknown) => ({ ok: true, text: async () => JSON.stringify(body) });

afterEach(() => vi.unstubAllGlobals());

describe('SearchPage', () => {
  it('запрашивает следующую страницу только для выбранной группы', async () => {
    const fetchMock = vi.fn((url: string) => Promise.resolve(response(url.includes('section=files')
      ? { query: 'от', sections: [{ key: 'files', items: [second], nextCursor: '', hasMore: false, unavailable: false }] }
      : { query: 'от', sections: [{ key: 'files', items: [first], nextCursor: 'cursor-1', hasMore: true, unavailable: false }] })));
    vi.stubGlobal('fetch', fetchMock);
    render(<MemoryRouter initialEntries={['/search?q=от']}><SearchPage /></MemoryRouter>);

    expect(await screen.findByText('Отчёт')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));
    expect(await screen.findByText('Второй отчёт')).toBeTruthy();
    expect(fetchMock.mock.calls[1][0]).toContain('section=files');
    expect(fetchMock.mock.calls[1][0]).toContain('cursor=cursor-1');
  });
});
