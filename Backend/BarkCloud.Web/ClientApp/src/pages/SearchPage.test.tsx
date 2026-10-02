import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SearchPage } from './SearchPage';

const first = { kind: 'file', id: 'entry-1', fileId: 'file-1', entryId: 'entry-1', title: 'Отчёт', subtitle: '', previewUrl: '', mediaKind: 'document', favorite: false, matchField: 'alias', matchValue: 'отчёт', createdAt: null, size: 1 };
const second = { ...first, id: 'entry-2', fileId: 'file-2', entryId: 'entry-2', title: 'Второй отчёт' };
const response = (body: unknown) => ({ ok: true, text: async () => JSON.stringify(body) });

afterEach(() => vi.unstubAllGlobals());

/** fetch-мок: /api/search отдаёт одну секцию, остальное — `other`. Возвращает POST-запросы как [url, body]. */
function mockSearch(section: string, items: unknown[], other: unknown = {}) {
  const fetchMock = vi.fn((url: string, _init?: RequestInit) => Promise.resolve(response(url.startsWith('/api/search')
    ? { query: 'от', sections: [{ key: section, items, nextCursor: '', hasMore: false, unavailable: false }] }
    : other)));
  vi.stubGlobal('fetch', fetchMock);
  const posts = () => fetchMock.mock.calls
    .filter(([, init]) => init?.method === 'POST')
    .map(([url, init]) => [url, JSON.parse(String(init?.body))]);
  return { fetchMock, posts };
}

function renderSearch() {
  render(<MemoryRouter initialEntries={['/search?q=от']}><SearchPage /></MemoryRouter>);
}

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

  describe('контекстное меню', () => {
    it('файл: меню как в «Файлах», удаление идёт по записи, а не по файлу', async () => {
      const { posts } = mockSearch('files', [first]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Отчёт'));
      expect(screen.getByText('Скачать')).toBeTruthy();
      expect(screen.getByText('Копировать ссылку')).toBeTruthy();
      expect(screen.getByText('Переименовать')).toBeTruthy();

      fireEvent.click(screen.getByText('Удалить'));
      fireEvent.click(screen.getByRole('button', { name: 'Удалить' }));

      expect(await screen.findByText('Ничего не найдено')).toBeTruthy();
      expect(posts()).toEqual([['/api/cloud/entry/delete', { entryId: 'entry-1' }]]);
    });

    it('файл: «Скачать» открывает временную ссылку на оригинал', async () => {
      const open = vi.fn();
      vi.stubGlobal('open', open);
      const { fetchMock } = mockSearch('files', [first], { urls: { 'file-1': 'https://files.test/d/1' } });
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Отчёт'));
      fireEvent.click(screen.getByText('Скачать'));

      await waitFor(() => expect(open).toHaveBeenCalledWith('https://files.test/d/1', '_blank'));
      expect(fetchMock.mock.calls.map(([url]) => url)).toContain('/api/files/download?ids=file-1');
    });

    it('файл: переименование обновляет название в выдаче', async () => {
      const { posts } = mockSearch('files', [first]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Отчёт'));
      fireEvent.click(screen.getByText('Переименовать'));
      fireEvent.change(screen.getByDisplayValue('Отчёт'), { target: { value: 'Итоги' } });
      fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

      expect(await screen.findByText('Итоги')).toBeTruthy();
      expect(posts()).toEqual([['/api/cloud/entry/rename', { entryId: 'entry-1', name: 'Итоги' }]]);
    });

    it('корзина: «Восстановить» вызывает restore и убирает хит', async () => {
      const trash = { ...first, kind: 'trash', id: 'entry-9', fileId: 'file-9', entryId: 'entry-9', title: 'Старый файл' };
      const { posts } = mockSearch('trash', [trash]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Старый файл'));
      fireEvent.click(screen.getByText('Восстановить'));

      expect(await screen.findByText('Ничего не найдено')).toBeTruthy();
      expect(posts()).toEqual([['/api/cloud/trash/restore', { entryId: 'entry-9' }]]);
    });

    it('торрент: «Возобновить» вызывает resume, после чего в меню «Пауза»', async () => {
      const torrent = { ...first, kind: 'torrent', id: 't1', fileId: '', entryId: '', title: 'Дистрибутив', status: 'paused', progress: 0.4 };
      const { posts } = mockSearch('torrents', [torrent]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Дистрибутив'));
      fireEvent.click(screen.getByText('Возобновить'));
      await waitFor(() => expect(posts()).toEqual([['/api/torrents/t1/resume', {}]]));

      fireEvent.contextMenu(screen.getByText('Дистрибутив'));
      expect(await screen.findByText('Пауза')).toBeTruthy();
    });

    it('папка: меню с действиями над папкой', async () => {
      const folder = { ...first, kind: 'folder', id: 'dir-1', fileId: '', entryId: '', title: 'Проекты' };
      mockSearch('folders', [folder]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Проекты'));
      for (const label of ['Открыть', 'Скачать папку', 'Сделать папку публичной', 'Поделиться с пользователем', 'Переименовать', 'Удалить']) {
        expect(screen.getByText(label)).toBeTruthy();
      }
    });

    it('умная папка: только «Открыть»', async () => {
      const smart = { ...first, kind: 'dynamicFolder', id: 'smart-1', fileId: '', entryId: '', title: 'Недавние' };
      mockSearch('folders', [smart]);
      renderSearch();

      fireEvent.contextMenu(await screen.findByText('Недавние'));
      expect(screen.getByText('Открыть')).toBeTruthy();
      expect(document.querySelectorAll('.ctx-item')).toHaveLength(1);
    });
  });
});
