import React from 'react';
import { useTextFileViewer } from '../hooks/useTextFileViewer';
import { textContentUrl } from '../lib/textFiles';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { PublicFileTile } from '../components/public/PublicTile';
import { PublicViewer } from '../components/public/PublicViewer';
import { countLabel, isPublicMedia } from '../components/public/publicFile';
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

/** Публичная страница папки по шаринг-ссылке (/f/:token). Без авторизации; контент динамический. */
export function PublicFolderPage() {
  const { token } = useParams<{ token: string }>();
  // Стек навигации внутри расшаренной папки: первый элемент — корень (id '').
  const [stack, setStack] = React.useState<{ id: string; name: string }[]>([{ id: '', name: '' }]);
  const [data, setData] = React.useState<Listing | null>(null);
  const [state, setState] = React.useState<'loading' | 'notfound' | 'ok'>('loading');
  const [viewer, setViewer] = React.useState<PubFile | null>(null);
  const { openTextFile, textViewer } = useTextFileViewer();

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
    if (openTextFile({ name: f.name, size: f.fileSize, contentUrl: textContentUrl({ kind: 'publicFolder', token: token!, dir: here.id, fileId: f.fileId }), onDownload: () => { if (f.downloadUrl) window.location.href = f.downloadUrl; } })) return;
    if (isPublicMedia(f.mediaKind)) setViewer(f);
    else if (f.downloadUrl) window.location.href = f.downloadUrl;
  }

  if (state === 'loading') {
    return <PublicStatus title="Открываем папку" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        title="Папка недоступна"
        text="Папка не найдена или владелец отозвал доступ."
      />
    );
  }

  const d = data!;
  return (
    <PublicShareShell>
      <PublicShareHeader
        variant="folder"
        icon={Icon.folder}
        label="Публичная папка"
        title={d.currentName || d.folderName}
        subtitle="Содержимое открывается прямо по ссылке, без входа в аккаунт."
        chips={[
          d.subdirs.length > 0 && countLabel(d.subdirs.length, 'папка', 'папки', 'папок'),
          countLabel(d.files.length, 'файл', 'файла', 'файлов'),
        ]}
      />

      <nav className="public-breadcrumb" aria-label="Путь к папке">
        {stack.map((s, i) => (
          <React.Fragment key={s.id || 'root'}>
            {i > 0 && <Icon.chev size={18} />}
            {i === stack.length - 1 ? (
              <span className="public-crumb is-current" aria-current="page">
                {s.name || d.folderName}
              </span>
            ) : (
              <button type="button" className="public-crumb public-st" onClick={() => setStack((st) => st.slice(0, i + 1))}>
                {i === 0 && <Icon.folder size={20} />}
                {s.name || d.folderName}
              </button>
            )}
          </React.Fragment>
        ))}
      </nav>

      {d.subdirs.length === 0 && d.files.length === 0 ? (
        <div className="public-empty">Папка пуста.</div>
      ) : (
        <section className="public-grid" aria-label="Содержимое публичной папки">
          {d.subdirs.map((sd) => (
            <button
              key={sd.id}
              type="button"
              className="public-tile public-st"
              onClick={() => setStack((st) => [...st, { id: sd.id, name: sd.name }])}
            >
              <div className="public-tile-media is-file tone-primary">
                <Icon.folder size={48} />
              </div>
              <div className="public-tile-title">{sd.name}</div>
              <div className="public-tile-sub">Папка</div>
            </button>
          ))}
          {d.files.map((f) => (
            <PublicFileTile key={f.fileId} file={f} onOpen={() => openFile(f)} />
          ))}
        </section>
      )}

      {viewer && <PublicViewer file={viewer} onClose={() => setViewer(null)} />}
      {textViewer}
    </PublicShareShell>
  );
}
