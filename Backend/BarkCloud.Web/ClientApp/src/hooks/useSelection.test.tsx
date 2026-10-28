import { act, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useSelection } from './useSelection';

describe('useSelection Shift range', () => {
  const order = ['a', 'b', 'c', 'd', 'e'];

  it.each([['b', 'e'], ['e', 'b']])('selects the inclusive range from %s to %s', (first, last) => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.select(first, order, false));
    act(() => result.current.select(last, order, true));
    expect([...result.current.ids].sort()).toEqual(['b', 'c', 'd', 'e']);
  });

  it('uses an ordinary file click as the range anchor', () => {
    const { result } = renderHook(() => useSelection());
    act(() => result.current.setAnchor('b'));
    act(() => result.current.select('e', order, true));
    expect(result.current.list).toEqual(['b', 'c', 'd', 'e']);
  });
});
