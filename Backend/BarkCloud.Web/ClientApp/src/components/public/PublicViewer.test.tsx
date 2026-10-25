import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PublicViewer } from './PublicViewer';

afterEach(cleanup);

describe('PublicViewer SVG safety', () => {
  it('shows a placeholder and keeps only original download when no raster preview exists', () => {
    const { container } = render(<PublicViewer file={{
      name: 'active.svg',
      mediaKind: 'photo',
      downloadUrl: '/original.svg',
      previewUrl: '',
      fileSize: 20,
    }} onClose={vi.fn()} />);

    expect(screen.getByText('Безопасное превью недоступно')).toBeTruthy();
    expect(container.querySelector('img')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Скопировать изображение' })).toBeNull();
    expect(screen.getByRole('link', { name: /скачать/i }).getAttribute('href')).toBe('/original.svg');
  });

  it('displays and offers copying the public raster preview', () => {
    const { container } = render(<PublicViewer file={{
      name: 'active.svg',
      mediaKind: 'photo',
      downloadUrl: '/original.svg',
      previewUrl: '/preview.jpg',
      fileSize: 20,
    }} onClose={vi.fn()} />);

    expect(container.querySelector('img')?.getAttribute('src')).toBe('/preview.jpg');
    expect(screen.getByRole('button', { name: 'Скопировать изображение' })).toBeTruthy();
  });
});
