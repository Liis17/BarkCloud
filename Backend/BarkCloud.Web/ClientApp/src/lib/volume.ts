// Глобальная громкость видео. Хранится в cookie `bark_vol`, чтобы переноситься
// между всеми плеерами: облачный Lightbox и публичные страницы шаринга (/v, /f, /al).

const COOKIE = 'bark_vol';
const MAX_AGE = 60 * 60 * 24 * 365; // 1 год

interface VolumeState {
  volume: number; // 0..1
  muted: boolean;
}

function read(): VolumeState | null {
  const m = document.cookie.match(/(?:^|;\s*)bark_vol=([^;]+)/);
  if (!m) return null;
  try {
    const p = JSON.parse(decodeURIComponent(m[1]));
    const volume = Math.min(1, Math.max(0, Number(p.v)));
    if (Number.isNaN(volume)) return null;
    return { volume, muted: !!p.m };
  } catch {
    return null;
  }
}

function write(state: VolumeState) {
  const payload = encodeURIComponent(JSON.stringify({ v: state.volume, m: state.muted }));
  document.cookie = `${COOKIE}=${payload}; path=/; max-age=${MAX_AGE}; samesite=lax`;
}

/** ref-callback для <video>: при монтировании выставляет сохранённую громкость и
 *  подписывается на её изменения (одноразово на элемент). Слушатель живёт вместе
 *  с элементом, поэтому отдельная очистка не нужна. */
export function persistVolumeRef(video: HTMLVideoElement | null): void {
  if (!video || (video as unknown as { _volBound?: boolean })._volBound) return;
  (video as unknown as { _volBound?: boolean })._volBound = true;

  const saved = read();
  if (saved) {
    video.volume = saved.volume;
    video.muted = saved.muted;
  }
  video.addEventListener('volumechange', () => write({ volume: video.volume, muted: video.muted }));
}
