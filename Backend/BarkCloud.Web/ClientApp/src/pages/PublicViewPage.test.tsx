import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { PublicViewPage } from './PublicViewPage';

const { usePublicShareActions } = vi.hoisted(() => ({ usePublicShareActions: vi.fn() }));

vi.mock('../components/public/usePublicShareActions', () => ({ usePublicShareActions }));

const shareInfo = {
  found: true,
  name: 'active.svg',
  mediaKind: 'photo',
  previewUrl: '/s/token/preview.jpg',
  downloadUrl: '/s/token/download.svg',
  imageWidth: 0,
  imageHeight: 0,
  fileSize: 20,
  downloadPath: '/s/token',
};

function renderPublicView(info: typeof shareInfo) {
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => info })));
  return render(
    <MemoryRouter initialEntries={['/v/token']}>
      <Routes><Route path="/v/:token" element={<PublicViewPage />} /></Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  usePublicShareActions.mockReturnValue({ copyLink: vi.fn(), copyImage: vi.fn(), toastNode: null });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe('PublicViewPage SVG safety', () => {
  it('shows and copies only the raster preview while preserving the original download', async () => {
    renderPublicView(shareInfo);

    expect(await screen.findByRole('heading', { name: 'active.svg' })).toBeTruthy();
    expect(document.querySelector('main img')?.getAttribute('src')).toBe('/s/token/preview.jpg');
    expect(screen.getByRole('button', { name: 'Скопировать изображение' })).toBeTruthy();
    expect(usePublicShareActions).toHaveBeenLastCalledWith('/s/token/preview.jpg');
    expect(screen.getByRole('link', { name: /скачать/i }).getAttribute('href')).toBe('/s/token');
  });

  it('hides copy and displays a placeholder without a raster preview', async () => {
    renderPublicView({ ...shareInfo, previewUrl: '' });

    expect(await screen.findByRole('heading', { name: 'active.svg' })).toBeTruthy();
    expect(document.querySelector('main img')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скопировать изображение' })).toBeNull();
    expect(screen.getByText('Безопасное превью недоступно')).toBeTruthy();
    expect(usePublicShareActions).toHaveBeenLastCalledWith(undefined);
  });

  it('copies a non-SVG photo from its original when no preview exists', async () => {
    renderPublicView({
      ...shareInfo,
      name: 'photo.jpg',
      previewUrl: '',
      downloadUrl: '/s/token/download.jpg',
    });

    expect(await screen.findByRole('heading', { name: 'photo.jpg' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Скопировать изображение' })).toBeTruthy();
    expect(usePublicShareActions).toHaveBeenLastCalledWith('/s/token/download.jpg');
  });
});
