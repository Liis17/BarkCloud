import React from 'react';
import { Modal } from './Modal';
import { apiGet, apiPut } from '../../lib/api';
import { fmtFull, kindRu } from '../../lib/format';
import type { CardFile, FileActivity, FileInfo, FileMetadata, MediaItem, Page } from '../../lib/types';

interface PropertiesModalProps {
  fileId: string;
  fallback?: Partial<MediaItem> & Partial<CardFile>;
  onClose?: () => void;
}

/** Свойства файла — отдельный запрос GetFileData (/api/files/info), пока грузит — данные из карточки. */
export function PropertiesModal({ fileId, fallback, onClose }: PropertiesModalProps) {
  const [info, setInfo] = React.useState<FileInfo | null>(null);
  const [activity, setActivity] = React.useState<FileActivity[]>([]);
  const [activityErr, setActivityErr] = React.useState<string | null>(null);
  const [err, setErr] = React.useState<string | null>(null);
  React.useEffect(() => {
    let alive = true;
    setInfo(null);
    setActivity([]);
    setErr(null);
    setActivityErr(null);
    apiGet<FileInfo>('/api/files/info?id=' + encodeURIComponent(fileId))
      .then((d) => {
        if (alive) setInfo(d);
      })
      .catch((e) => {
        if (alive) setErr(e.message);
      });
    apiGet<Page<FileActivity>>('/api/files/activity?id=' + encodeURIComponent(fileId) + '&limit=30')
      .then((d) => {
        if (alive) setActivity(d.items || []);
      })
      .catch((e) => {
        if (alive) setActivityErr(e.message);
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
  const meta = info?.metadata;
  const hasGeo = meta?.latitude != null && meta?.longitude != null;

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

      {info && <SearchMetadataEditor fileId={fileId} initialAlias={info.searchAlias || ''} initialTags={info.tags || []} />}

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

      {hasGeo && (
        <>
          <div className="prop-section">Место съёмки</div>
          <FileLocationMap lat={meta!.latitude!} lon={meta!.longitude!} />
        </>
      )}

      <div className="prop-section">История</div>
      {activityErr && <div className="prop-err">История недоступна: {activityErr}</div>}
      {!activityErr && activity.length === 0 && <div className="prop-empty">Событий пока нет</div>}
      {activity.length > 0 && (
        <div className="prop-activity">
          {activity.map((item) => (
            <div className="prop-activity-row" key={item.id}>
              <div className="pa-dot" />
              <div className="pa-main">
                <div className="pa-text">{item.summary || activityLabel(item.kind)}</div>
                <div className="pa-time">{fmtFull(item.createdAt)}</div>
              </div>
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}

function SearchMetadataEditor({ fileId, initialAlias, initialTags }: { fileId: string; initialAlias: string; initialTags: string[] }) {
  const [alias, setAlias] = React.useState(initialAlias);
  const [tags, setTags] = React.useState(initialTags);
  const [tag, setTag] = React.useState('');
  const [saving, setSaving] = React.useState(false);
  const [message, setMessage] = React.useState<string | null>(null);

  React.useEffect(() => {
    setAlias(initialAlias);
    setTags(initialTags);
    setTag('');
    setMessage(null);
  }, [fileId, initialAlias, initialTags]);

  function addTags(value: string) {
    const incoming = value.split(',').map((item) => item.trim()).filter(Boolean);
    if (!incoming.length) return;
    const merged = [...tags];
    for (const item of incoming) {
      if (item.length > 50 || merged.some((existing) => existing.localeCompare(item, undefined, { sensitivity: 'accent' }) === 0)) continue;
      if (merged.length < 20) merged.push(item);
    }
    setTags(merged);
    setTag('');
  }

  async function save() {
    setSaving(true);
    setMessage(null);
    try {
      const saved = await apiPut<{ alias: string; tags: string[] }>(`/api/files/${encodeURIComponent(fileId)}/search-metadata`, { alias: alias.trim(), tags });
      setAlias(saved.alias || '');
      setTags(saved.tags || []);
      setMessage('Сохранено');
    } catch (e) {
      setMessage((e as Error).message || 'Не удалось сохранить');
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="prop-search-meta">
      <div className="prop-section">Поиск</div>
      <label className="prop-search-label">
        <span>Имя для поиска</span>
        <input value={alias} maxLength={120} onChange={(e) => setAlias(e.target.value)} placeholder="Например, Настя" />
        <small>Настоящее имя файла не изменится.</small>
      </label>
      <label className="prop-search-label">
        <span>Теги</span>
        <div className="prop-tag-list">
          {tags.map((item) => <button type="button" className="prop-tag" key={item.toLocaleLowerCase()} onClick={() => setTags(tags.filter((tagValue) => tagValue !== item))}>{item} ×</button>)}
        </div>
        <input
          value={tag}
          maxLength={50}
          placeholder="Добавить тег"
          onChange={(e) => setTag(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addTags(tag); }
          }}
          onBlur={() => addTags(tag)}
        />
        <small>Добавляйте Enter или запятой, максимум 20 тегов.</small>
      </label>
      <div className="prop-search-actions">
        <button className="btn outlined" type="button" disabled={saving} onClick={save}>{saving ? 'Сохраняем…' : 'Сохранить'}</button>
        {message && <span className={message === 'Сохранено' ? 'prop-search-ok' : 'prop-err'}>{message}</span>}
      </div>
    </section>
  );
}

function activityLabel(kind: string): string {
  switch (kind) {
    case 'uploaded': return 'Файл загружен';
    case 'attached': return 'Добавлен в папку';
    case 'renamed': return 'Переименован';
    case 'moved': return 'Перемещён';
    case 'deleted': return 'Перемещён в корзину';
    case 'restored': return 'Восстановлен';
    case 'purged': return 'Удалён навсегда';
    case 'favorite_added': return 'Добавлен в избранное';
    case 'favorite_removed': return 'Убран из избранного';
    case 'share_created': return 'Создана публичная ссылка';
    case 'share_revoked': return 'Публичная ссылка отозвана';
    case 'shared_with_user': return 'Выдан доступ пользователю';
    case 'user_share_revoked': return 'Доступ пользователя отозван';
    case 'album_added': return 'Добавлен в альбом';
    case 'album_removed': return 'Убран из альбома';
    default: return 'Событие файла';
  }
}

/** Мини-карта с маркером в точке съёмки (встроенный OpenStreetMap, без JS-зависимостей). */
function FileLocationMap({ lat, lon }: { lat: number; lon: number }) {
  const d = 0.008; // полуразмер видимой области в градусах (~900 м) — задаёт стартовый зум
  const bbox = `${lon - d},${lat - d},${lon + d},${lat + d}`;
  const src =
    `https://www.openstreetmap.org/export/embed.html?bbox=${bbox}&layer=mapnik&marker=${lat},${lon}`;
  return (
    <div className="prop-map">
      <iframe
        src={src}
        title="Карта места съёмки"
        loading="lazy"
        referrerPolicy="no-referrer"
      />
    </div>
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
