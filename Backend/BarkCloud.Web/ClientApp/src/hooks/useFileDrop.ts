import React from 'react';
import { walkEntries, type FolderTree } from '../lib/folderUpload';

export interface FileDropTarget {
  id: string;
  label: string;
}

export type FileDropHandlers = {
  onDragEnter: (event: React.DragEvent<HTMLElement>) => void;
  onDragOver: (event: React.DragEvent<HTMLElement>) => void;
  onDragLeave: (event: React.DragEvent<HTMLElement>) => void;
  onDrop: (event: React.DragEvent<HTMLElement>) => void;
};

function isNode(value: EventTarget | null): value is Node {
  return typeof Node !== 'undefined' && value instanceof Node;
}

function contains(container: EventTarget | null, value: EventTarget | null): boolean {
  return isNode(container) && isNode(value) && container.contains(value);
}

/** Тип данных перетаскивания записей облака внутри страницы (перемещение, а не загрузка). */
export const CLOUD_ITEMS_MIME = 'application/x-barkcloud-items';

function hasFiles(event: React.DragEvent<HTMLElement>): boolean {
  const types = Array.from(event.dataTransfer.types || []);
  // Chrome добавляет «Files» и при перетаскивании превью-картинки со страницы: такой drag — перемещение, не загрузка.
  return types.includes('Files') && !types.includes(CLOUD_ITEMS_MIME);
}

/**
 * Общая drag&drop-механика для страниц библиотеки.
 * Состояние сбрасывается не только при обычном dragleave, потому что браузер
 * может не прислать его при выходе файла за окно приложения.
 */
export function useFileDrop(
  /** tree — пути папок, если брошены папки; страницы без иерархии получают просто все файлы. */
  onFiles: (files: File[], target: FileDropTarget | null, tree?: FolderTree) => void,
) {
  const [over, setOver] = React.useState(false);
  const [target, setTarget] = React.useState<FileDropTarget | null>(null);
  const depth = React.useRef(0);
  const handledDrops = React.useRef(new WeakSet<Event>());
  const pendingLeaveReset = React.useRef<number | null>(null);

  const reset = React.useCallback(() => {
    if (pendingLeaveReset.current !== null) {
      window.clearTimeout(pendingLeaveReset.current);
      pendingLeaveReset.current = null;
    }
    depth.current = 0;
    setOver(false);
    setTarget(null);
  }, []);

  const cancelPendingLeaveReset = React.useCallback(() => {
    if (pendingLeaveReset.current !== null) {
      window.clearTimeout(pendingLeaveReset.current);
      pendingLeaveReset.current = null;
    }
  }, []);

  const scheduleLeaveReset = React.useCallback(() => {
    if (pendingLeaveReset.current !== null) return;
    pendingLeaveReset.current = window.setTimeout(() => {
      pendingLeaveReset.current = null;
      reset();
    }, 0);
  }, [reset]);

  const processDrop = React.useCallback((event: React.DragEvent<HTMLElement>, dropTarget: FileDropTarget | null) => {
    if (handledDrops.current.has(event.nativeEvent)) return;
    handledDrops.current.add(event.nativeEvent);

    event.preventDefault();
    reset();
    // Записи берутся синхронно: после обработчика drop DataTransfer уже пуст.
    const entries = Array.from(event.dataTransfer.items || [])
      .map((item) => (item.kind === 'file' ? item.webkitGetAsEntry?.() : null))
      .filter((entry): entry is FileSystemEntry => !!entry);
    if (entries.some((entry) => entry.isDirectory)) {
      // Брошены папки: файлы приходят из обхода дерева (сама папка в files — пустая псевдозапись).
      void walkEntries(entries).then(({ files, tree }) => {
        if (files.length) onFiles(files, dropTarget, tree);
      });
      return;
    }
    const files = Array.from(event.dataTransfer.files || []);
    if (files.length) onFiles(files, dropTarget);
  }, [onFiles, reset]);

  const onDragEnter = React.useCallback((event: React.DragEvent<HTMLElement>) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    cancelPendingLeaveReset();
    if (!contains(event.currentTarget, event.relatedTarget)) depth.current = 0;
    depth.current += 1;
    setOver(true);
  }, [cancelPendingLeaveReset]);

  const onDragOver = React.useCallback((event: React.DragEvent<HTMLElement>) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
  }, []);

  const onDragLeave = React.useCallback((event: React.DragEvent<HTMLElement>) => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    if (event.relatedTarget && !contains(event.currentTarget, event.relatedTarget)) {
      reset();
      return;
    }
    if (!event.relatedTarget && event.target === event.currentTarget) {
      reset();
      return;
    }
    if (!event.relatedTarget) scheduleLeaveReset();
    depth.current = Math.max(0, depth.current - 1);
  }, [reset, scheduleLeaveReset]);

  const onDrop = React.useCallback((event: React.DragEvent<HTMLElement>) => {
    processDrop(event, null);
  }, [processDrop]);

  const dropHandlers: FileDropHandlers = { onDragEnter, onDragOver, onDragLeave, onDrop };

  const getTargetHandlers = React.useCallback((dropTarget: FileDropTarget): FileDropHandlers => ({
    onDragEnter: (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      cancelPendingLeaveReset();
      setOver(true);
      setTarget(dropTarget);
    },
    onDragOver: (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      cancelPendingLeaveReset();
      setOver(true);
      setTarget(dropTarget);
    },
    onDragLeave: (event) => {
      if (!hasFiles(event)) return;
      event.preventDefault();
      if (!event.relatedTarget) {
        setTarget(null);
      } else if (!contains(event.currentTarget, event.relatedTarget)) {
        if (contains(event.currentTarget.parentElement, event.relatedTarget)) setTarget(null);
        else reset();
      }
    },
    onDrop: (event) => {
      event.stopPropagation();
      processDrop(event, dropTarget);
    },
  }), [cancelPendingLeaveReset, processDrop, reset]);

  React.useEffect(() => {
    const resetOnWindowEvent = () => reset();
    const resetOnDocumentDragLeave = (event: DragEvent) => {
      if (!event.relatedTarget && (event.target === document || event.target === document.documentElement || event.target === document.body)) {
        reset();
      }
    };

    window.addEventListener('dragend', resetOnWindowEvent);
    window.addEventListener('drop', resetOnWindowEvent);
    window.addEventListener('blur', resetOnWindowEvent);
    document.addEventListener('dragleave', resetOnDocumentDragLeave);

    return () => {
      window.removeEventListener('dragend', resetOnWindowEvent);
      window.removeEventListener('drop', resetOnWindowEvent);
      window.removeEventListener('blur', resetOnWindowEvent);
      document.removeEventListener('dragleave', resetOnDocumentDragLeave);
      reset();
    };
  }, [reset]);

  return { over, target, dropHandlers, getTargetHandlers };
}
