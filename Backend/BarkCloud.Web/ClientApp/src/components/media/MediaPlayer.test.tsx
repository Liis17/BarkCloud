import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { VideoPlayer, type VideoQuality } from './MediaPlayer';

const qualities: VideoQuality[] = [
  { height: 1080, width: 1920, fileSize: 8000, isOriginal: true, url: '/original.webm' },
  { height: 360, width: 640, fileSize: 1000, isOriginal: false, url: '/360.webm' },
  { height: 720, width: 1280, fileSize: 3000, isOriginal: false, url: '/720.webm' },
];

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  document.cookie = 'bc_video_quality=; Max-Age=0; Path=/';
});

beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue('probably');
});

describe('VideoPlayer quality selection', () => {
  it('hides AV1 variants when the browser does not report WebM AV1 support', () => {
    const canPlayType = vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue('');

    render(<VideoPlayer src="/original.webm" qualities={qualities} />);

    expect(canPlayType).toHaveBeenCalledWith('video/webm; codecs="av01, opus"');
    expect(screen.queryByRole('combobox', { name: 'Качество видео' })).toBeNull();
  });

  it('uses the nearest lower available quality from the saved cookie', () => {
    document.cookie = 'bc_video_quality=700; Path=/';
    const { container } = render(<VideoPlayer src="/original.webm" qualities={qualities.filter((quality) => quality.isOriginal || quality.height === 360)} />);

    expect(screen.getByRole('combobox', { name: 'Качество видео' })).toHaveProperty('value', '360');
    expect(container.querySelector('video')?.getAttribute('src')).toBe('/360.webm');
  });

  it('keeps playback position and paused state when switching sources', () => {
    const { container } = render(<VideoPlayer src="/original.webm" qualities={qualities} />);
    const video = container.querySelector('video')!;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 45 });
    Object.defineProperty(video, 'duration', { configurable: true, value: 120 });
    Object.defineProperty(video, 'paused', { configurable: true, value: false });
    video.play = vi.fn().mockResolvedValue(undefined);
    video.pause = vi.fn();

    expect(video.getAttribute('src')).toBe('/original.webm');
    fireEvent.change(screen.getByRole('combobox', { name: 'Качество видео' }), { target: { value: '360' } });
    expect(video.getAttribute('src')).toBe('/360.webm');
    fireEvent.loadedMetadata(video);

    expect(video.currentTime).toBe(45);
    expect(video.play).toHaveBeenCalledOnce();
    expect(document.cookie).toContain('bc_video_quality=360');
  });

  it('keeps a paused video paused when its source changes', () => {
    const { container } = render(<VideoPlayer src="/original.webm" qualities={qualities} />);
    const video = container.querySelector('video')!;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 18 });
    Object.defineProperty(video, 'duration', { configurable: true, value: 120 });
    Object.defineProperty(video, 'paused', { configurable: true, value: true });
    video.play = vi.fn().mockResolvedValue(undefined);
    video.pause = vi.fn();

    fireEvent.change(screen.getByRole('combobox', { name: 'Качество видео' }), { target: { value: '720' } });
    act(() => fireEvent.loadedMetadata(video));

    expect(video.currentTime).toBe(18);
    expect(video.pause).toHaveBeenCalledOnce();
    expect(video.play).not.toHaveBeenCalled();
  });
});
