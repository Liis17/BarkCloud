import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SearchPage } from './SearchPage';

const first = { kind: 'file', id: 'entry-1', fileId: 'file-1', entryId: 'entry-1', title: 'Отчёт', subtitle: '', previewUrl: '', mediaKind: 'document', favorite: false, matchField: 'alias', matchValue: 'отчёт', createdAt: null, size: 1 };
const second = { ...first, id: 'entry-2', fileId: 'file-2', entryId: 'entry-2', title: 'Второй отчёт' };
const response = (body: unknown) => ({ ok: true, text: async () => JSON.stringify(body) });

const player = vi.hoisted(() => ({ playTrack: vi.fn() }));
vi.mock('../hooks/useAudioPlayer', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../hooks/useAudioPlayer')>()),
  useAudioPlayer: () => player,
}));

afterEach(() => { vi.unstubAllGlobals(); player.playTrack.mockClear(); });

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

/** Рендер с маршрутами: уход со страницы поиска виден как away-route, кнопка Probe меняет запрос. */
function NavigateProbe() {
  const navigate = useNavigate();
  return <button data-testid="probe-new-query" onClick={() => navigate('/search?q=но')} />;
}

function renderSearchWithRoutes() {
  render(
    <MemoryRouter initialEntries={['/search?q=от']}>
      <Routes>
        <Route path="/search" element={<><SearchPage /><NavigateProbe /></>} />
        <Route path="*" element={<div data-testid="away-route" />} />
      </Routes>
    </MemoryRouter>,
  );
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

  describe('открытие медиа поверх поиска', () => {
    const photo = { kind: 'photo', id: 'entry-p1', fileId: 'file-1', entryId: 'entry-p1', title: 'Закат', subtitle: '', previewUrl: 'https://files.test/p/512', jpegViewUrl: 'https://files.test/p/jpeg', mediaKind: 'photo', favorite: false, matchField: 'name', matchValue: 'закат', createdAt: null, size: 1 };

    const lightbox = () => document.querySelector('.lightbox');
    const searchCalls = (fetchMock: ReturnType<typeof mockSearch>['fetchMock']) =>
      fetchMock.mock.calls.filter(([url]) => url.startsWith('/api/search')).length;

    it('фото: открывается в Lightbox без смены маршрута, закрытие сохраняет результаты', async () => {
      const { fetchMock } = mockSearch('photos', [photo]);
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: /Закат/ }));

      await waitFor(() => expect(document.querySelector('.lb-stage img')?.getAttribute('alt')).toBe('Закат'));
      expect(lightbox()).toBeTruthy();
      expect(screen.queryByTestId('away-route')).toBeNull();

      fireEvent.keyDown(window, { key: 'Escape' });

      expect(lightbox()).toBeNull();
      expect(screen.getByText('Закат')).toBeTruthy();
      expect(searchCalls(fetchMock)).toBe(1);
    });

    it('фото: стрелки листают секцию без дублей одного файла', async () => {
      const dup = { ...photo, id: 'entry-p2', entryId: 'entry-p2', title: 'Закат копия' };
      const next = { ...photo, id: 'entry-p3', fileId: 'file-2', entryId: 'entry-p3', title: 'Рассвет' };
      mockSearch('photos', [photo, dup, next]);
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: 'Закат' }));
      fireEvent.click(document.querySelector('.lb-nav.right')!);

      // Дубль file-1 пропущен: после «Заката» сразу «Рассвет».
      await waitFor(() => expect(document.querySelector('.lb-stage img')?.getAttribute('alt')).toBe('Рассвет'));
    });

    it('видео: открывается в Lightbox без перехода на вкладку видео', async () => {
      const video = { ...photo, kind: 'video', id: 'entry-v1', fileId: 'file-v', entryId: 'entry-v1', title: 'Клип', mediaKind: 'video' };
      mockSearch('videos', [video], { urls: { 'file-v': 'https://files.test/d/v' } });
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: /Клип/ }));

      await waitFor(() => expect(document.querySelector('video')).toBeTruthy());
      expect(lightbox()).toBeTruthy();
      expect(screen.queryByTestId('away-route')).toBeNull();
    });

    it('трек: запускается в мини-плеере без перехода', async () => {
      const track = { ...photo, kind: 'track', id: 'entry-t1', fileId: 'file-t', entryId: 'entry-t1', title: 'Песня', subtitle: 'Исполнитель · Альбом', previewUrl: '', mediaKind: 'audio' };
      mockSearch('tracks', [track]);
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: /Песня/ }));

      expect(player.playTrack).toHaveBeenCalledTimes(1);
      expect(player.playTrack).toHaveBeenCalledWith(expect.objectContaining({
        title: 'Песня', artist: 'Исполнитель', album: 'Альбом',
      }));
      expect(player.playTrack.mock.calls[0][0].file.id).toBe('file-t');
      expect(lightbox()).toBeNull();
      expect(screen.queryByTestId('away-route')).toBeNull();
    });

    it('фото: «Открыть» в контекстном меню работает как обычный клик', async () => {
      mockSearch('photos', [photo]);
      renderSearchWithRoutes();

      fireEvent.contextMenu(await screen.findByText('Закат'));
      fireEvent.click(screen.getByText('Открыть'));

      await waitFor(() => expect(lightbox()).toBeTruthy());
      expect(screen.queryByTestId('away-route')).toBeNull();
    });

    it('фото-SVG: остаётся в текстовом вьювере, а не в Lightbox', async () => {
      const svg = { ...photo, title: 'icon.svg' };
      mockSearch('photos', [svg]);
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: /icon\.svg/ }));

      await waitFor(() => expect(document.querySelector('.modal-backdrop')).toBeTruthy());
      expect(lightbox()).toBeNull();
      expect(screen.queryByTestId('away-route')).toBeNull();
    });

    it('смена запроса закрывает открытый Lightbox', async () => {
      mockSearch('photos', [photo]);
      renderSearchWithRoutes();

      fireEvent.click(await screen.findByRole('button', { name: /Закат/ }));
      expect(lightbox()).toBeTruthy();

      fireEvent.click(screen.getByTestId('probe-new-query'));

      expect(lightbox()).toBeNull();
      await waitFor(() => expect(screen.getByText('Закат')).toBeTruthy());
    });
  });
});
