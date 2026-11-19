// Позиции «Продолжить просмотр» для видео. Хранятся в localStorage этого браузера:
// один ключ на пользователя, внутри — отметки по id файла.

const KEY_PREFIX = 'bark_video_resume:';
const MAX_ENTRIES = 200;
/** Короче этого видео не запоминаем. */
export const RESUME_MIN_DURATION = 60;
/** Позицию ближе к началу не запоминаем. */
const MIN_POSITION = 10;
/** Отметку у самого конца считаем досмотром: max(10 с, 5% длительности). */
const END_MARGIN_SECONDS = 10;
const END_MARGIN_RATIO = 0.05;

export interface ResumeTarget {
  userId: string;
  fileId: string;
}

interface ResumeEntry {
  /** Позиция, с. */
  t: number;
  /** Время сохранения, мс. */
  at: number;
}

type ResumeMap = Record<string, ResumeEntry>;

function readMap(userId: string): ResumeMap {
  try {
    const raw = localStorage.getItem(KEY_PREFIX + userId);
    const parsed = raw ? JSON.parse(raw) : null;
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function writeMap(userId: string, map: ResumeMap): void {
  try {
    if (Object.keys(map).length === 0) localStorage.removeItem(KEY_PREFIX + userId);
    else localStorage.setItem(KEY_PREFIX + userId, JSON.stringify(map));
  } catch {
    /* localStorage недоступен — отметка не сохраняется */
  }
}

/** Сохранённая позиция или null. */
export function readResumePosition({ userId, fileId }: ResumeTarget): number | null {
  const entry = readMap(userId)[fileId];
  return entry && Number.isFinite(entry.t) && entry.t >= MIN_POSITION ? entry.t : null;
}

export function clearResumePosition({ userId, fileId }: ResumeTarget): void {
  const map = readMap(userId);
  if (!(fileId in map)) return;
  delete map[fileId];
  writeMap(userId, map);
}

/** Запоминает позицию; у конца ролика очищает отметку, у начала и для коротких роликов ничего не делает. */
export function saveResumePosition(target: ResumeTarget, time: number, duration: number): void {
  if (!Number.isFinite(time) || !Number.isFinite(duration) || duration < RESUME_MIN_DURATION) return;
  if (time >= duration - Math.max(END_MARGIN_SECONDS, duration * END_MARGIN_RATIO)) {
    clearResumePosition(target);
    return;
  }
  if (time < MIN_POSITION) return;
  const map = readMap(target.userId);
  map[target.fileId] = { t: Math.floor(time), at: Date.now() };
  const ids = Object.keys(map);
  if (ids.length > MAX_ENTRIES) {
    ids.sort((a, b) => (map[a].at || 0) - (map[b].at || 0))
      .slice(0, ids.length - MAX_ENTRIES)
      .forEach((id) => delete map[id]);
  }
  writeMap(target.userId, map);
}
