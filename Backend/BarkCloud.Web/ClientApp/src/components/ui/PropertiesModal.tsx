import React from 'react';
import { Modal } from './Modal';
import { apiGet } from '../../lib/api';
import { fmtFull, kindRu } from '../../lib/format';
import type { CardFile, FileInfo, MediaItem } from '../../lib/types';

interface PropertiesModalProps {
  fileId: string;
  fallback?: Partial<MediaItem> & Partial<CardFile>;
  onClose?: () => void;
}

/** Свойства файла — отдельный запрос GetFileData (/api/files/info), пока грузит — данные из карточки. */
export function PropertiesModal({ fileId, fallback, onClose }: PropertiesModalProps) {
  const [info, setInfo] = React.useState<FileInfo | null>(null);
  const [err, setErr] = React.useState<string | null>(null);
  React.useEffect(() => {
    let alive = true;
    apiGet<FileInfo>('/api/files/info?id=' + encodeURIComponent(fileId))
      .then((d) => {
        if (alive) setInfo(d);
      })
      .catch((e) => {
        if (alive) setErr(e.message);
      });
    return () => {
      alive = false;
    };
  }, [fileId]);

  const f = (info || fallback || {}) as Partial<FileInfo>;
  const fb = (fallback || {}) as Partial<MediaItem>;
  const rows: Array<[string, React.ReactNode] | null> = [
    ['Имя', f.name],
    ['Тип', kindRu(f.kind)],
    ['Размер', f.sizeLabel || (f.size != null ? f.size + ' Б' : '—')],
    f.width && f.height ? ['Разрешение', f.width + ' × ' + f.height + ' px'] : null,
    ['Создан', fmtFull(f.createdAt)],
    ['Дата загрузки', fmtFull(f.uploadedAt)],
    info?.uploadDeviceName ? ['Устройство загрузки', info.uploadDeviceName] : null,
    info && info.etag ? ['ETag', info.etag] : null,
    fb.entryNames && fb.entryNames.length ? ['Имя в папке', fb.entryNames[0]] : null,
    ['ID', f.id || fileId],
  ];

  return (
    <Modal title="Свойства" onClose={onClose} actions={<button className="btn primary" onClick={onClose}>Закрыть</button>}>
      {err && <div className="prop-err">Полные данные недоступны: {err}</div>}
      <div className="prop-grid">
        {rows.filter((r): r is [string, React.ReactNode] => r != null).map(([k, v]) => (
          <React.Fragment key={k}>
            <div className="pk">{k}</div>
            <div className="pv">{v == null || v === '' ? '—' : v}</div>
          </React.Fragment>
        ))}
      </div>
    </Modal>
  );
}
