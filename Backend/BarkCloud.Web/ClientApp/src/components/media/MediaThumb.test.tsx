import { act, cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MediaThumb } from './MediaThumb';

const originalDecode = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'decode');
const media = {
  id: 'photo-1',
  kind: 'photo' as const,
  previews: [{ w: 128, target: 128, url: '/preview.jpg' }],
  placeholder: { colors: Array(9).fill('#AA5533') as string[], aspectRatio: 1 },
};

function mockDecode(implementation: () => Promise<void>) {
  const decode = vi.fn(implementation);
  Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: decode });
  return decode;
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  if (originalDecode) Object.defineProperty(HTMLImageElement.prototype, 'decode', originalDecode);
  else delete (HTMLImageElement.prototype as Partial<HTMLImageElement>).decode;
});

describe('MediaThumb с цветным плейсхолдером', () => {
  it('показывает изображение и удаляет SVG только после полной загрузки и декодирования', async () => {
    let resolveDecode!: () => void;
    const decoded = new Promise<void>((resolve) => { resolveDecode = resolve; });
    Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: vi.fn(() => decoded) });
    const { container } = render(<MediaThumb media={media} colorPlaceholder />);
    const image = container.querySelector('img')!;

    expect(container.querySelectorAll('svg circle')).toHaveLength(9);
    expect(image.classList.contains('on')).toBe(false);
    fireEvent.load(image);
    expect(image.classList.contains('on')).toBe(false);
    expect(container.querySelectorAll('svg circle')).toHaveLength(9);

    await act(async () => { resolveDecode(); await decoded; });

    expect(image.classList.contains('on')).toBe(true);
    expect(container.querySelector('svg')).toBeNull();
  });

  it('декодирует уже загруженное изображение из кэша', async () => {
    vi.spyOn(HTMLImageElement.prototype, 'complete', 'get').mockReturnValue(true);
    vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(128);
    const decode = mockDecode(() => Promise.resolve());
    const { container } = render(<MediaThumb media={media} colorPlaceholder />);

    await waitFor(() => expect(container.querySelector('img')!.classList.contains('on')).toBe(true));

    expect(decode).toHaveBeenCalled();
    expect(container.querySelector('.thumb-color-ph')).toBeNull();
  });

  it('сохраняет плейсхолдер при ошибке декодирования', async () => {
    mockDecode(() => Promise.reject(new DOMException('Invalid image', 'EncodingError')));
    const { container } = render(<MediaThumb media={media} colorPlaceholder />);
    const image = container.querySelector('img')!;

    await act(async () => { fireEvent.load(image); });

    expect(image.classList.contains('on')).toBe(false);
    expect(container.querySelectorAll('.thumb-color-ph circle')).toHaveLength(9);
  });

  it('ошибка сети отменяет ещё не завершённое декодирование', async () => {
    let resolveDecode!: () => void;
    const decoded = new Promise<void>((resolve) => { resolveDecode = resolve; });
    mockDecode(() => decoded);
    const { container } = render(<MediaThumb media={media} colorPlaceholder />);
    const image = container.querySelector('img')!;
    fireEvent.load(image);
    fireEvent.error(image);

    await act(async () => { resolveDecode(); await decoded; });

    expect(image.classList.contains('on')).toBe(false);
    expect(container.querySelector('.thumb-color-ph')).not.toBeNull();
  });

  it('завершение старого запроса не показывает новый источник', async () => {
    let resolveOld!: () => void;
    const oldDecoded = new Promise<void>((resolve) => { resolveOld = resolve; });
    const decode = mockDecode(() => oldDecoded);
    const { container, rerender } = render(<MediaThumb media={media} colorPlaceholder />);
    const oldImage = container.querySelector('img')!;
    fireEvent.load(oldImage);
    rerender(<MediaThumb media={{ ...media, previews: [{ w: 128, target: 128, url: '/new.jpg' }] }} colorPlaceholder />);
    const newImage = container.querySelector('img')!;

    await act(async () => { resolveOld(); await oldDecoded; });

    expect(newImage).not.toBe(oldImage);
    expect(newImage.classList.contains('on')).toBe(false);
    expect(container.querySelector('.thumb-color-ph')).not.toBeNull();
    decode.mockResolvedValue(undefined);
    await act(async () => { fireEvent.load(newImage); });
    expect(newImage.classList.contains('on')).toBe(true);
    expect(container.querySelector('svg')).toBeNull();
  });

  it('освобождает SVG при размонтировании с незавершённым запросом', async () => {
    let resolveDecode!: () => void;
    const decoded = new Promise<void>((resolve) => { resolveDecode = resolve; });
    mockDecode(() => decoded);
    const { container, unmount } = render(<MediaThumb media={media} colorPlaceholder />);
    fireEvent.load(container.querySelector('img')!);
    unmount();
    await act(async () => { resolveDecode(); await decoded; });
    expect(container.childElementCount).toBe(0);
  });

  it('начинает новый цикл декодирования при смене sizes', async () => {
    const decode = mockDecode(() => Promise.resolve());
    const { container, rerender } = render(<MediaThumb media={media} sizes="128px" colorPlaceholder />);
    await act(async () => { fireEvent.load(container.querySelector('img')!); });
    expect(container.querySelector('.thumb-color-ph')).toBeNull();

    rerender(<MediaThumb media={media} sizes="512px" colorPlaceholder />);

    expect(container.querySelector('img')!.classList.contains('on')).toBe(false);
    expect(container.querySelector('.thumb-color-ph')).not.toBeNull();
    expect(decode).toHaveBeenCalledTimes(1);
  });

  it('сохраняет srcset, sizes и ленивую загрузку', () => {
    const { container } = render(<MediaThumb media={{ ...media, previews: [...media.previews, { w: 512, target: 512, url: '/big.jpg' }] }} sizes="240px" colorPlaceholder />);
    const image = container.querySelector('img')!;
    expect(image.getAttribute('srcset')).toBe('/preview.jpg 128w, /big.jpg 512w');
    expect(image.getAttribute('sizes')).toBe('240px');
    expect(image.getAttribute('loading')).toBe('lazy');
  });

  it('использует jpegViewUrl для небольшого фото без обычных превью', () => {
    const { container } = render(<MediaThumb media={{ ...media, previews: [], jpegViewUrl: '/jpeg-view.jpg' }} colorPlaceholder />);
    expect(container.querySelector('img')!.getAttribute('src')).toBe('/jpeg-view.jpg');
  });

  it.each([
    { colors: ['#AA5533'], aspectRatio: 1 },
    { colors: Array(9).fill('invalid') as string[], aspectRatio: 1 },
    { colors: media.placeholder.colors, aspectRatio: 0 },
  ])('использует обычный плейсхолдер для некорректных данных %j', (placeholder) => {
    const { container } = render(<MediaThumb media={{ ...media, placeholder }} colorPlaceholder />);
    expect(container.querySelector('.thumb-color-ph')).toBeNull();
    expect(container.querySelector('.thumb-ph')).not.toBeNull();
  });

  it('рисует широкое видео с центральным cover и отдельным фильтром на карточку', () => {
    const video = { ...media, kind: 'video' as const, placeholder: { ...media.placeholder, aspectRatio: 16 / 9 } };
    const { container } = render(<><MediaThumb media={video} colorPlaceholder /><MediaThumb media={video} colorPlaceholder /></>);
    const placeholders = container.querySelectorAll('.thumb-color-ph');
    expect(placeholders[0].getAttribute('viewBox')).toBe(`0 0 ${100 * 16 / 9} 100`);
    expect(placeholders[0].getAttribute('preserveAspectRatio')).toBe('xMidYMid slice');
    expect(placeholders[0].querySelector('circle')!.getAttribute('r')).toBe('35');
    expect(placeholders[0].querySelector('g')!.getAttribute('fill-opacity')).toBe('0.9');
    expect(placeholders[0].querySelector('feGaussianBlur')!.getAttribute('stdDeviation')).toBe('12');
    expect(placeholders[0].querySelector('filter')!.getAttribute('color-interpolation-filters')).toBe('sRGB');
    expect(placeholders[0].querySelector('filter')!.id).not.toBe(placeholders[1].querySelector('filter')!.id);
  });

  it('не включает цветной режим на остальных экранах', () => {
    const decode = mockDecode(() => Promise.resolve());
    const { container } = render(<MediaThumb media={media} />);
    const image = container.querySelector('img')!;
    fireEvent.load(image);
    expect(container.querySelector('.thumb-color-ph')).toBeNull();
    expect(image.classList.contains('on')).toBe(true);
    expect(decode).not.toHaveBeenCalled();
  });
});
