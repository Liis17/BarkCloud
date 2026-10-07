import React from 'react';
import { useTextFileViewer } from '../hooks/useTextFileViewer';
import { getTextFileKind, textContentUrl } from '../lib/textFiles';
import { useNavigate, useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { describePublicFile, fmtSize } from '../components/public/publicFile';
import { usePublicShareActions } from '../components/public/usePublicShareActions';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { persistVolumeRef } from '../lib/volume';
import { VideoPlayer } from '../components/media/MediaPlayer';

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

/** Публичная страница просмотра файла по шаринг-ссылке (/v/:token). Без авторизации. */
export function PublicViewPage() {
  const { token } = useParams<{ token: string }>();
  const navigate = useNavigate();
  const [state, setState] = React.useState<'loading' | 'notfound' | ShareInfo>('loading');
  const { openTextFile, textViewer } = useTextFileViewer();
  const headInfo = typeof state === 'object' ? state : null;
  const headTitle = headInfo ? headInfo.name : state === 'notfound' ? 'Ссылка недоступна' : 'Публичный файл';
  const headIconUrl = headInfo?.previewUrl || null;
  const { copyLink, copyImage, toastNode } = usePublicShareActions(headInfo?.downloadUrl || headInfo?.previewUrl);

  useDocumentHead(
    () => ({ title: headTitle, iconUrl: headIconUrl }),
    [headTitle, headIconUrl],
  );

  React.useEffect(() => {
    let alive = true;
    // Намеренно plain fetch (не авторизованный api(), который редиректит на /login при 401).
    fetch(`/s/${token}/info`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('not found'))))
      .then((d: ShareInfo) => {
        if (!alive) return;
        if (d.found && d.mediaKind === 'audio') {
          navigate(`/m/${token}`, { replace: true });
          return;
        }
        setState(d.found ? d : 'notfound');
      })
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
  }, [navigate, token]);

  if (state === 'loading') {
    return <PublicStatus title="Открываем файл" loading />;
  }
  if (state === 'notfound') {
    return (
      <PublicStatus
        title="Ссылка недоступна"
        text="Файл не найден или владелец отозвал доступ."
      />
    );
  }

  const info = state;
  const isPhoto = info.mediaKind === 'photo';
  const isVideo = info.mediaKind === 'video';
  const file = describePublicFile(info.name, info.mediaKind);
  const downloadHref = info.downloadPath || `/s/${token}`;
  const isText = !!getTextFileKind(info.name);
  return (
    <PublicShareShell>
      <PublicShareHeader
        variant="file"
        icon={isVideo ? Icon.video : isPhoto ? Icon.photo : Icon.file}
        tone={file.tone}
        label="Публичный файл"
        title={info.name}
        chips={[file.typeLabel, info.fileSize > 0 && fmtSize(info.fileSize)]}
      >
        <button className="public-fab public-st" type="button" title="Копировать ссылку" aria-label="Копировать ссылку" onClick={copyLink}>
          <Icon.link size={24} />
        </button>
        {isPhoto && (
          <button className="public-fab public-st" type="button" title="Скопировать изображение" aria-label="Скопировать изображение" onClick={copyImage}>
            <Icon.copy size={24} />
          </button>
        )}
        <a className="public-pill is-lg public-st" href={downloadHref}>
          <Icon.download size={24} />
          Скачать
        </a>
      </PublicShareHeader>

      {isText ? (
        <section className="public-nopreview">
          <button className="btn primary" onClick={() => openTextFile({ name: info.name, size: info.fileSize,
            contentUrl: textContentUrl({ kind: 'public', token: token! }), onDownload: () => { window.location.href = downloadHref; } })}>
            <Icon.eye size={20} /> Открыть файл
          </button>
        </section>
      ) : isVideo || isPhoto ? (
        <section className="public-stage">
          <div className={'public-stage-media' + (isVideo ? ' is-video' : '')}>
            {isVideo && info.downloadUrl ? (
              <VideoPlayer
                onMediaRef={persistVolumeRef}
                src={info.downloadUrl}
                poster={info.previewUrl || undefined}
                bigPlay
              />
            ) : info.previewUrl ? (
              <img src={info.previewUrl} alt={info.name} />
            ) : (
              <div className="public-stage-empty public-ph">
                <span className="public-ph-tile">{isVideo ? <Icon.video size={36} /> : <Icon.photo size={36} />}</span>
              </div>
            )}
          </div>
        </section>
      ) : (
        <section className="public-nopreview">
          <div className="public-sheet">
            <div className="public-sheet-body">
              <span style={{ width: '70%' }} />
              <span style={{ width: '100%' }} />
              <span style={{ width: '100%' }} />
              <span style={{ width: '85%' }} />
              <span style={{ width: '60%' }} />
            </div>
            <span className="public-sheet-badge">{file.ext || 'ФАЙЛ'}</span>
          </div>
          <div>
            <h2>Предпросмотр недоступен</h2>
            <p>Файл открывается после скачивания.</p>
          </div>
        </section>
      )}
      {toastNode}
      {textViewer}
    </PublicShareShell>
  );
}
