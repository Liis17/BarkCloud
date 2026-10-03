import React from 'react';
import { Icon } from '../Icon';
import { VideoPlayer } from '../media/MediaPlayer';
import { persistVolumeRef } from '../../lib/volume';
import { fmtSize } from './publicFile';
import { usePublicShareActions } from './usePublicShareActions';

export interface PublicViewerFile {
  name: string;
  mediaKind: string;
  downloadUrl: string;
  fileSize: number;
}

interface Props {
  file: PublicViewerFile;
  onClose: () => void;
}

/** Оверлей просмотра фото/видео на публичных страницах папки и альбома.
 *  Урезанные действия без авторизации: скачать, скопировать изображение (фото), копировать ссылку на страницу. */
export function PublicViewer({ file, onClose }: Props) {
  const { copyLink, copyImage, toastNode } = usePublicShareActions(file.downloadUrl);
  const isVideo = file.mediaKind === 'video';

  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="public-viewer" role="dialog" aria-label={isVideo ? 'Просмотр видео' : 'Просмотр фото'} onClick={onClose}>
      <div className="public-viewer-top">
        <div className="public-viewer-name">
          <div>{file.name}</div>
          {file.fileSize > 0 && <div>{fmtSize(file.fileSize)}</div>}
        </div>
        <button className="public-viewer-close public-st" type="button" title="Закрыть" aria-label="Закрыть" onClick={onClose}>
          <Icon.x size={24} />
        </button>
      </div>
      <div className="public-viewer-stage">
        <div className="public-viewer-media" onClick={(e) => e.stopPropagation()}>
          {isVideo ? (
            <VideoPlayer onMediaRef={persistVolumeRef} src={file.downloadUrl} autoPlay />
          ) : (
            <img src={file.downloadUrl} alt={file.name} />
          )}
        </div>
      </div>
      <div className="public-viewer-actions" aria-label="Действия просмотрщика" onClick={(e) => e.stopPropagation()}>
        <a className="public-pill public-st" href={file.downloadUrl} download={file.name}>
          <Icon.download size={20} />
          Скачать
        </a>
        {file.mediaKind === 'photo' && (
          <button className="public-viewer-btn public-st" type="button" title="Скопировать изображение" aria-label="Скопировать изображение" onClick={copyImage}>
            <Icon.copy size={24} />
          </button>
        )}
        <button className="public-viewer-btn public-st" type="button" title="Копировать ссылку" aria-label="Копировать ссылку" onClick={copyLink}>
          <Icon.link size={24} />
        </button>
      </div>
      {toastNode}
    </div>
  );
}
