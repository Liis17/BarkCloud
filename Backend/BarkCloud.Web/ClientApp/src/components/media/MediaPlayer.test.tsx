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

describe('VideoPlayer extra controls', () => {
  it('sets playback speed as the default rate so it survives source changes', () => {
    const { container } = render(<VideoPlayer src="/original.webm" qualities={qualities} />);
    const video = container.querySelector('video')!;

    fireEvent.change(screen.getByRole('combobox', { name: 'Скорость воспроизведения' }), { target: { value: '1.5' } });

    expect(video.playbackRate).toBe(1.5);
    expect(video.defaultPlaybackRate).toBe(1.5);
  });

  it('shows picture-in-picture only when the browser supports it', () => {
    const { unmount } = render(<VideoPlayer src="/original.webm" />);
    expect(screen.queryByRole('button', { name: 'Картинка в картинке' })).toBeNull();
    unmount();

    Object.defineProperty(document, 'pictureInPictureEnabled', { configurable: true, value: true });
    try {
      const { container } = render(<VideoPlayer src="/original.webm" />);
      const video = container.querySelector('video')!;
      video.requestPictureInPicture = vi.fn().mockResolvedValue({});

      fireEvent.click(screen.getByRole('button', { name: 'Картинка в картинке' }));
      expect(video.requestPictureInPicture).toHaveBeenCalledOnce();

      fireEvent(video, new Event('enterpictureinpicture'));
      expect(screen.getByRole('button', { name: 'Вернуть из окна' })).toBeTruthy();
    } finally {
      delete (document as { pictureInPictureEnabled?: boolean }).pictureInPictureEnabled;
    }
  });

  it('loads a local SRT file as a showing subtitle track and drops it for another video', async () => {
    const createObjectURL = vi.fn().mockReturnValue('blob:subs');
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    const { container, rerender } = render(<VideoPlayer src="/a.webm" />);
    const video = container.querySelector('video')!;
    const track = { mode: 'disabled' };
    Object.defineProperty(video, 'textTracks', { configurable: true, value: [track] });

    const text = '1\n00:00:01,000 --> 00:00:02,000\nПривет\n';
    // В jsdom у File нет arrayBuffer().
    const srt = Object.assign(new File([text], 'a.srt'), { arrayBuffer: async () => new TextEncoder().encode(text).buffer });
    await act(async () => {
      fireEvent.change(screen.getByLabelText('Файл субтитров'), { target: { files: [srt] } });
    });

    expect(createObjectURL.mock.calls[0][0].type).toBe('text/vtt');
    expect(container.querySelector('track')?.getAttribute('src')).toBe('blob:subs');
    expect(track.mode).toBe('showing');

    rerender(<VideoPlayer src="/b.webm" />);
    expect(container.querySelector('track')).toBeNull();
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:subs');
    delete (URL as { createObjectURL?: unknown }).createObjectURL;
    delete (URL as { revokeObjectURL?: unknown }).revokeObjectURL;
  });
});

describe('VideoPlayer continue watching', () => {
  const resume = { userId: '7', fileId: 'f1' };
  const stored = () => JSON.parse(localStorage.getItem('bark_video_resume:7') || '{}');

  function mockVideo(video: HTMLVideoElement, time: number, duration: number) {
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: time });
    Object.defineProperty(video, 'duration', { configurable: true, value: duration });
    video.play = vi.fn().mockResolvedValue(undefined);
  }

  const storedValues = new Map<string, string>();
  beforeEach(() => {
    storedValues.clear();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => storedValues.get(key) ?? null,
      setItem: (key: string, value: string) => storedValues.set(key, value),
      removeItem: (key: string) => storedValues.delete(key),
    });
  });
  afterEach(() => vi.unstubAllGlobals());

  it('offers to continue from the saved position and seeks there', () => {
    localStorage.setItem('bark_video_resume:7', JSON.stringify({ f1: { t: 1122, at: 1 } }));
    const { container } = render(<VideoPlayer src="/a.webm" resume={resume} />);
    const video = container.querySelector('video')!;
    mockVideo(video, 0, 3600);

    fireEvent.click(screen.getByRole('button', { name: /Продолжить с 18:42/ }));

    expect(video.currentTime).toBe(1122);
    expect(video.play).toHaveBeenCalledOnce();
    expect(screen.queryByRole('button', { name: /Продолжить/ })).toBeNull();
  });

  it('saves the position on close and clears it after the video ends', () => {
    const { container, unmount } = render(<VideoPlayer src="/a.webm" resume={resume} />);
    const video = container.querySelector('video')!;
    mockVideo(video, 300.4, 3600);
    fireEvent.timeUpdate(video);
    video.currentTime = 420;
    fireEvent.timeUpdate(video);
    unmount();
    expect(stored().f1.t).toBe(420);

    const second = render(<VideoPlayer src="/a.webm" resume={resume} />);
    expect(screen.getByRole('button', { name: /Продолжить с 7:00/ })).toBeTruthy();
    fireEvent.ended(second.container.querySelector('video')!);
    expect(stored().f1).toBeUndefined();
    expect(screen.queryByRole('button', { name: /Продолжить/ })).toBeNull();
  });

  it('does not remember short videos or positions near the start and end', () => {
    const { container, unmount } = render(<VideoPlayer src="/a.webm" resume={resume} />);
    const video = container.querySelector('video')!;

    mockVideo(video, 30, 50);
    fireEvent.pause(video);
    mockVideo(video, 5, 3600);
    fireEvent.pause(video);
    expect(stored().f1).toBeUndefined();

    mockVideo(video, 600, 3600);
    fireEvent.pause(video);
    expect(stored().f1.t).toBe(600);
    mockVideo(video, 3590, 3600);
    fireEvent.timeUpdate(video);
    unmount();
    expect(stored().f1).toBeUndefined();
  });

  it('keeps separate positions per user and does not save without a resume target', () => {
    localStorage.setItem('bark_video_resume:8', JSON.stringify({ f1: { t: 900, at: 1 } }));
    render(<VideoPlayer src="/a.webm" resume={resume} />);
    expect(screen.queryByRole('button', { name: /Продолжить/ })).toBeNull();
    cleanup();

    const { container, unmount } = render(<VideoPlayer src="/a.webm" />);
    const video = container.querySelector('video')!;
    mockVideo(video, 600, 3600);
    fireEvent.pause(video);
    unmount();
    expect(localStorage.getItem('bark_video_resume:7')).toBeNull();
  });
});
