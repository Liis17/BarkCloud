import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { CardFile } from '../../lib/types';

const mocks = vi.hoisted(() => ({ apiGet: vi.fn() }));

vi.mock('../../lib/api', () => ({
  apiGet: mocks.apiGet,
  fetchClipboardImage: vi.fn(),
  proxiedImageUrl: (url: string) => url,
}));

import { Lightbox } from './Lightbox';

function file(id: string, overrides: Partial<CardFile> = {}): CardFile {
  return {
    id,
    name: id + '.jpg',
    ext: 'jpg',
    kind: 'photo',
    iconKind: 'img',
    size: 1,
    sizeLabel: '1 B',
    width: 10,
    height: 10,
    previews: [],
    createdAt: null,
    uploadedAt: null,
    jpegViewUrl: '/view/' + id + '.jpg',
    ...overrides,
  } as CardFile;
}

function shownImage() {
  return document.querySelector('.lb-stage img')?.getAttribute('src');
}

function touch(target: Element, type: 'touchStart' | 'touchMove' | 'touchEnd', points: Array<[number, number]>, changed?: Array<[number, number]>) {
  const list = points.map(([clientX, clientY]) => ({ clientX, clientY }));
  fireEvent[type](target, {
    touches: list,
    changedTouches: (changed ?? points).map(([clientX, clientY]) => ({ clientX, clientY })),
  });
}

const preloaded: string[] = [];

beforeEach(() => {
  preloaded.length = 0;
  mocks.apiGet.mockImplementation((url: string) => {
    const ids = decodeURIComponent(url.split('ids=')[1]).split(',');
    return Promise.resolve({
      urls: Object.fromEntries(ids.map((id) => [id, '/orig/' + id])),
      videoQualities: {},
    });
  });
  vi.stubGlobal('Image', class {
    set src(value: string) { preloaded.push(value); }
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  mocks.apiGet.mockReset();
});

describe('Lightbox', () => {
  it('flips videos with Shift+Arrow while plain arrows keep seeking', async () => {
    const items = [file('a', { kind: 'video' }), file('b', { kind: 'video' })];
    render(<Lightbox items={items} index={0} onClose={() => {}} />);
    await act(async () => {});
    const video = document.querySelector('video')!;
    expect(video.getAttribute('src')).toBe('/orig/a');
    Object.defineProperty(video, 'duration', { configurable: true, value: 60 });
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 10 });

    fireEvent.keyDown(window, { key: 'ArrowRight' });
    expect(video.currentTime).toBe(15);

    fireEvent.keyDown(window, { key: 'ArrowRight', shiftKey: true });
    await act(async () => {});
    expect(document.querySelector('video')?.getAttribute('src')).toBe('/orig/b');
  });

  it('flips with Shift+Arrow from the player slider but not from text fields', async () => {
    const items = [file('a'), file('b')];
    render(
      <>
        <Lightbox items={items} index={0} onClose={() => {}} />
        <input aria-label="text" />
      </>,
    );
    await act(async () => {});
    fireEvent.keyDown(screen.getByLabelText('text'), { key: 'ArrowRight', shiftKey: true });
    expect(shownImage()).toBe('/view/a.jpg');

    const range = document.createElement('input');
    range.type = 'range';
    document.body.appendChild(range);
    fireEvent.keyDown(range, { key: 'ArrowRight', shiftKey: true });
    expect(shownImage()).toBe('/view/b.jpg');
    range.remove();
  });

  it('preloads neighbour photos and fetches originals only for photos without a view source', async () => {
    const items = [file('a'), file('b'), file('c', { jpegViewUrl: undefined }), file('d', { kind: 'video' })];
    render(<Lightbox items={items} index={1} onClose={() => {}} />);
    await act(async () => {});

    expect(preloaded).toContain('/view/a.jpg');
    expect(preloaded).toContain('/orig/c');
    const requested = mocks.apiGet.mock.calls.map(([url]) => decodeURIComponent(url.split('ids=')[1]));
    expect(requested).toContain('c');
    expect(requested.join(',')).not.toContain('d');

    // Переход на уже предзагруженного соседа не повторяет запрос ссылки.
    const callsBefore = mocks.apiGet.mock.calls.length;
    fireEvent.keyDown(window, { key: 'ArrowRight' });
    await act(async () => {});
    expect(shownImage()).toBe('/orig/c');
    const repeated = mocks.apiGet.mock.calls.slice(callsBefore).map(([url]) => decodeURIComponent(url.split('ids=')[1]));
    expect(repeated).not.toContain('c');
  });

  it('swipes between items and ignores a swipe that ends a pinch', async () => {
    const items = [file('a'), file('b')];
    render(<Lightbox items={items} index={0} onClose={() => {}} />);
    await act(async () => {});
    const stage = document.querySelector('.lb-stage')!;

    touch(stage, 'touchStart', [[300, 200]]);
    touch(stage, 'touchMove', [[150, 205]]);
    touch(stage, 'touchEnd', [], [[150, 205]]);
    expect(shownImage()).toBe('/view/b.jpg');

    // Щипок увеличивает фото; оставшийся после щипка палец не листает.
    const img = document.querySelector('.lb-stage img')!;
    touch(img, 'touchStart', [[100, 100], [200, 100]]);
    touch(img, 'touchMove', [[50, 100], [250, 100]]);
    expect((img as HTMLElement).style.transform).toContain('scale(2)');
    touch(img, 'touchEnd', [[50, 100]], [[250, 100]]);
    touch(img, 'touchMove', [[300, 100]]);
    touch(img, 'touchEnd', [], [[300, 100]]);
    expect(shownImage()).toBe('/view/b.jpg');
  });

  it('pans a zoomed photo with one finger instead of swiping', async () => {
    const items = [file('a'), file('b')];
    render(<Lightbox items={items} index={0} onClose={() => {}} />);
    await act(async () => {});
    const img = document.querySelector('.lb-stage img') as HTMLElement;
    fireEvent.doubleClick(img);

    touch(img, 'touchStart', [[300, 200]]);
    touch(img, 'touchMove', [[100, 200]]);
    touch(img, 'touchEnd', [], [[100, 200]]);

    expect(shownImage()).toBe('/view/a.jpg');
    expect(img.style.transform).toContain('translate(-200px, 0px)');
  });
});
