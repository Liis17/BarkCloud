import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Topbar } from './Topbar';
import { UploadManagerProvider } from '../../hooks/useUploadManager';

const hit = (id: string) => ({
  kind: 'photo', id, fileId: id, entryId: '', title: `Фото ${id}`, subtitle: '', previewUrl: '', mediaKind: 'photo',
  favorite: false, matchField: 'name', matchValue: id, createdAt: null, size: 0,
});
const jsonResponse = (body: unknown) => ({ ok: true, text: async () => JSON.stringify(body) });

function Location() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}{location.search}</output>;
}

function renderTopbar() {
  return render(<UploadManagerProvider><MemoryRouter initialEntries={['/photos']}><Topbar title="Фото" /><Location /></MemoryRouter></UploadManagerProvider>);
}

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Topbar global search', () => {
  it('ждёт 250 мс и отменяет устаревшую подсказку', async () => {
    vi.useFakeTimers();
    const pending = new Promise<never>(() => {});
    const fetchMock = vi.fn((_input: RequestInfo | URL, _init?: RequestInit) => pending);
    vi.stubGlobal('fetch', fetchMock);
    renderTopbar();

    const input = screen.getByRole('combobox');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'на' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(250); });
    const firstSignal = (fetchMock.mock.calls[0][1] as RequestInit).signal!;

    fireEvent.change(input, { target: { value: 'наст' } });
    expect(firstSignal.aborted).toBe(true);
    await act(async () => { await vi.advanceTimersByTimeAsync(250); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('оставляет максимум три элемента в группе и открывает выбранный результат клавиатурой', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(jsonResponse({
      query: 'на',
      sections: [{ key: 'photos', items: [hit('1'), hit('2'), hit('3'), hit('4')], nextCursor: '', hasMore: true, unavailable: false }],
    }))));
    renderTopbar();

    const input = screen.getByRole('combobox');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'на' } });
    await act(async () => { await vi.advanceTimersByTimeAsync(250); await Promise.resolve(); });

    expect(screen.getAllByRole('option')).toHaveLength(4); // 3 результата + «Все результаты»
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(screen.getByTestId('location').textContent).toBe('/photos?open=1');
  });
});
