import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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

  it('uses the original image and allows copying when a non-SVG photo has no preview', () => {
    const { container } = render(<PublicViewer file={{
      name: 'photo.jpg',
      mediaKind: 'photo',
      downloadUrl: '/original.jpg',
      previewUrl: '',
      fileSize: 20,
    }} onClose={vi.fn()} />);

    expect(container.querySelector('img')?.getAttribute('src')).toBe('/original.jpg');
    expect(screen.getByRole('button', { name: 'Скопировать изображение' })).toBeTruthy();
  });

  it('uses the original animated GIF instead of its static preview', () => {
    const { container } = render(<PublicViewer file={{
      name: 'animated.gif',
      mediaKind: 'photo',
      downloadUrl: '/original.gif',
      previewUrl: '/preview.jpg',
      fileSize: 20,
    }} onClose={vi.fn()} />);

    expect(container.querySelector('img')?.getAttribute('src')).toBe('/original.gif');
    expect(screen.getByRole('button', { name: 'Скопировать изображение' })).toBeTruthy();
  });
});

function touch(target: Element, type: 'touchStart' | 'touchMove' | 'touchEnd', points: Array<[number, number]>, changed?: Array<[number, number]>) {
  fireEvent[type](target, {
    touches: points.map(([clientX, clientY]) => ({ clientX, clientY })),
    changedTouches: (changed ?? points).map(([clientX, clientY]) => ({ clientX, clientY })),
  });
}

describe('PublicViewer navigation and gestures', () => {
  const photo = (n: number) => ({ name: `p${n}.jpg`, mediaKind: 'photo', downloadUrl: `/p${n}.jpg`, previewUrl: '', fileSize: 0 });
  const video = { name: 'clip.mp4', mediaKind: 'video', downloadUrl: '/clip.mp4', previewUrl: '', fileSize: 0 };
  const img = (c: HTMLElement) => c.querySelector('.public-viewer-media img') as HTMLElement | null;

  it('листает кнопками, стрелками и свайпом, показывая позицию', () => {
    const items = [photo(1), photo(2), photo(3)];
    const { container } = render(<PublicViewer file={items[1]} items={items} onClose={vi.fn()} />);
    expect(screen.getByText('2 из 3')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Следующий' }));
    expect(img(container)?.getAttribute('src')).toBe('/p3.jpg');
    expect(screen.queryByRole('button', { name: 'Следующий' })).toBeNull();

    fireEvent.keyDown(window, { key: 'ArrowLeft' });
    expect(img(container)?.getAttribute('src')).toBe('/p2.jpg');

    const stage = container.querySelector('.public-viewer-stage')!;
    touch(stage, 'touchStart', [[300, 200]]);
    touch(stage, 'touchMove', [[150, 205]]);
    touch(stage, 'touchEnd', [], [[150, 205]]);
    expect(img(container)?.getAttribute('src')).toBe('/p3.jpg');
  });

  it('щипок зумит фото, а увеличенное фото двигается пальцем без листания', () => {
    const items = [photo(1), photo(2)];
    const { container } = render(<PublicViewer file={items[0]} items={items} onClose={vi.fn()} />);
    touch(img(container)!, 'touchStart', [[100, 100], [200, 100]]);
    touch(img(container)!, 'touchMove', [[50, 100], [250, 100]]);
    touch(img(container)!, 'touchEnd', [], [[50, 100], [250, 100]]);
    expect(img(container)?.style.transform).toContain('scale(2)');

    touch(img(container)!, 'touchStart', [[300, 200]]);
    touch(img(container)!, 'touchMove', [[100, 200]]);
    touch(img(container)!, 'touchEnd', [], [[100, 200]]);
    expect(img(container)?.getAttribute('src')).toBe('/p1.jpg');
    expect(img(container)?.style.transform).toContain('translate(-200px, 0px)');
  });

  it('на видео стрелки остаются плееру, листание — Shift+стрелки', () => {
    const items = [video, photo(2)];
    const { container } = render(<PublicViewer file={video} items={items} onClose={vi.fn()} />);
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(img(container)).toBeNull();
    fireEvent.keyDown(window, { key: 'ArrowRight', shiftKey: true });
    expect(img(container)?.getAttribute('src')).toBe('/p2.jpg');
  });
});

describe('PublicViewer page title sync', () => {
  it('сообщает странице о файле, показанном после листания', () => {
    const items = [1, 2].map((n) => ({ name: `p${n}.jpg`, mediaKind: 'photo', downloadUrl: `/p${n}.jpg`, previewUrl: '', fileSize: 0 }));
    const onChange = vi.fn();
    render(<PublicViewer file={items[0]} items={items} onChange={onChange} onClose={vi.fn()} />);
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(onChange).toHaveBeenCalledWith(items[1]);
  });
});
