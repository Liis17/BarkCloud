import React from 'react';
import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { Footbar } from './Footbar';
import { ShellContext } from '../../hooks/useShell';
import { PageHeaderContext, type PageHeader } from '../../hooks/usePageHeader';
import { useDocumentHead } from '../../hooks/useDocumentHead';
import { apiGet } from '../../lib/api';
import type { Shell } from '../../lib/types';

/** Каркас приложения: layout-route. Грузит /api/me один раз, держит Sidebar/Topbar/Footbar
 *  смонтированными, меняется только <Outlet/> при переходах между вкладками. */
export function AppShell() {
  const [shell, setShell] = React.useState<Shell | null>(null);
  const [header, setHeader] = React.useState<PageHeader>({ title: '' });

  React.useEffect(() => {
    // 401 внутри apiGet сам редиректит на /login.
    apiGet<Shell>('/api/me')
      .then(setShell)
      .catch(() => {});
  }, []);

  const headerCtx = React.useMemo(() => ({ header, setHeader }), [header]);
  const documentTitle = header.documentTitle ?? (typeof header.title === 'string' ? header.title : '');
  const documentIconUrl = header.documentIconUrl ?? null;

  useDocumentHead(
    () => ({ title: documentTitle, iconUrl: documentIconUrl }),
    [documentTitle, documentIconUrl],
  );

  return (
    <ShellContext.Provider value={shell}>
      <PageHeaderContext.Provider value={headerCtx}>
        <div className="app">
          <Sidebar />
          <div className="main">
            <Topbar {...header} />
            <div className={'content' + (header.contentClass ? ' ' + header.contentClass : '')}>
              <Outlet />
            </div>
            <Footbar />
          </div>
        </div>
      </PageHeaderContext.Provider>
    </ShellContext.Provider>
  );
}
