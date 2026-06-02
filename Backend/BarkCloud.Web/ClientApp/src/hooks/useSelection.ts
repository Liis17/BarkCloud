import React from 'react';

/** Множественный выбор по строковому id (fileId/entryId) с поддержкой диапазона по Shift. */
export function useSelection() {
  const [ids, setIds] = React.useState<Set<string>>(new Set());
  // Якорь — последний явно отмеченный id; от него Shift+клик тянет диапазон.
  const anchorRef = React.useRef<string | null>(null);

  const toggle = React.useCallback((id: string) => {
    setIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    anchorRef.current = id;
  }, []);

  /** Выделить диапазон от якоря до id (включительно), добавляя к текущему выбору. */
  const selectRange = React.useCallback((orderedIds: string[], id: string) => {
    setIds((prev) => {
      const next = new Set(prev);
      const anchor = anchorRef.current;
      const b = orderedIds.indexOf(id);
      const a = anchor ? orderedIds.indexOf(anchor) : -1;
      if (a === -1 || b === -1) {
        next.add(id);
        return next;
      }
      const [lo, hi] = a < b ? [a, b] : [b, a];
      for (let i = lo; i <= hi; i++) next.add(orderedIds[i]);
      return next;
    });
    anchorRef.current = id;
  }, []);

  /** Единый обработчик клика по чекбоксу/«галке»: Shift тянет диапазон, иначе обычный toggle. */
  const select = React.useCallback(
    (id: string, orderedIds: string[], shift: boolean) => {
      if (shift && anchorRef.current) selectRange(orderedIds, id);
      else toggle(id);
    },
    [selectRange, toggle],
  );

  const clear = React.useCallback(() => {
    anchorRef.current = null;
    setIds(new Set());
  }, []);
  const has = React.useCallback((id: string) => ids.has(id), [ids]);
  const setAll = React.useCallback((all: string[], on: boolean) => {
    anchorRef.current = on && all.length ? all[all.length - 1] : null;
    setIds(on ? new Set(all) : new Set());
  }, []);

  return { ids, list: Array.from(ids), count: ids.size, active: ids.size > 0, toggle, select, selectRange, clear, has, setAll };
}
