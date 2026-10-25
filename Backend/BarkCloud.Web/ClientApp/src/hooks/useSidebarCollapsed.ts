import { useCallback, useEffect, useState } from 'react';

const SIDEBAR_COLLAPSED_KEY = 'bark_sidebar_collapsed';

function readSidebarCollapsed() {
  try {
    return localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true';
  } catch {
    return false;
  }
}

export function useSidebarCollapsed() {
  const [collapsed, setCollapsed] = useState(readSidebarCollapsed);

  useEffect(() => {
    try {
      localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(collapsed));
    } catch {
      // localStorage может быть недоступен; состояние панели остаётся рабочим.
    }
  }, [collapsed]);

  const toggle = useCallback(() => setCollapsed((value) => !value), []);
  return [collapsed, toggle] as const;
}
