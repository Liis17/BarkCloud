import React from 'react';
import { TextFileViewer, type TextPreviewFile } from '../components/text/TextFileViewer';
import { getTextFileKind } from '../lib/textFiles';

/** Один общий вьювер; страницы передают контекст доступа и действие скачивания оригинала. */
export function useTextFileViewer() {
  const [file, setFile] = React.useState<TextPreviewFile | null>(null);
  const close = React.useCallback(() => setFile(null), []);
  const openTextFile = React.useCallback((target: TextPreviewFile): boolean => {
    if (!getTextFileKind(target.name)) return false;
    setFile(target);
    return true;
  }, []);
  return { openTextFile, closeTextFile: close, textViewer: file && <TextFileViewer key={file.contentUrl + file.name} file={file} onClose={close} /> };
}
