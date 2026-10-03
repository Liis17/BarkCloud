import { Icon } from '../Icon';
import { describePublicFile, fmtSize, isPublicMedia } from './publicFile';

export interface PublicTileFile {
  fileId: string;
  name: string;
  mediaKind: string;
  previewUrl: string;
  fileSize: number;
}

interface Props {
  file: PublicTileFile;
  /** Без карточки вокруг (сетка альбома); по умолчанию — карточка (папка). */
  plain?: boolean;
  onOpen: () => void;
}

/** Плитка файла на публичных страницах папки и альбома: превью фото/видео либо тональная плитка с расширением. */
export function PublicFileTile({ file, plain, onOpen }: Props) {
  const media = isPublicMedia(file.mediaKind);
  const info = describePublicFile(file.name, file.mediaKind);
  const isVideo = file.mediaKind === 'video';
  const TileIcon = isVideo ? Icon.video : file.mediaKind === 'photo' ? Icon.photo : file.mediaKind === 'audio' ? Icon.music : Icon.file;
  return (
    <button type="button" className={'public-tile public-st' + (plain ? ' is-plain' : '')} title={file.name} onClick={onOpen}>
      {media ? (
        <div className="public-tile-media public-ph">
          {file.previewUrl ? (
            <img src={file.previewUrl} alt="" loading="lazy" />
          ) : (
            <>
              {!isVideo && <TileIcon size={32} />}
              <span className="public-tile-kind">{isVideo ? 'ВИДЕО' : 'ФОТО'}</span>
            </>
          )}
          {isVideo && (
            <span className="public-tile-play">
              <Icon.play size={26} />
            </span>
          )}
        </div>
      ) : (
        <div className={'public-tile-media is-file tone-' + info.tone}>
          <TileIcon size={44} />
          {info.ext && <span>{info.ext}</span>}
        </div>
      )}
      <div className="public-tile-title">{file.name}</div>
      <div className="public-tile-sub">{file.fileSize > 0 ? fmtSize(file.fileSize) : ''}</div>
    </button>
  );
}
