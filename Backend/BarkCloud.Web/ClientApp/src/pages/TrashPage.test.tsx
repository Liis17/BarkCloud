import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TrashPage } from './TrashPage';

const response = (body: unknown) => ({ ok: true, status: 200, text: async () => JSON.stringify(body) });
const item = (n: number) => ({
  entryId: `entry-${n}`,
  name: `Файл ${n}`,
  deletedAt: '2026-11-01T00:00:00Z',
  purgeAt: '2026-11-15T00:00:00Z',
  media: { kind: 'document', ext: 'TXT', iconKind: 'doc', sizeLabel: '1 КБ' },
});
const firstPage = { items: Array.from({ length: 60 }, (_, i) => item(i + 1)), nextCursorAt: '2026-11-01T00:00:00Z', nextCursorId: 'entry-60' };
const secondPage = { items: [item(61)], nextCursorAt: null, nextCursorId: null };
const offline = () => Promise.reject(new TypeError('Failed to fetch'));

afterEach(() => vi.unstubAllGlobals());

function renderTrash() {
  render(<MemoryRouter initialEntries={['/trash']}><TrashPage /></MemoryRouter>);
}

describe('TrashPage', () => {
  it('догружает 61-ю запись по курсору через «Показать ещё»', async () => {
    const fetchMock = vi.fn((url: string) => Promise.resolve(response(url.includes('cursorId=') ? secondPage : firstPage)));
    vi.stubGlobal('fetch', fetchMock);
    renderTrash();

    expect(await screen.findByText('Файл 60')).toBeTruthy();
    expect(screen.queryByText('Файл 61')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));

    expect(await screen.findByText('Файл 61')).toBeTruthy();
    expect(fetchMock.mock.calls[1][0]).toBe('/api/cloud/trash?cursorAt=2026-11-01T00%3A00%3A00Z&cursorId=entry-60');
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).toBeNull();
    expect(screen.getAllByText(/^Файл \d+$/)).toHaveLength(61);
  });

  it('при обрыве соединения показывает ошибку вместо «Корзина пуста» и повторяет загрузку', async () => {
    const fetchMock = vi.fn().mockImplementationOnce(offline).mockImplementation(() => Promise.resolve(response(secondPage)));
    vi.stubGlobal('fetch', fetchMock);
    renderTrash();

    expect(await screen.findByText('Не удалось загрузить корзину')).toBeTruthy();
    expect(screen.queryByText('Корзина пуста')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить' }));

    expect(await screen.findByText('Файл 61')).toBeTruthy();
    expect(screen.queryByText('Не удалось загрузить корзину')).toBeNull();
  });

  it('при обрыве во время догрузки сохраняет список и кнопку для повтора', async () => {
    let calls = 0;
    vi.stubGlobal('fetch', vi.fn(() => (++calls === 2 ? offline() : Promise.resolve(response(calls === 1 ? firstPage : secondPage)))));
    renderTrash();

    expect(await screen.findByText('Файл 60')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));
    expect(await screen.findByText(/Нет связи с сервером/)).toBeTruthy();
    expect(screen.getByText('Файл 1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));
    expect(await screen.findByText('Файл 61')).toBeTruthy();
  });
});
