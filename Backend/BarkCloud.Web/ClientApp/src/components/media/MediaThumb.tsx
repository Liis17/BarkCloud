import React from 'react';
import { Icon } from '../Icon';
import type { CardFile, FilePlaceholder } from '../../lib/types';

interface MediaThumbProps {
  media: (Pick<CardFile, 'previews' | 'kind' | 'placeholder' | 'jpegViewUrl'> & Partial<Pick<CardFile, 'id'>>) | null | undefined;
  sizes?: string;
  className?: string;
  colorPlaceholder?: boolean;
}

export function MediaThumb({ colorPlaceholder = false, ...props }: MediaThumbProps) {
  if (colorPlaceholder && (props.media?.kind === 'photo' || props.media?.kind === 'video')) {
    const identity = JSON.stringify([props.media.id, props.media.jpegViewUrl, props.media.previews, props.sizes]);
    return <DecodedMediaThumb key={identity} {...props} />;
  }
  return <LegacyMediaThumb {...props} />;
}

/** Превью медиа. Браузер сам выбирает ширину под размер блока (srcset + sizes + DPR).
 *  Пока превью не загрузилось (или его нет) — MD3-иконка-плейсхолдер по типу медиа. */
function LegacyMediaThumb({ media, sizes = '200px', className = 'thumb' }: MediaThumbProps) {
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

function DecodedMediaThumb({ media, sizes = '200px', className = 'thumb' }: MediaThumbProps) {
  const previews = media?.previews || [];
  const fallback = previews.length ? previews[previews.length - 1].url : media?.kind === 'photo' ? media.jpegViewUrl : undefined;
  const [ready, setReady] = React.useState(false);
  const imageRef = React.useRef<HTMLImageElement>(null);
  const mounted = React.useRef(true);
  const request = React.useRef(0);

  const showDecoded = React.useCallback(async (image: HTMLImageElement) => {
    const version = ++request.current;
    const source = image.currentSrc || image.src;
    try {
      await image.decode();
      if (mounted.current && version === request.current && source === (image.currentSrc || image.src)) setReady(true);
    } catch {
      if (mounted.current && version === request.current) setReady(false);
    }
  }, []);

  React.useEffect(() => {
    mounted.current = true;
    const image = imageRef.current;
    if (image?.complete && image.naturalWidth > 0) void showDecoded(image);
    return () => { mounted.current = false; request.current++; };
  }, [showDecoded]);

  const placeholder = media?.placeholder;
  const validColors = placeholder && Array.isArray(placeholder.colors) && placeholder.colors.length === 9
    && placeholder.colors.every((color) => typeof color === 'string' && /^#[0-9a-f]{6}$/i.test(color))
    && Number.isFinite(placeholder.aspectRatio) && placeholder.aspectRatio > 0;
  const PhIcon = media?.kind === 'video' ? Icon.video : Icon.photo;
  const tint = (media?.kind === 'video'
    ? { '--tint-a': '#9FB4D6', '--tint-b': '#3F5374' }
    : { '--tint-a': '#C8A78C', '--tint-b': '#6F4A3A' }) as React.CSSProperties;

  return (
    <div className={`${className} thumb-with-placeholder`} style={tint}>
      {!ready && (validColors
        ? <ColorPlaceholder placeholder={placeholder} />
        : <div className="thumb-ph" aria-hidden="true"><PhIcon size={34} /></div>)}
      {fallback && <img
        ref={imageRef}
        className={'thumb-img' + (ready ? ' on' : '')}
        src={fallback}
        srcSet={previews.length ? previews.map((p) => `${p.url} ${p.w}w`).join(', ') : undefined}
        sizes={sizes}
        alt=""
        loading="lazy"
        decoding="async"
        onLoad={(event) => { void showDecoded(event.currentTarget); }}
        onError={() => { request.current++; setReady(false); }}
      />}
    </div>
  );
}

function ColorPlaceholder({ placeholder }: { placeholder: FilePlaceholder }) {
  const filterId = `thumb-blur-${React.useId().replace(/:/g, '')}`;
  const width = 100 * placeholder.aspectRatio;
  const height = 100;
  const side = Math.min(width, height);
  return (
    <svg className="thumb-color-ph" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="xMidYMid slice" aria-hidden="true">
      <defs>
        <filter id={filterId} x="-50%" y="-50%" width="200%" height="200%" colorInterpolationFilters="sRGB">
          <feGaussianBlur stdDeviation={side * 0.12} />
        </filter>
      </defs>
      <rect width={width} height={height} fill="#000000" />
      <g filter={`url(#${filterId})`} fillOpacity={0.9}>
        {placeholder.colors.map((color, index) => <circle
          key={index}
          cx={((index % 3) + 0.5) * width / 3}
          cy={(Math.floor(index / 3) + 0.5) * height / 3}
          r={side * 0.35}
          fill={color}
        />)}
      </g>
    </svg>
  );
}
