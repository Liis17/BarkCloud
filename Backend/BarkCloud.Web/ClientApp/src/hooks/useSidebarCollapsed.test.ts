import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useSidebarCollapsed } from './useSidebarCollapsed';

const STORAGE_KEY = 'bark_sidebar_collapsed';
const storedValues = new Map<string, string>();

beforeEach(() => {
  storedValues.clear();
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => storedValues.get(key) ?? null,
    setItem: (key: string, value: string) => storedValues.set(key, value),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useSidebarCollapsed', () => {
  it('starts expanded when no preference is stored', () => {
    const { result } = renderHook(() => useSidebarCollapsed());

    expect(result.current[0]).toBe(false);
    act(() => result.current[1]());
    expect(result.current[0]).toBe(true);
    expect(storedValues.get(STORAGE_KEY)).toBe('true');
  });

  it('restores the collapsed preference from localStorage', () => {
    storedValues.set(STORAGE_KEY, 'true');

    const { result } = renderHook(() => useSidebarCollapsed());

    expect(result.current[0]).toBe(true);
  });

  it('continues toggling when localStorage access throws', () => {
    const unavailableStorage = Object.defineProperties({}, {
      getItem: { get: () => { throw new Error('Storage unavailable'); } },
      setItem: { get: () => { throw new Error('Storage unavailable'); } },
    });
    vi.stubGlobal('localStorage', unavailableStorage);

    const { result } = renderHook(() => useSidebarCollapsed());
    act(() => result.current[1]());

    expect(result.current[0]).toBe(true);
  });
});
