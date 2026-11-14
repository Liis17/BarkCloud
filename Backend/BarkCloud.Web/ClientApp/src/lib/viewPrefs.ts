// Вид раскладки страниц, сохраняется в localStorage этого браузера (как тема в theme.ts).
import React from 'react';

/** Видео: карточки с метаданными или квадратная сетка как в «Фото». */
export type VideoLayout = 'cards' | 'grid';
/** Файлы: таблица или сетка плиток. */
export type FilesView = 'list' | 'grid';

export const VIDEO_LAYOUT_KEY = 'bark_video_layout';
export const FILES_VIEW_KEY = 'bark_files_view';

function read<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const value = localStorage.getItem(key) as T | null;
    return value && allowed.includes(value) ? value : fallback;
  } catch {
    return fallback;
  }
}

function write(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* localStorage недоступен — выбор действует до перезагрузки */
  }
}

/** Значение настройки в state компонента с сохранением при смене. */
export function useStoredChoice<T extends string>(key: string, allowed: readonly T[], fallback: T) {
  const [value, setValue] = React.useState<T>(() => read(key, allowed, fallback));
  const set = React.useCallback((next: T) => {
    setValue(next);
    write(key, next);
  }, [key]);
  return [value, set] as const;
}

export const VIDEO_LAYOUTS: readonly VideoLayout[] = ['cards', 'grid'];
export const FILES_VIEWS: readonly FilesView[] = ['list', 'grid'];
