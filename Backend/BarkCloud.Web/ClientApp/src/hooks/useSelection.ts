import React from 'react';

/** Множественный выбор по строковому id (fileId/entryId). */
export function useSelection() {
  const [ids, setIds] = React.useState<Set<string>>(new Set());

  const toggle = React.useCallback((id: string) => {
    setIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);
  const clear = React.useCallback(() => setIds(new Set()), []);
  const has = React.useCallback((id: string) => ids.has(id), [ids]);
  const setAll = React.useCallback((all: string[], on: boolean) => setIds(on ? new Set(all) : new Set()), []);

  return { ids, list: Array.from(ids), count: ids.size, active: ids.size > 0, toggle, clear, has, setAll };
}
