import React from 'react';
import { createPortal } from 'react-dom';
import { Modal } from '../ui/Modal';
import { Icon } from '../Icon';
import { fmtSize } from '../public/publicFile';
import { decodeTextFile, getTextFileKind, MAX_TEXT_FILE_BYTES, type TextEncoding } from '../../lib/textFiles';
import type { JsonFormatResult } from '../../lib/formatJson';
import { useDocumentHead } from '../../hooks/useDocumentHead';
import '../../styles/text-viewer.css';

const TextSource = React.lazy(() => import('./TextSource'));
const MarkdownPreview = React.lazy(() => import('./MarkdownPreview'));

export interface TextPreviewFile {
  name: string;
  size?: number;
  contentUrl: string;
  onDownload: () => void | Promise<void>;
}

export function TextFileViewer({ file, onClose }: { file: TextPreviewFile; onClose: () => void }) {
  const kind = getTextFileKind(file.name);
  const [bytes, setBytes] = React.useState<ArrayBuffer | null>(null);
  const [loadError, setLoadError] = React.useState('');
  const [attempt, setAttempt] = React.useState(0);
  const [encoding, setEncoding] = React.useState<TextEncoding>('auto');
  const [rendered, setRendered] = React.useState(kind === 'markdown');
  const [wrap, setWrap] = React.useState(false);
  const [formatted, setFormatted] = React.useState<string | null>(null);
  const [formatError, setFormatError] = React.useState('');
  const [formatting, setFormatting] = React.useState(false);
  const [downloadError, setDownloadError] = React.useState('');
  const worker = React.useRef<Worker | null>(null);
  useDocumentHead(() => ({ title: file.name }), [file.name], 30);

  React.useEffect(() => {
    const controller = new AbortController();
    setBytes(null); setLoadError('');
    if (file.size !== undefined && file.size > MAX_TEXT_FILE_BYTES) {
      setLoadError('Файл больше 10 МиБ. Для просмотра скачайте его.');
      return () => controller.abort();
    }
    fetch(file.contentUrl, { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) {
          const error = await response.json().catch(() => null);
          throw new Error(error?.error || 'Не удалось загрузить файл');
        }
        const data = await response.arrayBuffer();
        if (data.byteLength > MAX_TEXT_FILE_BYTES) throw new Error('Файл больше 10 МиБ. Для просмотра скачайте его.');
        if (!controller.signal.aborted) setBytes(data);
      })
      .catch((error: Error) => { if (!controller.signal.aborted) setLoadError(error.message); });
    return () => controller.abort();
  }, [file.contentUrl, file.size, attempt]);

  React.useEffect(() => {
    setFormatted(null); setFormatError(''); setFormatting(false);
    worker.current?.terminate(); worker.current = null;
    return () => { worker.current?.terminate(); worker.current = null; };
  }, [bytes, encoding]);

  const decoded = React.useMemo(() => {
    if (!bytes) return { text: '', encoding: '', error: '' };
    try { return { ...decodeTextFile(bytes, encoding), error: '' }; }
    catch (error) { return { text: '', encoding: '', error: (error as Error).message }; }
  }, [bytes, encoding]);

  function format() {
    setFormatError(''); setFormatting(true);
    worker.current?.terminate();
    const next = new Worker(new URL('../../lib/formatJson.worker.ts', import.meta.url), { type: 'module' });
    worker.current = next;
    next.onmessage = (event: MessageEvent<JsonFormatResult>) => {
      if (worker.current !== next) return;
      setFormatting(false);
      if (event.data.error) setFormatError(event.data.error);
      else setFormatted(event.data.text ?? null);
      next.terminate(); worker.current = null;
    };
    next.onerror = () => {
      if (worker.current !== next) return;
      setFormatting(false); setFormatError('Не удалось форматировать JSON. Исходник сохранён.');
      next.terminate(); worker.current = null;
    };
    next.postMessage(decoded.text);
  }

  async function download() {
    setDownloadError('');
    try { await file.onDownload(); }
    catch (error) { setDownloadError((error as Error).message || 'Не удалось скачать файл'); }
  }

  const ready = bytes !== null && !decoded.error;
  return createPortal(
    <Modal title={file.name} className="text-file-modal" onClose={onClose} actions={<>
      <button className="btn outlined" onClick={download}><Icon.download size={16} /> Скачать</button>
      <button className="btn text" onClick={onClose}>Закрыть</button>
    </>}>
      <div className="text-file-toolbar">
        <span className="text-file-size">{fmtSize(bytes?.byteLength ?? file.size ?? 0)}</span>
        {kind === 'markdown' && <div className="text-file-tabs" role="group" aria-label="Режим просмотра">
          <button className="btn text" aria-pressed={rendered} onClick={() => setRendered(true)}>Разметка</button>
          <button className="btn text" aria-pressed={!rendered} onClick={() => setRendered(false)}>Исходник</button>
        </div>}
        {kind === 'json' && <>
          <button className="btn text" disabled={!ready || formatting || decoded.text === ''} onClick={format}>{formatting ? 'Форматируем…' : 'Форматировать'}</button>
          {formatted !== null && <button className="btn text" onClick={() => setFormatted(null)}>Исходник</button>}
        </>}
        {(kind !== 'markdown' || !rendered) && <button className="btn text" aria-pressed={wrap} onClick={() => setWrap((value) => !value)}>Перенос строк</button>}
        <label className="text-file-encoding">Кодировка
          <select aria-label="Кодировка" value={encoding} onChange={(event) => setEncoding(event.target.value as TextEncoding)}>
            <option value="auto">Авто{decoded.encoding ? ` · ${decoded.encoding.toUpperCase()}` : ''}</option>
            <option value="utf-8">UTF-8</option><option value="utf-16le">UTF-16 LE</option>
            <option value="utf-16be">UTF-16 BE</option><option value="windows-1251">Windows-1251</option>
          </select>
        </label>
      </div>
      {(formatError || downloadError) && <div className="text-file-error" role="alert">{formatError || downloadError}</div>}
      <div className="text-file-content">
        {loadError ? <div className="text-file-status" role="alert"><p>{loadError}</p>
          <button className="btn text" onClick={() => setAttempt((value) => value + 1)}>Повторить</button></div>
          : bytes === null ? <div className="text-file-status" role="status">Загружаем файл…</div>
          : decoded.error ? <div className="text-file-status text-file-error" role="alert">{decoded.error}</div>
          : decoded.text === '' ? <div className="text-file-status">Файл пуст</div>
          : <React.Suspense fallback={<div className="text-file-status" role="status">Подготавливаем просмотр…</div>}>
            {kind === 'markdown' && rendered ? <MarkdownPreview text={decoded.text} />
              : <TextSource text={formatted ?? decoded.text} name={file.name} wrap={wrap} />}
          </React.Suspense>}
      </div>
    </Modal>, document.body,
  );
}
