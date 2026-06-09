import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { useDocumentHead } from '../hooks/useDocumentHead';

interface ShareInfo {
  found: boolean;
  name: string;
  mediaKind: string;
  previewUrl: string;
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

const wrap: React.CSSProperties = {
  minHeight: '100vh',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  padding: 24,
  background: 'var(--md-surface, #101014)',
};
const card: React.CSSProperties = {
  width: 'min(560px, 100%)',
  background: 'var(--md-surface-container, #1b1b22)',
  color: 'var(--md-on-surface, #e6e6ea)',
  borderRadius: 16,
  padding: 28,
  textAlign: 'center',
  boxShadow: '0 12px 48px rgba(0,0,0,.35)',
};

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
    return (
      <div style={wrap}>
        <div style={card}>
          <span className="spinner" />
        </div>
      </div>
    );
  }
  if (state === 'notfound') {
    return (
      <div style={wrap}>
        <div style={card}>
          <Icon.link size={40} />
          <h2 style={{ margin: '12px 0 6px' }}>Ссылка недоступна</h2>
          <p style={{ color: 'var(--md-on-surface-variant, #9a9aa6)' }}>Файл не найден или ссылка была отозвана.</p>
        </div>
      </div>
    );
  }

  const info = state;
  const hasPreview = (info.mediaKind === 'photo' || info.mediaKind === 'video') && !!info.previewUrl;
  return (
    <div style={wrap}>
      <div style={card}>
        {hasPreview ? (
          <div style={{ position: 'relative', marginBottom: 18 }}>
            <img
              src={info.previewUrl}
              alt={info.name}
              style={{ maxWidth: '100%', maxHeight: '60vh', borderRadius: 12, display: 'block', margin: '0 auto' }}
            />
            {info.mediaKind === 'video' && (
              <div
                style={{
                  position: 'absolute',
                  inset: 0,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  pointerEvents: 'none',
                }}
              >
                <Icon.play size={48} />
              </div>
            )}
          </div>
        ) : (
          <div style={{ marginBottom: 18 }}>
            <Icon.file size={56} />
          </div>
        )}
        <h2 style={{ margin: '0 0 6px', wordBreak: 'break-word' }}>{info.name}</h2>
        {info.fileSize > 0 && (
          <div style={{ color: 'var(--md-on-surface-variant, #9a9aa6)', marginBottom: 18 }}>{fmtSize(info.fileSize)}</div>
        )}
        <a
          className="btn primary"
          href={info.downloadPath || `/s/${token}`}
          style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}
        >
          <Icon.download size={18} /> Скачать
        </a>
      </div>
    </div>
  );
}
