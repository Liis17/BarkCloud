import React from 'react';
import { Icon } from '../Icon';
import { VideoPlayer } from '../media/MediaPlayer';
import type { VideoQuality } from '../media/MediaPlayer';
import { persistVolumeRef } from '../../lib/volume';
import { fmtSize } from './publicFile';
import { usePublicShareActions } from './usePublicShareActions';
import { isSvgFileName } from '../../lib/imagePreviewSource';
import { useViewerGestures } from '../../hooks/useViewerGestures';

export interface PublicViewerFile {
  name: string;
  mediaKind: string;
  downloadUrl: string;
  previewUrl?: string;
  fileSize: number;
  videoQualities?: VideoQuality[];
}

interface Props<T extends PublicViewerFile> {
  file: T;
  /** Фото и видео страницы по порядку: листание кнопками, стрелками и свайпом. */
  items?: T[];
  /** Показанный файл сменился листанием: страница обновляет заголовок вкладки. */
  onChange?: (file: T) => void;
  onClose: () => void;
}

/** Касания на панелях и кнопках не становятся жестами просмотра. */
const GESTURE_IGNORE = '.public-viewer-top, .public-viewer-actions, .video-player-bar, button, a, input, select';

/** Оверлей просмотра фото/видео на публичных страницах папки и альбома.
 *  Урезанные действия без авторизации: скачать, скопировать изображение (фото), копировать ссылку на страницу.
 *  Листание стрелками (на видео — Shift+стрелки) и свайпом, щипок зумит фото — как в Lightbox. */
export function PublicViewer<T extends PublicViewerFile>({ file: initial, items, onChange, onClose }: Props<T>) {
  const list = items && items.includes(initial) ? items : [initial];
  const [i, setI] = React.useState(() => list.indexOf(initial));
  const file = list[Math.min(i, list.length - 1)];
  const isVideo = file.mediaKind === 'video';
  const isSvg = isSvgFileName(file.name);
  const imageSrc = isVideo ? null : isSvg ? file.previewUrl || null : file.downloadUrl || null;
  const { copyLink, copyImage, toastNode } = usePublicShareActions(imageSrc || undefined);
  const [scale, setScale] = React.useState(1);
  const [pan, setPan] = React.useState({ x: 0, y: 0 });
  const rootRef = React.useRef<HTMLDivElement | null>(null);

  const go = React.useCallback(
    (delta: number) => setI((prev) => Math.min(list.length - 1, Math.max(0, prev + delta))),
    [list.length],
  );
  const onChangeRef = React.useRef(onChange);
  onChangeRef.current = onChange;
  React.useEffect(() => {
    setScale(1);
    setPan({ x: 0, y: 0 });
    if (file !== initial) onChangeRef.current?.(file);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [i]);
  useViewerGestures(rootRef, { ignore: GESTURE_IGNORE, zoomable: !isVideo && !!imageSrc, scale, pan, setScale, setPan, go });

  React.useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
        return;
      }
      if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
      // На видео стрелки перематывают плеер, листание — Shift+стрелки; слайдеры плеера обрабатывают стрелки сами.
      if (isVideo ? !e.shiftKey : e.target instanceof HTMLInputElement) return;
      e.preventDefault();
      go(e.key === 'ArrowLeft' ? -1 : 1);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, isVideo, go]);

  const hint = isVideo ? 'Shift+' : '';
  return (
    <div className="public-viewer" ref={rootRef} role="dialog" aria-label={isVideo ? 'Просмотр видео' : 'Просмотр фото'} onClick={onClose}>
      <div className="public-viewer-top">
        <div className="public-viewer-name">
          <div>{file.name}</div>
          <div>
            {list.length > 1 && `${i + 1} из ${list.length}`}
            {list.length > 1 && file.fileSize > 0 && ' · '}
            {file.fileSize > 0 && fmtSize(file.fileSize)}
          </div>
        </div>
        <button className="public-viewer-close public-st" type="button" title="Закрыть" aria-label="Закрыть" onClick={onClose}>
          <Icon.x size={24} />
        </button>
      </div>
      <div className="public-viewer-stage">
        {i > 0 && (
          <button className="public-viewer-nav is-prev public-st" type="button" title={`Назад (${hint}←)`} aria-label="Предыдущий" onClick={(e) => { e.stopPropagation(); go(-1); }}>
            <Icon.chev size={28} />
          </button>
        )}
        <div className="public-viewer-media" onClick={(e) => e.stopPropagation()}>
          {isVideo ? (
            <VideoPlayer key={file.downloadUrl} onMediaRef={persistVolumeRef} src={file.downloadUrl} qualities={file.videoQualities} autoPlay />
          ) : imageSrc ? (
            <img
              src={imageSrc}
              alt={file.name}
              draggable={false}
              style={scale > 1 ? { transform: `translate(${pan.x}px, ${pan.y}px) scale(${scale})` } : undefined}
            />
          ) : (
            <div className="public-viewer-placeholder">
              {isSvg ? 'Безопасное превью недоступно' : 'Предпросмотр недоступен'}
            </div>
          )}
        </div>
        {i < list.length - 1 && (
          <button className="public-viewer-nav is-next public-st" type="button" title={`Вперёд (${hint}→)`} aria-label="Следующий" onClick={(e) => { e.stopPropagation(); go(1); }}>
            <Icon.chev size={28} />
          </button>
        )}
      </div>
      <div className="public-viewer-actions" aria-label="Действия просмотрщика" onClick={(e) => e.stopPropagation()}>
        <a className="public-pill public-st" href={file.downloadUrl} download={file.name}>
          <Icon.download size={20} />
          Скачать
        </a>
        {file.mediaKind === 'photo' && imageSrc && (
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
