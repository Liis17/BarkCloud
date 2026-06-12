import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { persistVolumeRef } from '../lib/volume';

interface ShareInfo {
  found: boolean;
  name: string;
  mediaKind: string;
  previewUrl: string;
  downloadUrl: string;
  imageWidth: number;
  imageHeight: number;
  fileSize: number;
  downloadPath: string;
}

function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

/** Публичная страница просмотра файла по шаринг-ссылке (/v/:token). Без авторизации. */
export function PublicViewPage() {
  const { token } = useParams<{ token: string }>();
  const [state, setState] = React.useState<'loading' | 'notfound' | ShareInfo>('loading');
  const headInfo = typeof state === 'object' ? state : null;
  const headTitle = headInfo ? headInfo.name : state === 'notfound' ? 'Ссылка недоступна' : 'Публичный файл';
  const headIconUrl = headInfo?.previewUrl || null;

  useDocumentHead(
    () => ({ title: headTitle, iconUrl: headIconUrl }),
    [headTitle, headIconUrl],
  );

  React.useEffect(() => {
    let alive = true;
    // Намеренно plain fetch (не авторизованный api(), который редиректит на /login при 401).
    fetch(`/s/${token}/info`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('not found'))))
      .then((d: ShareInfo) => alive && setState(d.found ? d : 'notfound'))
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
  }, [token]);

  if (state === 'loading') {
    return <PublicStatus icon={Icon.cloud} title="Открываем файл" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        icon={Icon.link}
        title="Ссылка недоступна"
        text="Файл не найден или владелец отозвал доступ."
      />
    );
  }

  const info = state;
  const isVideo = info.mediaKind === 'video';
  const hasPreview = (info.mediaKind === 'photo' || info.mediaKind === 'video') && !!info.previewUrl;
  return (
    <PublicShareShell>
      <PublicShareHeader
        icon={info.mediaKind === 'video' ? Icon.video : info.mediaKind === 'photo' ? Icon.photo : Icon.file}
        label="Публичный файл BarkCloud"
        title={info.name}
        meta={info.fileSize > 0 ? fmtSize(info.fileSize) : undefined}
      >
        <a className="btn primary" href={info.downloadPath || `/s/${token}`} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Icon.download size={18} /> Скачать
        </a>
      </PublicShareHeader>

      <div className="public-view-card">
        <div className="public-preview">
          {isVideo && info.downloadUrl ? (
            <video
              ref={persistVolumeRef}
              src={info.downloadUrl}
              poster={info.previewUrl || undefined}
              controls
            />
          ) : hasPreview ? (
            <img
              src={info.previewUrl}
              alt={info.name}
            />
          ) : (
            <Icon.file size={56} />
          )}
        </div>
        <div className="public-file-summary">
          <div>
            <h2>{info.name}</h2>
            {info.fileSize > 0 && <p>{fmtSize(info.fileSize)}</p>}
          </div>
          <a className="btn primary" href={info.downloadPath || `/s/${token}`} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
            <Icon.download size={18} /> Скачать
          </a>
        </div>
      </div>
    </PublicShareShell>
  );
}
