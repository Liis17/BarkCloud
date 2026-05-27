import React from 'react';
import { Icon } from '../Icon';
import type { CardFile } from '../../lib/types';

interface MediaThumbProps {
  media: Pick<CardFile, 'previews' | 'kind'> | null | undefined;
  sizes?: string;
  className?: string;
}

/** Превью медиа. Браузер сам выбирает ширину под размер блока (srcset + sizes + DPR).
 *  Пока превью не загрузилось (или его нет) — MD3-иконка-плейсхолдер по типу медиа. */
export function MediaThumb({ media, sizes = '200px', className = 'thumb' }: MediaThumbProps) {
  const previews = (media && media.previews) || [];
  const kind = media && media.kind;
  const PhIcon = kind === 'video' ? Icon.video : kind === 'photo' ? Icon.photo : Icon.file;
  const tint = (kind === 'video'
    ? { '--tint-a': '#9FB4D6', '--tint-b': '#3F5374' }
    : { '--tint-a': '#C8A78C', '--tint-b': '#6F4A3A' }) as React.CSSProperties;
  const [loaded, setLoaded] = React.useState(false);

  const placeholder = (
    <div className={'thumb-ph' + (loaded ? ' off' : '')} aria-hidden="true">
      <PhIcon size={34} />
    </div>
  );

  if (!previews.length) {
    return (
      <div className={className} style={tint}>
        {placeholder}
      </div>
    );
  }
  const srcSet = previews.map((p) => `${p.url} ${p.w}w`).join(', ');
  const fallback = previews[previews.length - 1].url; // самое широкое
  return (
    <div className={className} style={tint}>
      {placeholder}
      <img
        className={'thumb-img' + (loaded ? ' on' : '')}
        src={fallback}
        srcSet={srcSet}
        sizes={sizes}
        alt=""
        loading="lazy"
        onLoad={() => setLoaded(true)}
      />
    </div>
  );
}
