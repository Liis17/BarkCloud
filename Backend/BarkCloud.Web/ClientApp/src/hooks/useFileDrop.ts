import React from 'react';

/** Drag&drop файлов на контейнер. `over` — флаг подсветки, `dropHandlers` вешаются на корневой элемент.
 *  Счётчик глубины нужен, чтобы dragleave у вложенных элементов не сбрасывал подсветку раньше времени. */
export function useFileDrop(onFiles: (files: File[]) => void) {
  const [over, setOver] = React.useState(false);
  const depth = React.useRef(0);

  const hasFiles = (e: React.DragEvent) => Array.from(e.dataTransfer.types || []).includes('Files');

  const onDragEnter = React.useCallback((e: React.DragEvent) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth.current++;
    setOver(true);
  }, []);
  const onDragOver = React.useCallback((e: React.DragEvent) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
  }, []);
  const onDragLeave = React.useCallback((e: React.DragEvent) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth.current = Math.max(0, depth.current - 1);
    if (depth.current === 0) setOver(false);
  }, []);
  const onDrop = React.useCallback(
    (e: React.DragEvent) => {
      e.preventDefault();
      depth.current = 0;
      setOver(false);
      const files = Array.from(e.dataTransfer.files || []);
      if (files.length) onFiles(files);
    },
    [onFiles],
  );

  return { over, dropHandlers: { onDragEnter, onDragOver, onDragLeave, onDrop } };
}
