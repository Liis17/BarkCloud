import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';

import './styles/shared.css';
import './styles/pages.css';

import { AppShell } from './components/shell/AppShell';
import { PhotosPage } from './pages/PhotosPage';
import { VideosPage } from './pages/VideosPage';
import { AlbumsPage } from './pages/AlbumsPage';
import { FilesPage } from './pages/FilesPage';
import { FavoritesPage } from './pages/FavoritesPage';
import { TrashPage } from './pages/TrashPage';
import { SharedPage } from './pages/SharedPage';
import { SettingsPage } from './pages/SettingsPage';
import { PublicViewPage } from './pages/PublicViewPage';
import { PublicFolderPage } from './pages/PublicFolderPage';
import { PublicAlbumPage } from './pages/PublicAlbumPage';
import { DocumentHeadProvider } from './hooks/useDocumentHead';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <BrowserRouter>
      <DocumentHeadProvider>
        <Routes>
          <Route path="v/:token" element={<PublicViewPage />} />
          <Route path="f/:token" element={<PublicFolderPage />} />
          <Route path="al/:token" element={<PublicAlbumPage />} />
          <Route element={<AppShell />}>
            <Route index element={<Navigate to="/photos" replace />} />
            <Route path="photos" element={<PhotosPage />} />
            <Route path="videos" element={<VideosPage />} />
            <Route path="albums" element={<AlbumsPage />} />
            <Route path="files" element={<FilesPage />} />
            <Route path="favorites" element={<FavoritesPage />} />
            <Route path="trash" element={<TrashPage />} />
            <Route path="shared" element={<SharedPage />} />
            <Route path="settings" element={<SettingsPage />} />
            <Route path="*" element={<Navigate to="/photos" replace />} />
          </Route>
        </Routes>
      </DocumentHeadProvider>
    </BrowserRouter>
  </React.StrictMode>,
);
