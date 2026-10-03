import React from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { Topbar } from './Topbar';
import { Footbar } from './Footbar';
import { ShellContext } from '../../hooks/useShell';
import { PageHeaderContext, type PageHeader } from '../../hooks/usePageHeader';
import { UploadManagerProvider } from '../../hooks/useUploadManager';
import { AudioPlayerProvider } from '../../hooks/useAudioPlayer';
import { useDocumentHead } from '../../hooks/useDocumentHead';
import { MiniPlayer } from '../music/MiniPlayer';
import { useApiResource } from '../../hooks/useApiResource';
import { storagePending } from '../ui/StorageStatsStatus';
import type { Shell, SidebarStorage } from '../../lib/types';

/** Каркас приложения: layout-route. Грузит /api/me один раз, держит Sidebar/Topbar/Footbar
 *  смонтированными, меняется только <Outlet/> при переходах между вкладками. */
export function AppShell() {
  const profile = useApiResource<Shell>('/api/me?includeStorage=false');
  const storage = useApiResource<SidebarStorage>('/api/storage', true, storagePending);
  const shell = React.useMemo(() => profile.data ? { ...profile.data, storage: storage.data } : null, [profile.data, storage.data]);
  const [header, setHeader] = React.useState<PageHeader>({ title: '' });
  const { pathname } = useLocation();
  const previousPath = React.useRef(pathname);
  React.useEffect(() => {
    if (previousPath.current !== pathname) { previousPath.current = pathname; void storage.reload(); }
  }, [pathname, storage.reload]);

  const headerCtx = React.useMemo(() => ({ header, setHeader }), [header]);
  const documentTitle = header.documentTitle ?? (typeof header.title === 'string' ? header.title : '');
  const documentIconUrl = header.documentIconUrl ?? null;

  useDocumentHead(
    () => ({ title: documentTitle, iconUrl: documentIconUrl }),
    [documentTitle, documentIconUrl],
  );

  return (
    <ShellContext.Provider value={shell}>
      <UploadManagerProvider>
        <AudioPlayerProvider>
          <PageHeaderContext.Provider value={headerCtx}>
            <div className={'app' + (header.contentClass === 'settings-content' ? ' app-settings' : '')}>
              <Sidebar statistics={storage.data} profileError={!!profile.error} storageError={!!storage.error} />
              <div className="main">
                <Topbar {...header} />
                <div className={'content' + (header.contentClass ? ' ' + header.contentClass : '')}>
                  <Outlet />
                </div>
                <Footbar />
              </div>
            </div>
            <MiniPlayer />
          </PageHeaderContext.Provider>
        </AudioPlayerProvider>
      </UploadManagerProvider>
    </ShellContext.Provider>
  );
}
