import React from 'react';
import { Modal } from './Modal';
import { Icon } from '../Icon';
import { Loading } from './EmptyState';
import { apiGet } from '../../lib/api';

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
}
interface Listing {
  found: boolean;
  directoryId: string;
  name: string;
  subdirs: PubDir[];
  files: PubFile[];
}

/** Браузер папки, доступной мне по гранту: навигация по поддереву + просмотр/скачивание файлов. */
export function SharedFolderModal({ rootDirId, rootName, onClose }: { rootDirId: string; rootName: string; onClose: () => void }) {
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>([{ id: rootDirId, name: rootName }]);
  const [data, setData] = React.useState<Listing | null>(null);
  const [error, setError] = React.useState(false);
  const [viewer, setViewer] = React.useState<PubFile | null>(null);

  const here = stack[stack.length - 1];

  React.useEffect(() => {
    let alive = true;
    setData(null);
    setError(false);
    apiGet<Listing>('/api/shared/dir?dir=' + encodeURIComponent(here.id))
      .then((d) => alive && (d.found ? setData(d) : setError(true)))
      .catch(() => alive && setError(true));
    return () => {
      alive = false;
    };
  }, [here.id]);

  function openFile(f: PubFile) {
    if (f.mediaKind === 'photo' || f.mediaKind === 'video') setViewer(f);
    else if (f.downloadUrl) window.open(f.downloadUrl, '_blank');
  }

  return (
    <Modal title={rootName} wide onClose={onClose}>
      <div className="breadcrumb" style={{ marginBottom: 12 }}>
        {stack.map((s, i) => (
          <React.Fragment key={s.id}>
            {i > 0 && <span className="sep">/</span>}
            {i === stack.length - 1 ? (
              <span className="cur">{s.name}</span>
            ) : (
              <a style={{ cursor: 'pointer' }} onClick={() => setStack((st) => st.slice(0, i + 1))}>
                {s.name}
              </a>
            )}
          </React.Fragment>
        ))}
      </div>

      {error ? (
        <p style={{ color: 'var(--md-on-surface-variant)', fontSize: 13 }}>Папка недоступна или была отозвана.</p>
      ) : data === null ? (
        <Loading />
      ) : data.subdirs.length === 0 && data.files.length === 0 ? (
        <p style={{ color: 'var(--md-on-surface-variant)', fontSize: 13 }}>Папка пуста.</p>
      ) : (
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(130px, 1fr))', gap: 12, maxHeight: '60vh', overflowY: 'auto' }}>
          {data.subdirs.map((sd) => (
            <button
              key={sd.id}
              onClick={() => setStack((st) => [...st, { id: sd.id, name: sd.name }])}
              style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8, aspectRatio: '1 / 1', borderRadius: 12, border: '1px solid var(--md-outline-variant)', background: 'var(--md-surface-container)', color: 'inherit', cursor: 'pointer', padding: 12, justifyContent: 'center' }}
            >
              <Icon.folder size={36} />
              <span style={{ fontSize: 12, textAlign: 'center', wordBreak: 'break-word' }}>{sd.name}</span>
            </button>
          ))}
          {data.files.map((f) => {
            const isMedia = f.mediaKind === 'photo' || f.mediaKind === 'video';
            return (
              <button
                key={f.fileId}
                onClick={() => openFile(f)}
                title={f.name}
                style={{ display: 'flex', flexDirection: 'column', borderRadius: 12, overflow: 'hidden', border: '1px solid var(--md-outline-variant)', background: 'var(--md-surface-container)', color: 'inherit', cursor: 'pointer', padding: 0 }}
              >
                <div style={{ position: 'relative', aspectRatio: '1 / 1', background: 'var(--md-surface-container-high)', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  {isMedia && f.previewUrl ? (
                    <img src={f.previewUrl} alt={f.name} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                  ) : (
                    <Icon.file size={34} />
                  )}
                  {f.mediaKind === 'video' && (
                    <span style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', pointerEvents: 'none' }}>
                      <Icon.play size={28} />
                    </span>
                  )}
                </div>
                <div style={{ padding: '6px 8px', fontSize: 12, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', textAlign: 'left' }}>{f.name}</div>
              </button>
            );
          })}
        </div>
      )}

      {viewer && (
        <div onClick={() => setViewer(null)} style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,.85)', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24, zIndex: 60 }}>
          <div onClick={(e) => e.stopPropagation()} style={{ maxWidth: '92vw', maxHeight: '92vh', textAlign: 'center' }}>
            {viewer.mediaKind === 'video' ? (
              <video src={viewer.downloadUrl} controls autoPlay style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8 }} />
            ) : (
              <img src={viewer.downloadUrl} alt={viewer.name} style={{ maxWidth: '92vw', maxHeight: '80vh', borderRadius: 8, objectFit: 'contain' }} />
            )}
            <div style={{ marginTop: 12, display: 'flex', gap: 10, justifyContent: 'center' }}>
              <a className="btn primary" href={viewer.downloadUrl} target="_blank" rel="noreferrer" style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                <Icon.download size={16} /> Скачать
              </a>
              <button className="btn outlined" onClick={() => setViewer(null)}>
                Закрыть
              </button>
            </div>
          </div>
        </div>
      )}
    </Modal>
  );
}
