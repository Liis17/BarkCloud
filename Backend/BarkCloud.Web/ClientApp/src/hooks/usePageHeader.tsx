import React from 'react';

export interface PageHeader {
  title: React.ReactNode;
  kicker?: React.ReactNode;
  actions?: React.ReactNode;
  search?: boolean;
  /** Доп. класс на контейнере .content (например, для страниц без отступов). */
  contentClass?: string;
}

interface PageHeaderCtx {
  header: PageHeader;
  setHeader: (h: PageHeader) => void;
}

export const PageHeaderContext = React.createContext<PageHeaderCtx>({
  header: { title: '' },
  setHeader: () => {},
});

/** Страница задаёт заголовок Topbar. `deps` контролируют, когда обновлять
 *  (включай в них состояние, от которого зависят actions). */
export function usePageHeader(factory: () => PageHeader, deps: React.DependencyList): void {
  const { setHeader } = React.useContext(PageHeaderContext);
  React.useEffect(() => {
    setHeader(factory());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
