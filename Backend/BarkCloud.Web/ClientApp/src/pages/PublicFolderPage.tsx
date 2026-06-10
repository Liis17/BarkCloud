import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { useDocumentHead } from '../hooks/useDocumentHead';

interface PubDir {
  id: string;
  name: string;
}
interface PubFile {
  fileId: string;
  name: string;
  mediaKind: string;
  downloadUrl: string;
  previewUrl: string;
  fileSize: number;
  imageWidth: number;
  imageHeight: number;
}
interface Listing {
  found: boolean;
  folderName: string;
  currentDir: string;
  currentName: string;
  subdirs: PubDir[];
  files: PubFile[];
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
  height: '100vh',
  overflowY: 'auto',
  background: 'var(--md-surface, #101014)',
  color: 'var(--md-on-surface, #e6e6ea)',
};
const inner: React.CSSProperties = { maxWidth: 1100, margin: '0 auto', padding: '24px 20px 64px' };

/** Публичная страница папки по шаринг-ссылке (/f/:token). Без авторизации; контент динамический. */
export function PublicFolderPage() {
  const { token } = useParams<{ token: string }>();
  // Стек навигации внутри расшаренной папки: первый элемент — корень (id '').
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>([{ id: '', name: '' }]);
  const [data, setData] = React.useState<Listing | null>(null);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [viewer, setViewer] = React.useState<PubFile | null>(null);

  const here = stack[stack.length - 1];
  const folderTitle = data?.currentName || here.name || data?.folderName || 'Публичная папка';
  const headTitle = viewer?.name || (state === 'notfound' ? 'Папка недоступна' : folderTitle);
  const headIconUrl = viewer?.previewUrl || null;

  useDocumentHead(
    () => ({ title: headTitle, iconUrl: headIconUrl }),
    [headTitle, headIconUrl],
  );

  React.useEffect(() => {
    let alive = true;
    setState('loading');
    fetch(`/f/${token}/list?dir=${encodeURIComponent(here.id)}`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('not found'))))
      .then((d: Listing) => {
        if (!alive) return;
        if (!d.found) {
          setState('notfound');
          return;
        }
        setData(d);
        setState('ok');
        // Подставим имя корня в стек, когда оно стало известно.
        setStack((s) => (s.length === 1 && !s[0].name ? [{ id: '', name: d.folderName }] : s));
      })
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
  }, [token, here.id]);

  function openFile(f: PubFile) {
    if (f.mediaKind === 'photo' || f.mediaKind === 'video') setViewer(f);
    else if (f.downloadUrl) window.location.href = f.downloadUrl;
  }

  if (state === 'loading') {
    return (
      <div style={{ ...wrap, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <span className="spinner" />
      </div>
    );
  }
  if (state === 'notfound') {
    return (
      <div style={{ ...wrap, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24, textAlign: 'center' }}>
        <div>
          <Icon.folder size={44} />
          <h2 style={{ margin: '12px 0 6px' }}>Папка недоступна</h2>
          <p style={{ color: 'var(--md-on-surface-variant, #9a9aa6)' }}>Папка не найдена или ссылка была отозвана.</p>
        </div>
      </div>
    );
  }

  const d = data!;
  return (
    <div style={wrap}>
      <div style={inner}>
        <div className="breadcrumb" style={{ marginBottom: 18, fontSize: 15 }}>
          {stack.map((s, i) => (
            <React.Fragment key={s.id || 'root'}>
              {i > 0 && <span className="sep">/</span>}
              {i === stack.length - 1 ? (
                <span className="cur" style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
                  <Icon.folder size={18} /> {s.name || d.folderName}
                </span>
              ) : (
                <a style={{ cursor: 'pointer', display: 'inline-flex', alignItems: 'center', gap: 6 }} onClick={() => setStack((st) => st.slice(0, i + 1))}>
                  {i === 0 ? <Icon.folder size={18} /> : null} {s.name || d.folderName}
                </a>
              )}
            </React.Fragment>
          ))}
        </div>

        {d.subdirs.length === 0 && d.files.length === 0 ? (
          <p style={{ color: 'var(--md-on-surface-variant, #9a9aa6)' }}>Папка пуста.</p>
        ) : (
          <div className="pubfolder-grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 14 }}>
            {d.subdirs.map((sd) => (
              <button
                key={sd.id}
                onClick={() => setStack((st) => [...st, { id: sd.id, name: sd.name }])}
                style={{
                  display: 'flex',
                  flexDirection: 'column',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: 8,
                  aspectRatio: '1 / 1',
                  borderRadius: 12,
                  border: '1px solid var(--md-outline-variant, #333)',
                  background: 'var(--md-surface-container, #1b1b22)',
                  color: 'inherit',
                  cursor: 'pointer',
                  padding: 12,
                }}
              >
                <Icon.folder size={40} />
                <span style={{ fontSize: 13, textAlign: 'center', wordBreak: 'break-word' }}>{sd.name}</span>
              </button>
            ))}
            {d.files.map((f) => {
              const isMedia = f.mediaKind === 'photo' || f.mediaKind === 'video';
              return (
                <button
                  key={f.fileId}
                  onClick={() => openFile(f)}
                  title={f.name}
                  style={{
                    display: 'flex',
                    flexDirection: 'column',
                    borderRadius: 12,
                    overflow: 'hidden',
                    border: '1px solid var(--md-outline-variant, #333)',
                    background: 'var(--md-surface-container, #1b1b22)',
                    color: 'inherit',
                    cursor: 'pointer',
                    padding: 0,
                  }}
                >
                  <div style={{ position: 'relative', aspectRatio: '1 / 1', background: 'var(--md-surface-container-high, #25252e)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                    {isMedia && f.previewUrl ? (
                      <img src={f.previewUrl} alt={f.name} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                    ) : (
                      <Icon.file size={40} />
                    )}
                    {f.mediaKind === 'video' && (
                      <span style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', pointerEvents: 'none' }}>
                        <Icon.play size={34} />
                      </span>
                    )}
                  </div>
                  <div style={{ padding: '8px 10px', textAlign: 'left' }}>
                    <div style={{ fontSize: 13, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{f.name}</div>
                    {f.fileSize > 0 && <div style={{ fontSize: 11, color: 'var(--md-on-surface-variant, #9a9aa6)' }}>{fmtSize(f.fileSize)}</div>}
                  </div>
                </button>
              );
            })}
          </div>
        )}
      </div>

      {viewer && (
        <div
          onClick={() => setViewer(null)}
          style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,.85)', display: 'flex', alignItems: 'center', justifyContent: 'center', overflowY: 'auto', padding: 24, zIndex: 50 }}
        >
          <div onClick={(e) => e.stopPropagation()} style={{ maxWidth: '92vw', maxHeight: '92vh', textAlign: 'center' }}>
            {viewer.mediaKind === 'video' ? (
              <video src={viewer.downloadUrl} controls autoPlay style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8 }} />
            ) : (
              <img src={viewer.downloadUrl} alt={viewer.name} style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8, objectFit: 'contain' }} />
            )}
            <div style={{ marginTop: 12, display: 'flex', gap: 10, justifyContent: 'center' }}>
              <a className="btn primary" href={viewer.downloadUrl} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <Icon.download size={16} /> Скачать
              </a>
              <button className="btn outlined" onClick={() => setViewer(null)}>
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
