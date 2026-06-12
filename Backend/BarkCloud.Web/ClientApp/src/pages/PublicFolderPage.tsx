import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { persistVolumeRef } from '../lib/volume';

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
    return <PublicStatus icon={Icon.folder} title="Открываем папку" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        icon={Icon.folder}
        title="Папка недоступна"
        text="Папка не найдена или владелец отозвал доступ."
      />
    );
  }

  const d = data!;
  const meta = `${d.subdirs.length} папок · ${d.files.length} файлов`;
  return (
    <PublicShareShell>
      <PublicShareHeader
        icon={Icon.folder}
        label="Публичная папка BarkCloud"
        title={d.currentName || d.folderName}
        subtitle="Содержимое открывается прямо по ссылке, без входа в аккаунт."
        meta={meta}
      />

      <div className="public-breadcrumb">
          {stack.map((s, i) => (
            <React.Fragment key={s.id || 'root'}>
              {i > 0 && <span className="sep">/</span>}
              {i === stack.length - 1 ? (
                <span className="cur">
                  <Icon.folder size={18} /> {s.name || d.folderName}
                </span>
              ) : (
                <a onClick={() => setStack((st) => st.slice(0, i + 1))}>
                  {i === 0 ? <Icon.folder size={18} /> : null} {s.name || d.folderName}
                </a>
              )}
            </React.Fragment>
          ))}
      </div>

        {d.subdirs.length === 0 && d.files.length === 0 ? (
          <div className="public-empty">Папка пуста.</div>
        ) : (
          <div className="public-grid">
            {d.subdirs.map((sd) => (
              <button
                key={sd.id}
                onClick={() => setStack((st) => [...st, { id: sd.id, name: sd.name }])}
                className="public-tile"
              >
                <div className="public-tile-media">
                  <Icon.folder size={44} />
                </div>
                <div className="public-tile-title">{sd.name}</div>
                <div className="public-tile-sub">Папка</div>
              </button>
            ))}
            {d.files.map((f) => {
              const isMedia = f.mediaKind === 'photo' || f.mediaKind === 'video';
              return (
                <button
                  key={f.fileId}
                  onClick={() => openFile(f)}
                  title={f.name}
                  className="public-tile"
                >
                  <div className="public-tile-media">
                    {isMedia && f.previewUrl ? (
                      <img src={f.previewUrl} alt={f.name} />
                    ) : (
                      <Icon.file size={40} />
                    )}
                    {f.mediaKind === 'video' && (
                      <span className="public-play">
                        <Icon.play size={34} />
                      </span>
                    )}
                  </div>
                  <div className="public-tile-title">{f.name}</div>
                  <div className="public-tile-sub">{f.fileSize > 0 ? fmtSize(f.fileSize) : ''}</div>
                </button>
              );
            })}
          </div>
        )}

      {viewer && (
        <div
          onClick={() => setViewer(null)}
          className="public-viewer"
        >
          <div onClick={(e) => e.stopPropagation()} className="public-viewer-body">
            {viewer.mediaKind === 'video' ? (
              <video ref={persistVolumeRef} src={viewer.downloadUrl} controls autoPlay />
            ) : (
              <img src={viewer.downloadUrl} alt={viewer.name} />
            )}
            <div className="public-viewer-actions">
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
    </PublicShareShell>
  );
}
