import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useApiResource } from './useApiResource';
import { jsonResponse } from '../test/settingsFixtures';

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('useApiResource', () => {
  it('объединяет повторные запросы и игнорирует отменённый ответ предыдущего раздела', async () => {
    let resolveOld!: (response: Response) => void;
    const fetchMock = vi.fn((path: string, _init?: RequestInit) => path === '/old'
      ? new Promise<Response>((resolve) => { resolveOld = resolve; }) : Promise.resolve(jsonResponse({ value: 'new' })));
    vi.stubGlobal('fetch', fetchMock);
    const { result, rerender } = renderHook(({ path }) => useApiResource<{ value: string }>(path), { initialProps: { path: '/old' } });
    let first!: Promise<void>;
    let second!: Promise<void>;
    act(() => { first = result.current.reload(); second = result.current.reload(); });
    expect(first).toBe(second);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    rerender({ path: '/new' });
    expect(fetchMock.mock.calls[0][1]?.signal?.aborted).toBe(true);
    await waitFor(() => expect(result.current.data?.value).toBe('new'));
    await act(async () => { resolveOld(jsonResponse({ value: 'old' })); await first; });
    expect(result.current.data?.value).toBe('new');
  });

  it('сохраняет успешные данные при ошибке обновления', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(jsonResponse({ value: 42 })).mockResolvedValueOnce(jsonResponse({ message: 'offline' }, 503));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useApiResource<{ value: number }>('/stats'));
    await waitFor(() => expect(result.current.data?.value).toBe(42));
    await act(async () => { await result.current.reload(); });
    expect(result.current.error?.message).toBe('offline');
    expect(result.current.data?.value).toBe(42);
  });

  it('опрашивает подсчёт раз в три секунды и приостанавливает его в скрытой вкладке', async () => {
    vi.useFakeTimers();
    let hidden = false;
    vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden);
    const fetchMock = vi.fn(() => Promise.resolve(jsonResponse({ state: 'loading' })));
    vi.stubGlobal('fetch', fetchMock);
    const pending = (data: { state: string }) => data.state === 'loading';
    const { result } = renderHook(() => useApiResource('/stats', true, pending));
    await act(async () => {});
    expect(result.current.data).toEqual({ state: 'loading' });
    await act(async () => { await vi.advanceTimersByTimeAsync(2999); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    act(() => { hidden = true; document.dispatchEvent(new Event('visibilitychange')); });
    await act(async () => { await vi.advanceTimersByTimeAsync(9000); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => { hidden = false; document.dispatchEvent(new Event('visibilitychange')); });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });
});
