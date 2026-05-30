import React from 'react';
import { Modal } from './Modal';
import { apiGet } from '../../lib/api';
import { fmtFull, kindRu } from '../../lib/format';
import type { CardFile, FileInfo, FileMetadata, MediaItem } from '../../lib/types';

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

  const metaRows = buildMetadataRows(info?.metadata);

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

      {metaRows.length > 0 && (
        <>
          <div className="prop-section">Метаданные</div>
          <div className="prop-grid">
            {metaRows.map(([k, v]) => (
              <React.Fragment key={k}>
                <div className="pk">{k}</div>
                <div className="pv">{v}</div>
              </React.Fragment>
            ))}
          </div>
        </>
      )}
    </Modal>
  );
}

/**
 * Превращает FileMetadata в плоский список «ключ → отображаемое значение».
 * Только заполненные поля; форматирование человекочитаемое.
 */
function buildMetadataRows(m: FileMetadata | null | undefined): Array<[string, React.ReactNode]> {
  if (!m) return [];
  const rows: Array<[string, React.ReactNode] | null> = [
    m.takenAt ? ['Дата съёмки', fmtFull(m.takenAt)] : null,
    m.cameraMake || m.cameraModel
      ? ['Устройство', [m.cameraMake, m.cameraModel].filter(Boolean).join(' ')]
      : null,
    m.lensModel ? ['Объектив', m.lensModel] : null,
    m.creatorTool ? ['Программа', m.creatorTool] : null,

    m.focalLengthMm != null ? ['Фокусное расстояние', m.focalLengthMm.toFixed(1) + ' мм'] : null,
    m.fNumber != null ? ['Диафрагма', 'f/' + m.fNumber.toFixed(1)] : null,
    m.exposureTimeSeconds != null ? ['Выдержка', fmtExposure(m.exposureTimeSeconds)] : null,
    m.iso != null ? ['ISO', String(m.iso)] : null,
    m.flash != null ? ['Вспышка', m.flash ? 'Сработала' : 'Не сработала'] : null,
    m.orientation != null ? ['Ориентация', String(m.orientation)] : null,

    // GPS — показываем как координаты + ссылку на карту.
    m.latitude != null && m.longitude != null
      ? ['Координаты', gpsCell(m.latitude, m.longitude)]
      : null,
    m.altitude != null ? ['Высота', m.altitude.toFixed(1) + ' м'] : null,

    m.durationSeconds != null ? ['Длительность', fmtDuration(m.durationSeconds)] : null,
    m.videoCodec ? ['Видеокодек', m.videoCodec] : null,
    m.audioCodec ? ['Аудиокодек', m.audioCodec] : null,
    m.frameRate != null ? ['Частота кадров', m.frameRate.toFixed(2) + ' fps'] : null,
    m.bitrate != null ? ['Битрейт', fmtBitrate(m.bitrate)] : null,

    m.documentAuthor ? ['Автор', m.documentAuthor] : null,
    m.documentTitle ? ['Заголовок', m.documentTitle] : null,
    m.documentSubject ? ['Тема', m.documentSubject] : null,
    m.documentPageCount != null ? ['Страниц', String(m.documentPageCount)] : null,
  ];
  return rows.filter((r): r is [string, React.ReactNode] => r != null);
}

function fmtExposure(seconds: number): string {
  if (seconds <= 0) return '—';
  if (seconds >= 1) return seconds.toFixed(1) + ' с';
  // Короткие выдержки приятнее показывать как 1/N — округление по обратной величине.
  const denom = Math.round(1 / seconds);
  return '1/' + denom + ' с';
}

function fmtDuration(seconds: number): string {
  const s = Math.round(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n: number) => (n < 10 ? '0' + n : String(n));
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
}

function fmtBitrate(bps: number): string {
  if (bps >= 1_000_000) return (bps / 1_000_000).toFixed(2) + ' Мбит/с';
  if (bps >= 1_000) return (bps / 1_000).toFixed(0) + ' Кбит/с';
  return bps + ' бит/с';
}

function gpsCell(lat: number, lon: number): React.ReactNode {
  const label = lat.toFixed(6) + ', ' + lon.toFixed(6);
  const url = `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=15/${lat}/${lon}`;
  return (
    <a href={url} target="_blank" rel="noopener noreferrer">
      {label}
    </a>
  );
}
